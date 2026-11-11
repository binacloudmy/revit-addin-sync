#nullable enable
// The result card (result-pattern spec, Nov 2026): one shape for every task
// kind, built from the turn's envelopes and never from the reply text.
//
//   changes   set_parameters / code commits with changes.touched.modified
//   created   commits that only added elements
//   findings  warnings or a check table, with no commit
//   selection a read that left a selection (highlight.selected > 0)
//
// Revit-free: compiles in Tests.csproj and the scratch harness.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum ResultKind { None, Changes, Created, Findings, Selection }

    public sealed class ChangeRow
    {
        public string Param = "";
        public string? Before;
        public string? After;
        /// <summary>Where the value came from, as text: "Your prompt", "Copilot's choice", "brochure.pdf, page 8",
        /// "list.xlsx, row 3", or the attachment's name when the row carries no source of its own; "" when unknown.</summary>
        public string Source = "";
        /// <summary>The elements (or types) this row was written to: what an edit writes again.</summary>
        public List<long> Ids = new List<long>();
        /// <summary>The read-back did not match what was written, so the drafter should look.</summary>
        public bool NeedsCheck;
        /// <summary>The row's own source (AI-296), null when it declared none.</summary>
        public ResultSourceDto? SourceInfo;
    }

    /// <summary>Text for a value's source, in the pane's language.</summary>
    public static class SourceLabels
    {
        public static string Of(ResultSourceDto? s, string fallback, ReceiptLanguage lang)
        {
            if (s == null) return fallback;
            var ms = lang == ReceiptLanguage.Ms;
            var at = string.IsNullOrWhiteSpace(s.Attachment) ? "" : s.Attachment!.Trim();
            string Where(string word, int? n) => n == null ? at : at + ", " + word + " " + n.Value.ToString(CultureInfo.InvariantCulture);
            switch (s.Kind)
            {
                case ResultSourceKinds.Prompt: return ms ? "Prompt anda" : "Your prompt";
                case ResultSourceKinds.Calculated: return ms ? "Pilihan Copilot" : "Copilot's choice";
                case ResultSourceKinds.Rule: return ms ? "Peraturan" : "Rule";
                case ResultSourceKinds.Document: return at == "" ? fallback : Where(ms ? "halaman" : "page", s.Page);
                case ResultSourceKinds.Excel: return at == "" ? fallback : Where(ms ? "baris" : "row", s.Row);
                default: return fallback;
            }
        }
    }

    public sealed class TypeGroup
    {
        public string TypeName = "";
        /// <summary>True when Ids are type ids whose instances changed (a type parameter).</summary>
        public bool IsType;
        public int Instances;
        public List<long> Ids = new List<long>();
        public List<ChangeRow> Rows = new List<ChangeRow>();
    }

    public sealed class CreatedItem
    {
        public long Id;
        public string Label = "";
    }

    public sealed class Finding
    {
        public string Title = "";
        public string Severity = "";
        public List<long> Ids = new List<long>();
        public string? Handle;
        public bool Fixable;
    }

    public sealed class ResultCardModel
    {
        public ResultKind Kind = ResultKind.None;
        public string Headline = "";
        public string Detail = "";
        public List<TypeGroup> Groups = new List<TypeGroup>();
        public List<CreatedItem> Created = new List<CreatedItem>();
        public List<Finding> Findings = new List<Finding>();
        public int Selected;
        public int VisibleInView;
        public int Mismatches;
        /// <summary>The headline's counts without the read-back note (the card shows that as a badge).</summary>
        public string Counts = "";
        public int CheckCount => Groups.Sum(g => g.Rows.Count(r => r.NeedsCheck));
        public int ChangeCount => Groups.Sum(g => g.Rows.Count);
        public int InstanceCount => Groups.Sum(g => g.Instances);

        public static ResultCardModel From(TurnEvidence ev, ReceiptLanguage lang, string? attachment = null)
        {
            var m = new ResultCardModel();
            if (ev == null) return m;
            var calls = ev.Calls;
            var source = string.IsNullOrWhiteSpace(attachment) ? "" : attachment!.Trim();

            var commits = calls.Where(c => c.Env.PhaseKind() == EnvelopePhaseKind.Commit
                && (c.Env.StatusKind() == EnvelopeStatusKind.Done || c.Env.StatusKind() == EnvelopeStatusKind.Partial)).ToList();
            // Net change: a value written and then put back (by a later call) is one row, or none.
            var net = new Dictionary<(long, string), (string? Before, string? After)>();
            var lastAt = new Dictionary<(long, string), int>();
            for (var i = 0; i < commits.Count; i++)
                foreach (var r in commits[i].Env.Changes?.Touched?.Modified ?? new List<FieldChangeDto>())
                {
                    var k = (r.Id, r.Field);
                    net[k] = net.TryGetValue(k, out var first) ? (first.Before, r.After) : (r.Before, r.After);
                    lastAt[k] = i;
                }
            var anyModified = net.Count > 0;
            var emitted = new HashSet<(long, string)>();
            var mismatched = new HashSet<(long, string)>();

            for (var i = 0; i < commits.Count; i++)
            {
                var c = commits[i];
                m.Mismatches += c.Env.Verified?.Mismatches?.Select(x => x.Id).Distinct().Count() ?? 0;
                foreach (var x in c.Env.Verified?.Mismatches ?? new List<MismatchDto>()) mismatched.Add((x.Id, x.Field));
                var touched = c.Env.Changes?.Touched;
                if (touched == null) continue;
                var modified = (touched.Modified ?? new List<FieldChangeDto>())
                    .Where(r => lastAt[(r.Id, r.Field)] == i && emitted.Add((r.Id, r.Field)))
                    .Select(r => new FieldChangeDto { Id = r.Id, Field = r.Field, Before = net[(r.Id, r.Field)].Before, After = r.After })
                    .Where(r => (r.Before ?? "") != (r.After ?? ""))
                    .ToList();
                if (modified.Count > 0) AddChanges(m, c.Call, c.Env, modified, source, lang);
                if (touched.Added != null) foreach (var a in touched.Added) m.Created.Add(new CreatedItem { Id = a.Id, Label = Label(c.Env, a.Id) });
            }

            // Rows to check come first; the rest stay in the order they were written.
            foreach (var g in m.Groups)
            {
                foreach (var r in g.Rows) r.NeedsCheck = r.Ids.Any(id => mismatched.Contains((id, r.Param)));
                g.Rows = g.Rows.OrderByDescending(r => r.NeedsCheck).ToList();
            }

            if (m.Groups.Count > 0) m.Kind = ResultKind.Changes;
            else if (anyModified && m.Created.Count == 0)
            {
                // Every value ended where it started.
                m.Kind = ResultKind.Changes;
                m.Headline = lang == ReceiptLanguage.Ms ? "Tiada perubahan bersih" : "No net change";
                m.Detail = lang == ReceiptLanguage.Ms ? "Nilai yang ditulis telah dipulihkan" : "Values written were put back";
                return m;
            }
            else if (m.Created.Count > 0) m.Kind = ResultKind.Created;
            else
            {
                foreach (var c in calls)
                {
                    if (c.Env.Warnings != null)
                        foreach (var w in c.Env.Warnings)
                            if (!string.IsNullOrWhiteSpace(w.Text))
                                m.Findings.Add(new Finding { Title = w.Text, Severity = w.Severity ?? "", Ids = w.Ids?.ToList() ?? new List<long>(), Fixable = w.Severity == WarningSeverity.Error });
                }
                foreach (var t in ev.Tables.Where(t => t.Kind == ResultTable.CheckKind))
                {
                    var fail = t.Columns.IndexOf("Fail"); var sev = t.Columns.IndexOf("Severity");
                    for (var i = 0; i < t.Rows.Count; i++)
                    {
                        var row = t.Rows[i];
                        if (fail < 0 || fail >= row.Count || !int.TryParse(row[fail], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) continue;
                        m.Findings.Add(new Finding { Title = row[0], Severity = sev >= 0 && sev < row.Count ? row[sev] : "", Handle = t.HandleOf(i), Fixable = false, Ids = new List<long>() });
                    }
                }
                if (m.Findings.Count > 0) m.Kind = ResultKind.Findings;
                else
                {
                    var sel = ev.Evidence.Where(e => (e.Selected ?? 0) > 0).OrderByDescending(e => e.Selected).FirstOrDefault();
                    if (sel != null) { m.Kind = ResultKind.Selection; m.Selected = sel.Selected ?? 0; m.VisibleInView = sel.VisibleInView ?? 0; }
                }
            }

            SetHeadline(m, lang);
            return m;
        }

        private static void AddChanges(ResultCardModel m, ToolCallDto call, Envelope env, List<FieldChangeDto> modified, string source, ReceiptLanguage lang)
        {
            var write = ToolResults.Write(env);
            var typeParam = write != null && write.TypeParam;
            var types = new Dictionary<long, (string Name, int Instances)>();
            if (typeParam)
                foreach (var t in write!.Types)
                    if (t.Id is long tid)
                        types[tid] = (t.Name ?? t.Family ?? tid.ToString(CultureInfo.InvariantCulture), t.InstancesInModel ?? 0);

            if (typeParam && types.Count > 0)
            {
                foreach (var row in modified)
                {
                    var name = types.TryGetValue(row.Id, out var ti) ? ti.Name : row.Id.ToString(CultureInfo.InvariantCulture);
                    var g = m.Groups.FirstOrDefault(x => x.TypeName == name);
                    if (g == null) m.Groups.Add(g = new TypeGroup { TypeName = name, IsType = true, Instances = types.TryGetValue(row.Id, out var t2) ? t2.Instances : 0 });
                    if (!g.Ids.Contains(row.Id)) g.Ids.Add(row.Id);
                    g.Rows.Add(new ChangeRow { Param = row.Field, Before = row.Before, After = row.After, Source = SourceLabels.Of(row.Source, source, lang), SourceInfo = row.Source, Ids = new List<long> { row.Id } });
                }
                return;
            }

            // Instance writes: one group per category (or one "elements" group), one row per field.
            var cat = write?.InstancesCategory;
            var groupName = string.IsNullOrWhiteSpace(cat) ? (Str(call.Args, "param") ?? "elements") : cat!;
            var grp = m.Groups.FirstOrDefault(x => x.TypeName == groupName && !x.IsType);
            if (grp == null) m.Groups.Add(grp = new TypeGroup { TypeName = groupName, IsType = false });
            foreach (var row in modified) if (!grp.Ids.Contains(row.Id)) grp.Ids.Add(row.Id);
            grp.Instances = grp.Ids.Count;
            foreach (var field in modified.Select(r => r.Field).Distinct())
            {
                var rows = modified.Where(r => r.Field == field).ToList();
                var befores = rows.Select(r => r.Before ?? "").Distinct().ToList();
                var afters = rows.Select(r => r.After ?? "").Distinct().ToList();
                // One source for the row when every changed element of the field has the same one; else the call's own.
                var sources = rows.Select(r => r.Source).Where(x => x != null).GroupBy(x => x!.Kind + "|" + x.Attachment + "|" + x.Page + "|" + x.Row).ToList();
                var rowSource = sources.Count == 1 && rows.All(r => r.Source != null) ? sources[0].First() : null;
                grp.Rows.Add(new ChangeRow
                {
                    Param = field,
                    SourceInfo = rowSource,
                    Before = befores.Count == 1 ? befores[0] : befores.Count.ToString(CultureInfo.InvariantCulture) + " values",
                    After = afters.Count == 1 ? afters[0] : afters.Count.ToString(CultureInfo.InvariantCulture) + " values",
                    Source = SourceLabels.Of(rowSource, source, lang),
                    Ids = rows.Select(r => r.Id).Distinct().ToList(),
                });
            }
        }

        private static void SetHeadline(ResultCardModel m, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            string N(int n) => n.ToString(CultureInfo.InvariantCulture);
            switch (m.Kind)
            {
                case ResultKind.Changes:
                {
                    var typeGroups = m.Groups.Count(g => g.IsType);
                    m.Headline = ms ? N(m.ChangeCount) + " perubahan diterapkan" : N(m.ChangeCount) + " change" + (m.ChangeCount == 1 ? "" : "s") + " applied";
                    var what = typeGroups > 0
                        ? (ms ? N(m.Groups.Count) + " jenis · " + N(m.InstanceCount) + " elemen" : N(m.Groups.Count) + " type" + (m.Groups.Count == 1 ? "" : "s") + " · " + N(m.InstanceCount) + " elements")
                        : (ms ? N(m.InstanceCount) + " elemen" : N(m.InstanceCount) + " element" + (m.InstanceCount == 1 ? "" : "s"));
                    m.Counts = what;
                    m.Detail = what + (ms ? " · dibaca balik dari Revit" : " · read back from Revit");
                    if (m.Mismatches > 0) m.Detail += ms ? " · " + N(m.Mismatches) + " tak sepadan" : " · " + N(m.Mismatches) + " mismatch" + (m.Mismatches == 1 ? "" : "es");
                    break;
                }
                case ResultKind.Created:
                    m.Headline = ms ? N(m.Created.Count) + " dicipta" : N(m.Created.Count) + " created";
                    m.Detail = ms ? "Undo membuang semuanya dalam satu langkah" : "Undo removes them in one step";
                    break;
                case ResultKind.Findings:
                {
                    var ids = m.Findings.Sum(f => f.Ids.Count);
                    var fixable = m.Findings.Where(f => f.Fixable).Sum(f => f.Ids.Count);
                    m.Headline = ids > 0
                        ? (ms ? N(ids) + " elemen gagal" : N(ids) + " element" + (ids == 1 ? "" : "s") + " fail")
                        : (ms ? N(m.Findings.Count) + " isu" : N(m.Findings.Count) + " issue" + (m.Findings.Count == 1 ? "" : "s"));
                    m.Detail = (ms ? N(m.Findings.Count) + " jenis isu" : N(m.Findings.Count) + " kind" + (m.Findings.Count == 1 ? "" : "s") + " of issue")
                        + (fixable > 0 ? (ms ? " · " + N(fixable) + " boleh dibaiki automatik" : " · " + N(fixable) + " fixable automatically") : "");
                    break;
                }
                case ResultKind.Selection:
                    m.Headline = ms ? N(m.Selected) + " dipilih" : N(m.Selected) + " selected";
                    m.Detail = m.VisibleInView > 0 ? (ms ? N(m.VisibleInView) + " kelihatan dalam view" : N(m.VisibleInView) + " visible in view") : "";
                    break;
            }
        }

        private static string Label(Envelope env, long id) => "Element " + id.ToString(CultureInfo.InvariantCulture);
        private static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
