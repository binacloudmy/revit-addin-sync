#nullable enable
// The add-in's own confirm card (lean-harness spec §7.5; W5 Task 3). Revit-free.
//
// Built ONLY from what the add-in measured in the preview it holds:
//   - counts, deletions first and in red (touched deletions, then side-effect
//     deletions by category);
//   - additions, then before -> after values from the preview read-back;
//   - side effects by category, warnings Revit raised, preview failures;
//   - uncategorised changes (W3 reports them under "class:<ClassName>" or an
//     empty key) as one "Other settings: N" side-effect row (ruling R43),
//     counted in the hard-warning decision; red when any was deleted;
//   - tool labels the add-in owns, and run_revit_code's code in full.
// The cloud's words (the tool_request description, run_revit_code's intent)
// appear only in AssistantText, under its own label. A hard-warning style is
// used when anything is deleted or the side effects outnumber the touched set.
//
// The drafter's answer resolves Decision exactly once; cancellation (Stop, a
// new turn, a closed pane) resolves it as declined.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum ConfirmRowKind { Delete, Add, Modify, SideEffect, Warning, Failure }

    public sealed class ConfirmRow
    {
        public ConfirmRowKind Kind;
        public string Text = "";
        /// <summary>Rendered in the danger (red) colour.</summary>
        public bool Danger;
    }

    public sealed class ConfirmCardModel
    {
        private const int MaxModifyGroups = 4;
        private const int MaxWarnings = 5;

        public string BatchId = "";
        public string PlanHash = "";
        public ReceiptLanguage Language;
        /// <summary>Counts only, e.g. "Delete 7 · Change 34 elements".</summary>
        public string Headline = "";
        /// <summary>Add-in-owned label of the tools in the batch.</summary>
        public string ToolLabel = "";
        public List<ConfirmRow> Rows = new List<ConfirmRow>();
        /// <summary>run_revit_code's code, verbatim; null for typed writes.</summary>
        public string? Code;
        /// <summary>Cloud/model words, shown only under <see cref="AssistantLabel"/>.</summary>
        public string? AssistantText;
        public string AssistantLabel = "";
        public bool HardWarning;
        public bool OfferApproveThisBuild;
        /// <summary>Revit could not preview (part of) this; counts are not measured.</summary>
        public bool EstimateOnly;

        public int Deletions;
        public int Additions;
        public int Modifications;

        private readonly TaskCompletionSource<ConfirmDecision> _decision =
            new TaskCompletionSource<ConfirmDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _decided;
        private CancellationTokenRegistration _cancel;

        public Task<ConfirmDecision> Decision => _decision.Task;
        public bool Resolved => Volatile.Read(ref _decided) == 1;
        public ConfirmDecision? Outcome => _decision.Task.IsCompleted ? _decision.Task.Result : null;

        /// <summary>The first decision wins; later ones return false.</summary>
        public bool TryDecide(ConfirmDecision decision)
        {
            if (Interlocked.Exchange(ref _decided, 1) == 1) return false;
            try { _cancel.Dispose(); } catch { }
            _decision.TrySetResult(decision ?? new ConfirmDecision());
            return true;
        }

        /// <summary>Cancelling the token declines the card (if still open).</summary>
        public void BindCancellation(CancellationToken ct)
        {
            if (!ct.CanBeCanceled) return;
            _cancel = ct.Register(() => TryDecide(new ConfirmDecision
            {
                Approved = false,
                Reason = "cancelled",
            }));
        }

        public static ConfirmCardModel From(ConfirmRequest req, IReadOnlyList<ToolCallDto>? calls, ReceiptLanguage lang)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            var ms = lang == ReceiptLanguage.Ms;
            var card = new ConfirmCardModel
            {
                BatchId = req.BatchId ?? "",
                PlanHash = req.PlanHash ?? "",
                Language = lang,
                OfferApproveThisBuild = req.OfferApproveThisBuild,
                AssistantLabel = ms ? "Penerangan pembantu" : "Assistant's description",
            };
            var previews = req.Preview ?? Array.Empty<Envelope>();
            calls ??= Array.Empty<ToolCallDto>();

            var deletedTouched = 0;
            var deletedSide = new Dictionary<string, int>();
            var addedTouched = 0;
            var addedSide = new Dictionary<string, int>();
            var modifiedSide = new Dictionary<string, int>();
            var other = new OtherSettings();
            var modifyGroups = new List<(string field, string? before, string? after, int n)>();
            var modifiedIds = new HashSet<long>();
            var modifiedCount = 0;
            var warnings = new List<string>();
            var failures = new List<string>();

            for (var i = 0; i < previews.Length; i++)
            {
                var env = previews[i];
                if (env == null) continue;
                var tool = i < calls.Count ? calls[i].Tool : null;
                if (env.Phase != EnvelopePhase.Preview) card.EstimateOnly = true;
                if (!env.Ok && env.Status != EnvelopeStatus.Rejected)
                    failures.Add(env.Error?.Message ?? env.Summary);

                var t = env.Changes?.Touched;
                if (t != null)
                {
                    deletedTouched += t.Deleted?.Count ?? 0;
                    addedTouched += t.Added?.Count ?? 0;
                    var ids = new HashSet<long>();
                    foreach (var m in t.Modified ?? new List<FieldChangeDto>())
                    {
                        ids.Add(m.Id);
                        var idx = modifyGroups.FindIndex(g => g.field == m.Field && g.before == m.Before && g.after == m.After);
                        if (idx >= 0) { var g = modifyGroups[idx]; modifyGroups[idx] = (g.field, g.before, g.after, g.n + 1); }
                        else modifyGroups.Add((m.Field, m.Before, m.After, 1));
                    }
                    foreach (var id in ids) modifiedIds.Add(id);
                    // A typed write's count is its target count; its rows may be capped at 50.
                    modifiedCount += tool == ToolNames.SetParameters && env.Count is int c && c > ids.Count ? c : ids.Count;
                }

                var s = env.Changes?.SideEffects;
                if (s != null)
                {
                    Merge(deletedSide, s.DeletedByCategory, other, deleted: true);
                    Merge(addedSide, s.AddedByCategory, other, deleted: false);
                    Merge(modifiedSide, s.ModifiedByCategory, other, deleted: false);
                }

                foreach (var w in env.Warnings ?? new List<WarningDto>())
                    if (!string.IsNullOrWhiteSpace(w.Text) && !warnings.Contains(w.Text)) warnings.Add(w.Text);
            }

            var deletedSideTotal = deletedSide.Values.Sum();
            card.Deletions = deletedTouched + deletedSideTotal + other.Deleted;
            card.Additions = addedTouched + addedSide.Values.Sum();
            card.Modifications = modifiedCount;

            // 1. Deletions, first and in red.
            if (deletedTouched > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Delete, Danger = true,
                    Text = (ms ? "Padam " : "Delete ") + deletedTouched + (ms ? " elemen" : deletedTouched == 1 ? " element" : " elements") });
            if (deletedSideTotal > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Delete, Danger = true,
                    Text = (ms ? "Turut dipadam: " : "Also deleted: ") + ByCategory(deletedSide) });

            // 2. Additions.
            if (addedTouched > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Add,
                    Text = (ms ? "Tambah " : "Add ") + addedTouched + (ms ? " elemen" : addedTouched == 1 ? " element" : " elements") });
            if (addedSide.Count > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Add,
                    Text = (ms ? "Turut ditambah: " : "Also added: ") + ByCategory(addedSide) });

            // 3. Before -> after from the preview read-back.
            foreach (var g in modifyGroups.OrderByDescending(g => g.n).Take(MaxModifyGroups))
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Modify,
                    Text = g.field + ": " + Show(g.before, ms) + " → " + Show(g.after, ms) + " (" + g.n + ")" });
            var shown = modifyGroups.OrderByDescending(g => g.n).Take(MaxModifyGroups).Sum(g => g.n);
            if (modifiedCount > shown)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Modify,
                    Text = ms ? "… dan " + (modifiedCount - shown) + " lagi" : "… and " + (modifiedCount - shown) + " more" });

            // 4. Side effects.
            if (modifiedSide.Count > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.SideEffect,
                    Text = (ms ? "Turut berubah: " : "Also changed: ") + ByCategory(modifiedSide) });
            if (other.Total > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.SideEffect, Danger = other.Deleted > 0,
                    Text = (ms ? "Tetapan lain: " : "Other settings: ") + other.Total
                           + (other.Classes.Count > 0 ? " (" + string.Join(", ", other.Classes.OrderBy(c => c, StringComparer.Ordinal)) + ")" : "") });

            // 5. Warnings Revit raised, then anything the preview could not do.
            foreach (var w in warnings.Take(MaxWarnings))
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Warning, Text = w });
            if (warnings.Count > MaxWarnings)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Warning,
                    Text = ms ? "… dan " + (warnings.Count - MaxWarnings) + " amaran lagi" : "… and " + (warnings.Count - MaxWarnings) + " more warnings" });
            foreach (var f in failures.Distinct())
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Failure, Danger = true, Text = f });

            var touchedTotal = deletedTouched + addedTouched + modifiedIds.Count;
            var sideTotal = deletedSideTotal + addedSide.Values.Sum() + modifiedSide.Values.Sum() + other.Total;
            card.HardWarning = card.Deletions > 0 || sideTotal > touchedTotal;

            card.Headline = BuildHeadline(card, ms);
            card.ToolLabel = string.Join(" + ", calls.Select(c => ToolLabelOf(c.Tool, ms)).Distinct());

            // Code in full; the model's words on their own line.
            var code = calls.Where(c => c.Tool == ToolNames.RunRevitCode).Select(c => Str(c.Args, "code")).Where(s => s != null).ToList();
            if (code.Count > 0) card.Code = string.Join("\n\n", code);
            var words = new List<string>();
            if (!string.IsNullOrWhiteSpace(req.AssistantDescription)) words.Add(req.AssistantDescription!.Trim());
            foreach (var intent in calls.Where(c => c.Tool == ToolNames.RunRevitCode).Select(c => Str(c.Args, "intent")))
                if (!string.IsNullOrWhiteSpace(intent) && !words.Contains(intent!.Trim())) words.Add(intent.Trim());
            card.AssistantText = words.Count > 0 ? string.Join("\n", words) : null;
            return card;
        }

        private static string BuildHeadline(ConfirmCardModel c, bool ms)
        {
            var parts = new List<string>();
            if (c.Deletions > 0) parts.Add((ms ? "Padam " : "Delete ") + c.Deletions);
            if (c.Additions > 0) parts.Add((ms ? "Tambah " : "Add ") + c.Additions);
            if (c.Modifications > 0) parts.Add((ms ? "Ubah " : "Change ") + c.Modifications);
            if (parts.Count == 0)
                return ms ? "Tiada perubahan diukur dalam pratonton" : "No changes measured in the preview";
            var total = c.Deletions + c.Additions + c.Modifications;
            return string.Join(" · ", parts) + (ms ? " elemen" : total == 1 ? " element" : " elements");
        }

        /// <summary>Labels the add-in owns for the tools that may write.</summary>
        public static string ToolLabelOf(string? tool, bool ms) => tool switch
        {
            ToolNames.SetParameters => ms ? "Tetapkan parameter" : "Set parameters",
            ToolNames.RunRevitCode => ms ? "Jalankan kod Revit" : "Run Revit code",
            _ => tool ?? "",
        };

        private static string Show(string? value, bool ms) =>
            string.IsNullOrEmpty(value) ? (ms ? "(kosong)" : "(empty)") : value!;

        /// <summary>Key prefix W3 uses for an element with no category: its class name.</summary>
        public const string ClassKeyPrefix = "class:";

        private sealed class OtherSettings
        {
            public int Total;
            public int Deleted;
            public readonly HashSet<string> Classes = new HashSet<string>(StringComparer.Ordinal);
        }

        private static void Merge(Dictionary<string, int> into, Dictionary<string, int>? from, OtherSettings other, bool deleted)
        {
            if (from == null) return;
            foreach (var kv in from)
            {
                if (kv.Value <= 0) continue;
                var key = kv.Key ?? "";
                if (key.Length == 0 || key.StartsWith(ClassKeyPrefix, StringComparison.Ordinal))
                {
                    other.Total += kv.Value;
                    if (deleted) other.Deleted += kv.Value;
                    var cls = key.Length > ClassKeyPrefix.Length ? key.Substring(ClassKeyPrefix.Length).Trim() : "";
                    if (cls.Length > 0) other.Classes.Add(cls);
                    continue;
                }
                into[key] = (into.TryGetValue(key, out var n) ? n : 0) + kv.Value;
            }
        }

        private static string ByCategory(Dictionary<string, int> counts) =>
            string.Join(", ", counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + " " + kv.Value));

        private static string? Str(JsonElement args, string name)
        {
            try
            {
                return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
            }
            catch (InvalidOperationException) { return null; }
        }
    }
}
