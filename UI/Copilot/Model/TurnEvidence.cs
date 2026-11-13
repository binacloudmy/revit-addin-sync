#nullable enable
// What one copilot turn measured, collected from the envelopes the add-in
// returned (lean-harness spec §7.11; W5 Task 3). Revit-free.
//
// The pane renders the evidence strip, the receipt headline and the caution
// line from THIS, never from the reply text: a reply that says "done" cannot
// hide a write that failed, was declined or changed nothing.
//
//   reads  (any envelope with phase read)       -> Evidence: count, searched,
//                                                  not_searched, highlight
//   writes (set_parameters, run_revit_code that -> Receipt: done/total, failed
//          previewed or committed)                 by reason, unchanged, declined
//   successful commits                          -> Committed (Save as command)
//   read rows (data.rows / data.groups)         -> Tables (Export to Excel)
//   check results (data.rules)                  -> a "check" Table, one row per
//                                                  rule, with each failing rule's
//                                                  handle for Highlight (spec §21)
//   read images (look's env.Image)              -> Images: the drafter sees every
//                                                  image the model saw (§7.7), taken
//                                                  before the turn budget strips any

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum ReceiptLanguage { En, Ms }

    /// <summary>One read's evidence, straight from its envelope.</summary>
    public sealed class EvidenceItem
    {
        public string Tool = "";
        public int? Count;
        public List<string> Sources = new List<string>();
        public List<string> Categories = new List<string>();
        public string? Filter;
        public List<string> NotSearched = new List<string>();
        public int? Selected;
        public int? VisibleInView;
        public string? Handle;
        /// <summary>The read failed: its error code (or status). Shown as a failed read,
        /// never in the write receipt (live-test L4).</summary>
        public string? FailedReason;
    }

    /// <summary>Elements that did not get the change, by reason (an envelope
    /// error code, or "mismatch" for a failed read-back).</summary>
    public sealed class FailureGroup
    {
        public string Reason = "";
        public int Count;
    }

    /// <summary>Counts for the receipt headline, summed over a turn's writes.</summary>
    public sealed class ReceiptFacts
    {
        public const string Mismatch = "mismatch";

        public int Done;
        public int Total;
        /// <summary>status noop: nothing needed changing.</summary>
        public int Unchanged;
        /// <summary>status rejected: the drafter declined.</summary>
        public int Declined;
        public List<FailureGroup> Failed = new List<FailureGroup>();

        public int FailedCount => Failed.Sum(f => f.Count);

        internal void AddFailed(string reason, int n)
        {
            if (n <= 0) return;
            var g = Failed.FirstOrDefault(f => f.Reason == reason);
            if (g == null) Failed.Add(g = new FailureGroup { Reason = reason });
            g.Count += n;
        }
    }

    /// <summary>A successful typed or code write, kept for "Save as command".</summary>
    public sealed class CommittedWrite
    {
        public string Tool = "";
        public JsonElement Args;
        /// <summary>run_revit_code's intent, or the add-in's own label.</summary>
        public string Intent = "";
        public string Summary = "";
    }

    /// <summary>Rows a read returned, for the Export button.</summary>
    public sealed class ResultTable
    {
        public const string CheckKind = "check";

        public string Title = "";
        public List<string> Columns = new List<string>();
        public List<List<string>> Rows = new List<List<string>>();
        /// <summary>"check" for a rule-set result; empty for plain read rows.</summary>
        public string Kind = "";
        /// <summary>Per row: the handle to highlight (a check rule's failing elements), or null.</summary>
        public List<string?> RowHandles = new List<string?>();

        public string? HandleOf(int row) => row >= 0 && row < RowHandles.Count ? RowHandles[row] : null;
    }

    public sealed class TurnEvidence
    {
        private readonly List<EvidenceItem> _evidence = new List<EvidenceItem>();
        private readonly List<Envelope> _writes = new List<Envelope>();
        private readonly List<string> _writeTools = new List<string>();
        private readonly List<CommittedWrite> _committed = new List<CommittedWrite>();
        private readonly List<ResultTable> _tables = new List<ResultTable>();
        private readonly List<ImageDto> _images = new List<ImageDto>();
        private readonly List<(ToolCallDto Call, Envelope Env)> _calls = new List<(ToolCallDto, Envelope)>();

        /// <summary>Every call and its envelope, in order: the result card is built from these.</summary>
        public IReadOnlyList<(ToolCallDto Call, Envelope Env)> Calls { get { lock (_sync) return _calls.ToList(); } }

        /// <summary>The most images one turn can send the model (TurnBudget).</summary>
        public const int MaxImages = RevitWebAppSync.Services.TurnBudget.MaxImages;
        private readonly List<string> _notes = new List<string>();
        private readonly object _sync = new object();

        /// <summary>Pane-owned facts the receipt must show (e.g. a table cut to
        /// its first 2000 rows), in the order they were added.</summary>
        public IReadOnlyList<string> Notes { get { lock (_sync) return _notes.ToList(); } }

        public void AddNote(string note)
        {
            if (string.IsNullOrWhiteSpace(note)) return;
            lock (_sync) if (!_notes.Contains(note)) _notes.Add(note);
        }

        public IReadOnlyList<EvidenceItem> Evidence { get { lock (_sync) return _evidence.ToList(); } }
        public IReadOnlyList<CommittedWrite> Committed { get { lock (_sync) return _committed.ToList(); } }
        public IReadOnlyList<ResultTable> Tables { get { lock (_sync) return _tables.ToList(); } }
        /// <summary>Images the turn's reads returned (look), in order.</summary>
        public IReadOnlyList<ImageDto> Images { get { lock (_sync) return _images.ToList(); } }

        public bool HasWrites { get { lock (_sync) return _writes.Count > 0; } }

        /// <summary>An attempt that failed BEFORE committing (a compile error, a policy block, a busy Revit) and
        /// that a later done write of the same tool followed: the retry worked. Never a partial or committed
        /// write (something may have changed), never a write the drafter declined, never a noop.</summary>
        private bool Retried(int i)
        {
            var w = _writes[i];
            var status = w.StatusKind();
            if (status == EnvelopeStatusKind.Done || status == EnvelopeStatusKind.Partial || status == EnvelopeStatusKind.Rejected || status == EnvelopeStatusKind.Noop) return false;
            if (w.PhaseKind() == EnvelopePhaseKind.Commit) return false;
            for (var j = i + 1; j < _writes.Count; j++)
                if (_writes[j].StatusKind() == EnvelopeStatusKind.Done && _writeTools[j] == _writeTools[i]) return true;
            return false;
        }

        /// <summary>True whenever a write in the turn is not status done, unless it was a failed attempt that a later
        /// done write of the same tool retried (RetriedAttempts says so instead). The amber warning.</summary>
        public bool Caution { get { lock (_sync) { for (var i = 0; i < _writes.Count; i++) if (_writes[i].StatusKind() != EnvelopeStatusKind.Done && !Retried(i) && !CleanNoop(i)) return true; return false; } } }

        /// <summary>The turn wrote nothing but clean no-ops (every write is one): no receipt bar, no caution.</summary>
        public bool OnlyCleanNoops { get { lock (_sync) { if (_writes.Count == 0) return false; for (var i = 0; i < _writes.Count; i++) if (!CleanNoop(i)) return false; return true; } } }

        /// <summary>A code write that changed nothing and carries no warning: the call declared that it changes nothing (the add-in adds the
        /// "ran but changed nothing" warning only when it did not), so "Not every change was applied" would be wrong.</summary>
        private bool CleanNoop(int i)
        {
            var w = _writes[i];
            return w.StatusKind() == EnvelopeStatusKind.Noop && _writeTools[i] == BinaVibe.Copilot.Contracts.ToolNames.RunRevitCode
                && !(w.Warnings ?? new System.Collections.Generic.List<BinaVibe.Copilot.Contracts.WarningDto>()).Any(x => x.Severity == BinaVibe.Copilot.Contracts.WarningSeverity.Warning);
        }

        /// <summary>Failed-before-commit attempts that a later done write of the same tool retried.</summary>
        public int RetriedAttempts { get { lock (_sync) { var n = 0; for (var i = 0; i < _writes.Count; i++) if (Retried(i)) n++; return n; } } }

        /// <summary>True when a write committed (an Undo may apply).</summary>
        public bool AnyCommitted { get { lock (_sync) return _writes.Any(IsCommitted); } }

        /// <summary>A write envelope that changed the model: a commit-phase envelope that is done or partial, or a code call that ran as a
        /// WRITE and did not stay a preview (a code write whose envelope does not say "commit" still has its undo group).</summary>
        internal static bool IsCommitted(Envelope w)
        {
            var done = w.StatusKind() == EnvelopeStatusKind.Done || w.StatusKind() == EnvelopeStatusKind.Partial;
            if (!done) return false;
            if (w.PhaseKind() == EnvelopePhaseKind.Commit) return true;
            return w.PhaseKind() != EnvelopePhaseKind.Preview && ToolResults.Mode(w) == EnvelopeCodeMode.Write;
        }

        public ReceiptFacts Receipt
        {
            get
            {
                lock (_sync)
                {
                    var f = new ReceiptFacts();
                    foreach (var w in _writes) AddTo(f, w);
                    return f;
                }
            }
        }

        /// <summary>Record one tool request's envelopes (one per call, in call order).</summary>
        public void Add(ToolRequestDto request, IReadOnlyList<Envelope> envelopes)
        {
            if (request?.Calls == null || envelopes == null) return;
            lock (_sync)
            {
                for (var i = 0; i < request.Calls.Count && i < envelopes.Count; i++)
                {
                    var call = request.Calls[i];
                    var env = envelopes[i];
                    if (env == null) continue;
                    _calls.Add((call, env));

                    if (IsWrite(request, call, env))
                    {
                        _writes.Add(env);
                        _writeTools.Add(call.Tool ?? "");
                        if (env.PhaseKind() == EnvelopePhaseKind.Commit && env.StatusKind() == EnvelopeStatusKind.Done)
                            _committed.Add(new CommittedWrite
                            {
                                Tool = call.Tool,
                                Args = call.Args.ValueKind == JsonValueKind.Undefined ? default : call.Args.Clone(),
                                Intent = IntentOf(call),
                                Summary = env.Summary,
                            });
                    }
                    else
                    {
                        var item = EvidenceOf(call.Tool, env);
                        if (item != null) _evidence.Add(item);
                        var table = call.Tool == ToolNames.Check ? CheckTableOf(env) : TableOf(env);
                        if (table != null) _tables.Add(table);
                        if (env.Image != null && !string.IsNullOrEmpty(env.Image.Base64) && _images.Count < MaxImages)
                            _images.Add(env.Image);
                    }
                }
            }
        }

        /// <summary>A write is set_parameters, or run_revit_code that ran as a write
        /// (ruling R13: read-classified code arrives in write requests too). L4: W4 puts
        /// data.mode = read|write on every code envelope, failed and rejected ones too;
        /// without it, a read-phase envelope is a read whatever its status, so a code
        /// read that failed or was blocked never shows as "0/1 done, 1 failed".</summary>
        internal static bool IsWrite(ToolRequestDto request, ToolCallDto call, Envelope env)
        {
            if (call.Tool == ToolNames.SetParameters) return true;
            if (call.Tool != ToolNames.RunRevitCode) return false;
            var mode = ToolResults.Mode(env);
            if (mode == EnvelopeCodeMode.Write) return true;
            if (mode == EnvelopeCodeMode.Read) return false;
            if (env.PhaseKind() == EnvelopePhaseKind.Read) return false;
            return request.Kind == ToolRequestKinds.Write;
        }

        private static string IntentOf(ToolCallDto call)
        {
            try
            {
                if (call.Args.ValueKind == JsonValueKind.Object
                    && call.Args.TryGetProperty("intent", out var i) && i.ValueKind == JsonValueKind.String)
                    return i.GetString() ?? "";
            }
            catch (InvalidOperationException) { }
            return call.Tool;
        }

        private static EvidenceItem? EvidenceOf(string tool, Envelope env)
        {
            var failed = !env.Ok && env.StatusKind() != EnvelopeStatusKind.Rejected
                ? env.Error?.Code ?? env.Status
                : null;
            if (failed == null && env.Searched == null && env.Count == null && env.Highlight == null) return null;
            return new EvidenceItem
            {
                FailedReason = failed,
                Tool = tool ?? "",
                Count = env.Count,
                Sources = env.Searched?.Sources?.ToList() ?? new List<string>(),
                Categories = env.Searched?.Categories?.ToList() ?? new List<string>(),
                Filter = env.Searched?.Filter,
                NotSearched = env.Searched?.NotSearched?.ToList() ?? new List<string>(),
                Selected = env.Highlight?.Selected,
                VisibleInView = env.Highlight?.VisibleInView,
                Handle = env.Handle,
            };
        }

        private static void AddTo(ReceiptFacts f, Envelope w)
        {
            var touched = TouchedIds(w);
            var mismatches = w.Verified?.Mismatches?.Select(m => m.Id).Distinct().Count() ?? 0;
            // A count_delta check compares the whole model's element count before and after: its "checked" is
            // every element in the document (21,984 in the live test), not the elements this write changed.
            var verified = w.Verified != null && w.Verified.Method != VerifyMethods.None && w.Verified.Method != VerifyMethods.CountDelta
                ? w.Verified.Checked
                : (int?)null;
            var total = w.Count ?? (touched > 0 ? touched : Math.Max(verified ?? 0, 1));

            switch (w.StatusKind())
            {
                case EnvelopeStatusKind.Done:
                    f.Total += total;
                    f.Done += total;
                    break;
                case EnvelopeStatusKind.Noop:
                    f.Total += total;
                    f.Unchanged += total;
                    break;
                case EnvelopeStatusKind.Rejected:
                    f.Total += total;
                    f.Declined += total;
                    break;
                case EnvelopeStatusKind.Partial:
                {
                    var reached = verified ?? touched;
                    var done = Math.Max(0, Math.Min(total, reached) - mismatches);
                    f.Total += total;
                    f.Done += done;
                    f.AddFailed(ReceiptFacts.Mismatch, Math.Min(mismatches, total - done));
                    f.AddFailed(w.Error?.Code ?? EnvelopeStatus.Partial, total - done - Math.Min(mismatches, total - done));
                    break;
                }
                default:   // failed, revit_busy, timeout
                    f.Total += total;
                    f.AddFailed(w.Error?.Code ?? w.Status, total);
                    break;
            }
        }

        private static int TouchedIds(Envelope w)
        {
            var t = w.Changes?.Touched;
            if (t == null) return 0;
            var ids = new HashSet<long>();
            foreach (var a in t.Added ?? new List<ElementRef>()) ids.Add(a.Id);
            foreach (var m in t.Modified ?? new List<FieldChangeDto>()) ids.Add(m.Id);
            foreach (var d in t.Deleted ?? new List<ElementRef>()) ids.Add(d.Id);
            return ids.Count;
        }

        /// <summary>check's data.rules as one row per rule (spec §21): rule, cite,
        /// severity, pass / fail / not verified counts and why not verified. A rule
        /// with failures keeps its fail_handle for the pane's Highlight action.</summary>
        internal static ResultTable? CheckTableOf(Envelope env)
        {
            var result = ToolResults.Check(env);
            if (result == null || result.Rules.Count == 0) return null;

            var title = string.IsNullOrWhiteSpace(result.RuleSetId) ? "Check" : "Check " + result.RuleSetId.Trim()
                + (string.IsNullOrWhiteSpace(result.Version) ? "" : " " + result.Version.Trim());
            var table = new ResultTable
            {
                Kind = ResultTable.CheckKind,
                Title = title,
                Columns = new List<string> { "Rule", "Cite", "Severity", "Pass", "Fail", "Not verified", "Why not verified" },
            };
            foreach (var r in result.Rules)
            {
                if (r == null) continue;
                table.Rows.Add(new List<string>
                {
                    r.RuleId ?? "", r.Cite ?? "", r.Severity ?? "",
                    r.Pass.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.Fail.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.NotVerified.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.NotVerifiedReason ?? "",
                });
                table.RowHandles.Add(r.Fail > 0 && !string.IsNullOrWhiteSpace(r.FailHandle) ? r.FailHandle!.Trim() : null);
            }
            return table;
        }

        /// <summary>data.rows (array of objects) or data.groups ({key,count}) as a table.</summary>
        private static ResultTable? TableOf(Envelope env)
        {
            var data = ToolResults.Rows(env);
            if (data == null || data.Items.Count == 0) return null;
            var rows = data.Items;

            var table = new ResultTable { Title = env.Summary ?? "" };

            // Schedules may come as {columns:[...], rows:[[...], ...]}.
            if (data.IsTabular)
            {
                if (data.Columns == null) return null;
                table.Columns = data.Columns.Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.GetRawText()).ToList();
                foreach (var row in rows)
                    table.Rows.Add(table.Columns.Select((_, i) => i < row.GetArrayLength() ? Cell(row[i]) : "").ToList());
                return table;
            }

            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in row.EnumerateObject())
                {
                    var name = p.Name == "ref" ? "id" : p.Name;
                    if (!table.Columns.Contains(name)) table.Columns.Add(name);
                }
            }
            foreach (var row in rows)
            {
                var cells = new List<string>();
                foreach (var col in table.Columns)
                {
                    JsonElement v;
                    var has = col == "id"
                        ? row.TryGetProperty("ref", out v) || row.TryGetProperty("id", out v)
                        : row.TryGetProperty(col, out v);
                    cells.Add(has ? Cell(v) : "");
                }
                table.Rows.Add(cells);
            }
            return table;
        }

        private static string Cell(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.String: return v.GetString() ?? "";
                case JsonValueKind.Number: return v.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null:
                case JsonValueKind.Undefined: return "";
                case JsonValueKind.Object:
                    // an element ref {id, link?}
                    if (v.TryGetProperty("id", out var id))
                        return v.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.Number
                            ? id.GetRawText() + "@" + link.GetRawText()
                            : id.GetRawText();
                    return v.GetRawText();
                default: return v.GetRawText();
            }
        }
    }
}
