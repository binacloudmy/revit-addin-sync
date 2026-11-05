#nullable enable
// The add-in's own confirm card (lean-harness spec §7.5; W5 Task 3). Revit-free.
//
// Built ONLY from what the add-in measured in the preview it holds:
//   - counts, deletions first and in red: touched deletions by category from
//     changes.touched_by_category (R78, "Delete 2 Sheets, 5 Views"), then
//     side-effect deletions by category;
//   - additions ("Add 12 Door Tags") and changes ("Change 34 Walls") by
//     category, then before -> after values from the preview read-back (rows
//     with no field name, which code edits report, are never shown). Without
//     touched_by_category the touched counts read "N elements";
//   - side effects by category, warnings Revit raised, preview failures;
//   - uncategorised changes (W3 reports them under "class:<ClassName>" or an
//     empty key) as one "Other settings: N" side-effect row (ruling R43),
//     counted in the hard-warning decision; red when any was deleted;
//   - the preview's thumbnail or markers of created elements (env.Image);
//   - tool labels the add-in owns, and run_revit_code's code in full.
// Per-call details (labels, code, intents) come ONLY from ConfirmRequest.Calls
// and only when it is index-aligned with Preview (R50, IsAligned); otherwise
// the card attributes nothing to a call and the counts still come from the
// preview totals. Preview failures are the add-in's own sentence ("Preview
// failed: not owned"); the raw message (often an exception the model's code
// threw) sits only under the labelled "Code output" line. The cloud's words
// (the description, run_revit_code's intent) appear only in AssistantText,
// under its own label. A hard-warning style is used when anything is deleted
// or the side effects outnumber the touched set.
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
        /// <summary>run_revit_code's code, verbatim; null for typed writes. The card shows it only behind "Show code".</summary>
        public string? Code;
        public bool HasCode => !string.IsNullOrEmpty(Code);
        /// <summary>Cloud/model words, shown only under <see cref="AssistantLabel"/>.</summary>
        public string? AssistantText;
        public string AssistantLabel = "";
        /// <summary>Raw failure text from the preview, shown only under <see cref="CodeOutputLabel"/>.</summary>
        public string? CodeOutput;
        public string CodeOutputLabel = "";
        /// <summary>Thumbnails / markers the preview rendered (W3 env.Image).</summary>
        public List<ImageDto> Images = new List<ImageDto>();
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

        /// <summary>The chat epoch the card's write started under (CopilotTurnRouter
        /// .SessionEpoch); -1 when unknown.</summary>
        public long ChatEpoch = -1;

        /// <summary>M-4: a card that would land after its chat was left ("+" or
        /// another chat continued), after its turn was stopped, or that is already
        /// decided, never appears: it is declined where it stands. True when so.</summary>
        public bool DeclineIfStale(long currentEpoch, CancellationToken ct)
        {
            var left = ChatEpoch >= 0 && ChatEpoch != currentEpoch;
            if (!left && !ct.IsCancellationRequested && !Resolved) return false;
            TryDecide(new ConfirmDecision { Approved = false, Reason = left ? "chat_left" : "cancelled" });
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

        public static ConfirmCardModel From(ConfirmRequest req, ReceiptLanguage lang)
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
                CodeOutputLabel = ms ? "Output kod" : "Code output",
            };
            var previews = req.Preview ?? Array.Empty<Envelope>();
            // R50: attribute to calls only when they line up 1:1 with the previews.
            IReadOnlyList<ToolCallDto> calls = req.IsAligned ? req.Calls : Array.Empty<ToolCallDto>();
            var outputs = new List<string>();

            var deletedTouched = 0;
            var deletedSide = new Dictionary<string, int>();
            var addedTouched = 0;
            // R78: touched counts by category, and the counts of envelopes without it.
            var deletedCat = new Dictionary<string, int>();
            var addedCat = new Dictionary<string, int>();
            var modifiedCat = new Dictionary<string, int>();
            var deletedPlain = 0;
            var addedPlain = 0;
            var modifiedPlain = 0;   // envelopes without categories whose rows have no field
            // Elements the before -> after rows stand for. The rows are a sample (lists
            // are capped at 50); a typed write sets its value on every target, so with
            // categories its total is the category count (N-3: "… and 70 more").
            var fieldTotal = 0;
            var addedSide = new Dictionary<string, int>();
            var modifiedSide = new Dictionary<string, int>();
            var other = new OtherSettings();
            // L18: grouped by field + value after; a row whose value does not change is never shown.
            var modifyGroups = new List<ModifyGroup>();
            // L15: names of what code creates (data.created), by category, in order.
            var created = new List<(string category, List<string> names)>();
            // L17: type targets of set_parameters (data.types) and the instances they reach.
            var typeNames = new List<string>();
            var typeInstances = 0;
            string? typeCategory = null;
            var typeCategories = new HashSet<string>(StringComparer.Ordinal);
            var modifiedIds = new HashSet<long>();
            var modifiedCount = 0;
            var touchedModifiedTotal = 0;
            var warnings = new List<string>();
            var failures = new List<string>();

            for (var i = 0; i < previews.Length; i++)
            {
                var env = previews[i];
                if (env == null) continue;
                var tool = i < calls.Count ? calls[i].Tool : null;
                if (env.Phase != EnvelopePhase.Preview) card.EstimateOnly = true;
                if (!env.Ok && env.Status != EnvelopeStatus.Rejected)
                {
                    failures.Add((ms ? "Pratonton gagal: " : "Preview failed: ")
                        + ReceiptTemplates.Reason(env.Error?.Code ?? env.Status, lang));
                    var raw = env.Error?.Message ?? env.Summary;
                    if (!string.IsNullOrWhiteSpace(raw) && !outputs.Contains(raw.Trim())) outputs.Add(raw.Trim());
                }
                if (env.Image != null && !string.IsNullOrEmpty(env.Image.Base64)) card.Images.Add(env.Image);

                var t = env.Changes?.Touched;
                var byCat = env.Changes?.TouchedByCategory;
                if (byCat != null)
                {
                    // R78: true totals per category (the touched lists are capped).
                    var d = AddCounts(deletedCat, byCat.DeletedByCategory, ms);
                    var a = AddCounts(addedCat, byCat.AddedByCategory, ms);
                    var m = AddCounts(modifiedCat, byCat.ModifiedByCategory, ms);
                    foreach (var k in (byCat.ModifiedByCategory ?? new Dictionary<string, int>()).Where(kv => kv.Value > 0).Select(kv => kv.Key))
                        if (ChangeNames.ClassOf(k) == null && !string.IsNullOrWhiteSpace(k)) typeCategories.Add(k.Trim());
                    deletedTouched += d;
                    addedTouched += a;
                    modifiedCount += m;
                    touchedModifiedTotal += m;
                }
                if (t != null)
                {
                    if (byCat == null)
                    {
                        deletedTouched += t.Deleted?.Count ?? 0;
                        addedTouched += t.Added?.Count ?? 0;
                        deletedPlain += t.Deleted?.Count ?? 0;
                        addedPlain += t.Added?.Count ?? 0;
                    }
                    var ids = new HashSet<long>();
                    var fieldless = new HashSet<long>();
                    var withField = new HashSet<long>();
                    foreach (var m in t.Modified ?? new List<FieldChangeDto>())
                    {
                        ids.Add(m.Id);
                        // A code edit names no field: it is counted by category, never
                        // shown as ": (empty) → (empty)".
                        if (string.IsNullOrWhiteSpace(m.Field)) { fieldless.Add(m.Id); continue; }
                        // L18: a value that does not change is not a change.
                        if (Norm(m.Before) == Norm(m.After)) continue;
                        withField.Add(m.Id);
                        var g = modifyGroups.FirstOrDefault(x => x.Field == m.Field && x.After == Norm(m.After));
                        if (g == null) modifyGroups.Add(g = new ModifyGroup { Field = m.Field, After = Norm(m.After) });
                        g.Befores.Add(Norm(m.Before));
                        g.N++;
                    }
                    if (byCat != null && withField.Count > 0)
                    {
                        var catModified = byCat.ModifiedByCategory?.Values.Where(v => v > 0).Sum() ?? 0;
                        var typed = tool == ToolNames.SetParameters || withField.Count >= EnvelopeLimits.MaxRows;
                        fieldTotal += typed ? Math.Max(withField.Count, catModified) : withField.Count;
                    }
                    if (byCat == null)
                    {
                        foreach (var id in ids) modifiedIds.Add(id);
                        // Lists are capped at 50 rows; the envelope's count is the true
                        // total (a typed write's count is its target count).
                        var capped = ids.Count >= EnvelopeLimits.MaxRows;
                        var n = (tool == ToolNames.SetParameters || capped) && env.Count is int c && c > ids.Count ? c : ids.Count;
                        modifiedCount += n;
                        touchedModifiedTotal += n;
                        var plain = fieldless.Count == ids.Count ? n : fieldless.Count;
                        modifiedPlain += plain;
                        fieldTotal += n - plain;
                    }
                }

                // L15: what a code preview creates, by name (W3's data.created).
                foreach (var (cat, name) in CreatedOf(env))
                {
                    var key = string.IsNullOrWhiteSpace(cat) ? (ms ? "Elemen" : "Elements") : cat!.Trim();
                    var slot = created.FindIndex(c => c.category == key);
                    if (slot < 0) { created.Add((key, new List<string>())); slot = created.Count - 1; }
                    created[slot].names.Add(name);
                }

                // L17: a type parameter changes every instance of the type.
                if (tool == ToolNames.SetParameters || tool == null)
                {
                    var (names, instances) = TypesOf(env);
                    foreach (var n in names) if (!typeNames.Contains(n)) typeNames.Add(n);
                    typeInstances += instances;
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

            // 1. Deletions, first and in red: "Delete 2 Sheets, 5 Views".
            if (deletedTouched > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Delete, Danger = true,
                    Text = (ms ? "Padam " : "Delete ") + Counted(deletedCat, deletedPlain, ms) });
            if (deletedSideTotal > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Delete, Danger = true,
                    Text = (ms ? "Turut dipadam: " : "Also deleted: ") + ByCategory(deletedSide, ms) });

            // 2. Additions: "Add 12 Door Tags".
            if (addedTouched > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Add,
                    Text = (ms ? "Tambah " : "Add ") + Counted(addedCat, addedPlain, ms) });
            foreach (var (category, names) in created.Take(MaxCreatedCategories))
            {
                var listed = names.Take(MaxCreatedNames).ToList();
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Add,
                    Text = category + ": " + string.Join(", ", listed)
                           + (names.Count > listed.Count ? (ms ? " dan " : " and ") + (names.Count - listed.Count) + (ms ? " lagi" : " more") : "") });
            }
            if (addedSide.Count > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Add,
                    Text = (ms ? "Turut ditambah: " : "Also added: ") + ByCategory(addedSide, ms) });

            // 3. Changes by category ("Change 34 Walls"), then before -> after from
            //    the preview read-back.
            if (modifiedCat.Values.Sum() + modifiedPlain > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Modify,
                    Text = (ms ? "Ubah " : "Change ") + Counted(modifiedCat, modifiedPlain, ms) });
            // L17: "Type PTr680a — affects 8 doors".
            if (typeNames.Count > 0)
            {
                typeCategory = typeCategories.Count == 1 ? typeCategories.First() : null;
                var what = typeCategory != null ? typeCategory.ToLowerInvariant() : (ms ? "instans" : typeInstances == 1 ? "instance" : "instances");
                var listedTypes = string.Join(", ", typeNames.Take(MaxTypeNames)) + (typeNames.Count > MaxTypeNames ? ", …" : "");
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Modify,
                    Text = (ms ? (typeNames.Count == 1 ? "Jenis " : "Jenis-jenis ") : (typeNames.Count == 1 ? "Type " : "Types ")) + listedTypes
                           + (typeInstances > 0
                               ? " — " + (ms ? "melibatkan " : typeNames.Count == 1 ? "affects " : "affect ") + typeInstances + " " + what
                               : "") });
            }
            foreach (var g in modifyGroups.OrderByDescending(g => g.N).Take(MaxModifyGroups))
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Modify,
                    Text = g.Field + ": " + (g.Befores.Count == 1 ? Show(g.Befores.First(), ms) : (ms ? "pelbagai" : "various"))
                           + " → " + Show(g.After, ms) + " (" + g.N + ")" });
            var shown = modifyGroups.OrderByDescending(g => g.N).Take(MaxModifyGroups).Sum(g => g.N);
            var covered = Math.Max(fieldTotal, modifyGroups.Sum(g => g.N));
            if (modifyGroups.Count > 0 && covered > shown)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Modify,
                    Text = ms ? "… dan " + (covered - shown) + " lagi" : "… and " + (covered - shown) + " more" });

            // 4. Side effects.
            if (modifiedSide.Count > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.SideEffect,
                    Text = (ms ? "Turut berubah: " : "Also changed: ") + ByCategory(modifiedSide, ms) });
            if (other.Total > 0)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.SideEffect, Danger = other.Deleted > 0,
                    Text = (ms ? "Tetapan lain: " : "Other settings: ") + other.Total + " — " + other.Describe(ms) });

            // 5. Warnings Revit raised, then anything the preview could not do.
            foreach (var w in warnings.Take(MaxWarnings))
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Warning, Text = w });
            if (warnings.Count > MaxWarnings)
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Warning,
                    Text = ms ? "… dan " + (warnings.Count - MaxWarnings) + " amaran lagi" : "… and " + (warnings.Count - MaxWarnings) + " more warnings" });
            foreach (var f in failures.Distinct())
                card.Rows.Add(new ConfirmRow { Kind = ConfirmRowKind.Failure, Danger = true, Text = f });

            var touchedTotal = deletedTouched + addedTouched + touchedModifiedTotal;
            var sideTotal = deletedSideTotal + addedSide.Values.Sum() + modifiedSide.Values.Sum() + other.Total;
            card.HardWarning = card.Deletions > 0 || sideTotal > touchedTotal;

            card.Headline = BuildHeadline(card, ms);
            card.ToolLabel = calls.Count > 0
                ? string.Join(" + ", calls.Select(c => ToolLabelOf(c.Tool, ms)).Distinct())
                : (ms ? "Perubahan Revit" : "Revit changes");
            card.CodeOutput = outputs.Count > 0 ? string.Join("\n", outputs) : null;

            // Code in full; the model's words on their own line.
            var code = calls.Where(c => c.Tool == ToolNames.RunRevitCode).Select(c => Str(c.Args, "code")).Where(s => s != null).ToList();
            if (code.Count > 0) card.Code = string.Join("\n\n", code);
            // The batch's description and run_revit_code's intent often say the same sentence (the description as
            // "Code: <intent>"): the drafter reads it once.
            card.AssistantText = OnceEach(new[] { req.AssistantDescription }.Concat(
                calls.Where(c => c.Tool == ToolNames.RunRevitCode).Select(c => Str(c.Args, "intent"))));
            return card;
        }

        // AI-269 item 2: no internal ids in the assistant's words. A plan hash, a GUID or a handle (exactly h_ and 8
        // lowercase hex, HandleStore) is the add-in's and the cloud's bookkeeping, never the drafter's words. Only the
        // assistant's text is scrubbed: the measured rows are the add-in's own facts (R42) and are never rewritten.
        private const string InternalId =
            @"\b(?:(?:sha256:)?[0-9a-fA-F]{64}|[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}|h_[0-9a-f]{8})\b";

        // An id in its own brackets goes with them; a bracket is removed only when both of its pair are there.
        private static readonly System.Text.RegularExpressions.Regex Internals = new System.Text.RegularExpressions.Regex(
            @"[ \t]*\(" + InternalId + @"\)|[ \t]*\[" + InternalId + @"\]|[ \t]*" + InternalId,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The text without GUIDs, plan hashes and handles. Text with none of them comes back exactly as it was
        /// (no trimming, no spacing changes). Null stays null.</summary>
        public static string? Plain(string? text)
        {
            if (text == null || !Internals.IsMatch(text)) return text;
            return Internals.Replace(text, "").Trim();
        }

        private static readonly System.Text.RegularExpressions.Regex CodePrefix = new System.Text.RegularExpressions.Regex(
            @"^\s*code\s*:\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>A text as compared: lower case, one space between words, no trailing punctuation.</summary>
        private static string SayingOf(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim().TrimEnd('.', ':', ';', '!', ' ');

        /// <summary>
        /// The assistant's words once each, in order, as the drafter should read them: ids scrubbed (<see cref="Plain"/>), a leading
        /// "Code:" removed, and a text that another already says (the same ignoring case, spacing and a final full stop, or
        /// contained in it) not repeated: of two such texts the longer is shown, in the place of the first. Texts that differ are
        /// all kept. Null when there is nothing to show.
        /// </summary>
        public static string? OnceEach(IEnumerable<string?> texts)
        {
            var kept = new List<(string Show, string Saying)>();
            foreach (var raw in texts)
            {
                var show = Plain(raw)?.Trim();
                if (show == null) continue;
                show = CodePrefix.Replace(show, "").Trim();
                var saying = SayingOf(show);
                if (saying.Length == 0) continue;
                if (kept.Any(k => k.Saying.Contains(saying))) continue;          // already said, in full or in more words
                var first = kept.FindIndex(k => saying.Contains(k.Saying));
                if (first < 0) { kept.Add((show, saying)); continue; }
                kept[first] = (show, saying);                                     // this one says what those did and more
                kept.RemoveAll(k => k.Saying != saying && saying.Contains(k.Saying));
            }
            return kept.Count == 0 ? null : string.Join("\n", kept.Select(k => k.Show));
        }

        /// <summary>The toggle that opens and closes the code on the card (collapsed by default).</summary>
        public static string CodeToggleLabel(bool ms, bool open) =>
            open ? (ms ? "Sembunyikan kod" : "Hide code") : (ms ? "Tunjuk kod" : "Show code");

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

        private const int MaxCreatedCategories = 4;
        private const int MaxCreatedNames = 5;
        private const int MaxTypeNames = 3;

        private sealed class ModifyGroup
        {
            public string Field = "";
            public string After = "";
            public readonly HashSet<string> Befores = new HashSet<string>(StringComparer.Ordinal);
            public int N;
        }

        private static string Norm(string? v) => (v ?? "").Trim();

        /// <summary>L16: elements with no category, by class. Listed classes get their
        /// friendly name ("View filters 2"); the rest read "1 other (an internal Revit
        /// setting)", never a raw class name.</summary>
        private sealed class OtherSettings
        {
            public int Total;
            public int Deleted;
            public readonly Dictionary<string, int> Classes = new Dictionary<string, int>(StringComparer.Ordinal);

            public string Describe(bool ms)
            {
                var named = new Dictionary<string, int>(StringComparer.Ordinal);
                var unknown = Total;
                foreach (var kv in Classes)
                {
                    var name = ChangeNames.FriendlyClass(kv.Key, ms);
                    if (name == null) continue;
                    named[name] = (named.TryGetValue(name, out var n) ? n : 0) + kv.Value;
                    unknown -= kv.Value;
                }
                var parts = named.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + " " + kv.Value).ToList();
                if (unknown > 0) parts.Add(unknown + (ms ? " lain (" : " other (") + ChangeNames.InternalSetting(ms) + ")");
                return string.Join(", ", parts);
            }
        }

        /// <summary>data.created = [{category, name}] (W3, code previews).</summary>
        private static IEnumerable<(string? category, string name)> CreatedOf(Envelope env)
        {
            if (env.Data is not JsonElement data || data.ValueKind != JsonValueKind.Object) yield break;
            if (!data.TryGetProperty("created", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var cat = item.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                yield return (cat, name!.Trim());
            }
        }

        /// <summary>set_parameters on a type parameter (W3's data.type_param, types[].name,
        /// instances_affected or types[].instances_in_model).</summary>
        private static (List<string> names, int instances) TypesOf(Envelope env)
        {
            var names = new List<string>();
            if (env.Data is not JsonElement data || data.ValueKind != JsonValueKind.Object) return (names, 0);
            if (!data.TryGetProperty("type_param", out var tp) || tp.ValueKind != JsonValueKind.True) return (names, 0);
            var sum = 0;
            if (data.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                foreach (var t in types.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    if (t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                        names.Add(n.GetString()!.Trim());
                    if (t.TryGetProperty("instances_in_model", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var k)) sum += k;
                }
            if (data.TryGetProperty("instances_affected", out var ia) && ia.ValueKind == JsonValueKind.Number && ia.TryGetInt32(out var total))
                sum = total;
            return (names, sum);
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
                    if (cls.Length > 0) other.Classes[cls] = (other.Classes.TryGetValue(cls, out var c) ? c : 0) + kv.Value;
                    continue;
                }
                into[key] = (into.TryGetValue(key, out var n) ? n : 0) + kv.Value;
            }
        }

        /// <summary>Adds per-category counts. A "class:" key of a listed class reads by its
        /// friendly name ("Add 2 View filters", L16); any other element with no category
        /// is summed under "". Returns the total added.</summary>
        private static int AddCounts(Dictionary<string, int> into, Dictionary<string, int>? from, bool ms)
        {
            if (from == null) return 0;
            var total = 0;
            foreach (var kv in from)
            {
                if (kv.Value <= 0) continue;
                var key = (kv.Key ?? "").Trim();
                var cls = ChangeNames.ClassOf(key);
                if (cls != null) key = ChangeNames.FriendlyClass(cls, ms) ?? "";
                into[key] = (into.TryGetValue(key, out var n) ? n : 0) + kv.Value;
                total += kv.Value;
            }
            return total;
        }

        /// <summary>"2 Sheets, 5 Views, 1 other, 3 elements": categories by name, then
        /// the uncategorised, then counts from envelopes without categories.</summary>
        private static string Counted(Dictionary<string, int> byCategory, int plain, bool ms)
        {
            var parts = byCategory.Where(kv => kv.Key.Length > 0 && kv.Value > 0)
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Value + " " + kv.Key)
                .ToList();
            if (byCategory.TryGetValue("", out var other) && other > 0)
                parts.Add(other + (ms ? " lain (" : " other (") + ChangeNames.InternalSetting(ms) + ")");
            if (plain > 0) parts.Add(plain + (ms ? " elemen" : plain == 1 ? " element" : " elements"));
            return string.Join(", ", parts);
        }

        /// <summary>"Rooms 1 (room boundaries recompute), Walls 3": a category Revit
        /// regenerates along with an edit says why (L16).</summary>
        private static string ByCategory(Dictionary<string, int> counts, bool ms) =>
            string.Join(", ", counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + " " + kv.Value + (ChangeNames.ReasonFor(kv.Key, ms) is string why ? " (" + why + ")" : "")));

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
