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
        public string Title = "";
        public List<string> Columns = new List<string>();
        public List<List<string>> Rows = new List<List<string>>();
    }

    public sealed class TurnEvidence
    {
        private readonly List<EvidenceItem> _evidence = new List<EvidenceItem>();
        private readonly List<Envelope> _writes = new List<Envelope>();
        private readonly List<CommittedWrite> _committed = new List<CommittedWrite>();
        private readonly List<ResultTable> _tables = new List<ResultTable>();
        private readonly object _sync = new object();

        public IReadOnlyList<EvidenceItem> Evidence { get { lock (_sync) return _evidence.ToList(); } }
        public IReadOnlyList<CommittedWrite> Committed { get { lock (_sync) return _committed.ToList(); } }
        public IReadOnlyList<ResultTable> Tables { get { lock (_sync) return _tables.ToList(); } }

        public bool HasWrites { get { lock (_sync) return _writes.Count > 0; } }

        /// <summary>True whenever any write in the turn is not status done.</summary>
        public bool Caution { get { lock (_sync) return _writes.Any(w => w.Status != EnvelopeStatus.Done); } }

        /// <summary>True when a write committed (an Undo may apply).</summary>
        public bool AnyCommitted { get { lock (_sync) return _writes.Any(w => w.Phase == EnvelopePhase.Commit && (w.Status == EnvelopeStatus.Done || w.Status == EnvelopeStatus.Partial)); } }

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

                    if (IsWrite(request, call, env))
                    {
                        _writes.Add(env);
                        if (env.Phase == EnvelopePhase.Commit && env.Status == EnvelopeStatus.Done)
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
                        var table = TableOf(env);
                        if (table != null) _tables.Add(table);
                    }
                }
            }
        }

        /// <summary>A write is set_parameters, or run_revit_code that did not run as a
        /// read (ruling R13: read-classified code arrives in write requests too).</summary>
        private static bool IsWrite(ToolRequestDto request, ToolCallDto call, Envelope env)
        {
            if (call.Tool == ToolNames.SetParameters) return true;
            if (call.Tool != ToolNames.RunRevitCode) return false;
            if (request.Kind != ToolRequestKinds.Write) return false;
            return !(env.Phase == EnvelopePhase.Read && env.Ok);
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
            if (env.Searched == null && env.Count == null && env.Highlight == null) return null;
            return new EvidenceItem
            {
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
            var verified = w.Verified != null && w.Verified.Method != VerifyMethods.None ? w.Verified.Checked : (int?)null;
            var total = w.Count ?? (touched > 0 ? touched : Math.Max(verified ?? 0, 1));

            switch (w.Status)
            {
                case EnvelopeStatus.Done:
                    f.Total += total;
                    f.Done += total;
                    break;
                case EnvelopeStatus.Noop:
                    f.Total += total;
                    f.Unchanged += total;
                    break;
                case EnvelopeStatus.Rejected:
                    f.Total += total;
                    f.Declined += total;
                    break;
                case EnvelopeStatus.Partial:
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

        /// <summary>data.rows (array of objects) or data.groups ({key,count}) as a table.</summary>
        private static ResultTable? TableOf(Envelope env)
        {
            if (env.Data is not JsonElement data || data.ValueKind != JsonValueKind.Object) return null;
            JsonElement rows;
            if (!data.TryGetProperty("rows", out rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            {
                if (!data.TryGetProperty("groups", out rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                    return null;
            }

            var table = new ResultTable { Title = env.Summary ?? "" };
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) return null;
                foreach (var p in row.EnumerateObject())
                {
                    var name = p.Name == "ref" ? "id" : p.Name;
                    if (!table.Columns.Contains(name)) table.Columns.Add(name);
                }
            }
            foreach (var row in rows.EnumerateArray())
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
