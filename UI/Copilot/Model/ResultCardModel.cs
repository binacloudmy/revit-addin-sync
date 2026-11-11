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
        /// <summary>The value was written and then put back by a later call: After is what the element holds now
        /// (its old value), <see cref="Intended"/> is what was written.</summary>
        public bool ChangedBack;
        /// <summary>For a changed-back row: the one value that was written to all its elements, or null when they
        /// differed (then only <see cref="IntendedLabel"/> is set, and the row cannot be written again as one value).</summary>
        public string? Intended;
        /// <summary>For a changed-back row whose written values differed: "3 values".</summary>
        public string? IntendedLabel;
        /// <summary>What to show for the written value of a changed-back row.</summary>
        public string IntendedText => Intended ?? IntendedLabel ?? "";
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
        /// <summary>Every row of this type was written and then put back: it holds its old values.</summary>
        public bool ChangedBack => Rows.Count > 0 && Rows.All(r => r.ChangedBack);
        /// <summary>At least one row was written and put back (all of them, or only some).</summary>
        public bool AnyChangedBack => Rows.Any(r => r.ChangedBack);
        /// <summary>The drafter chose "Leave as is" for this type's put-back values (a view state, not written anywhere).</summary>
        public bool LeftAsIs;
        /// <summary>The changed-back rows can all be written again: each has one value to write.</summary>
        public bool CanWriteAgain => AnyChangedBack && !LeftAsIs && Rows.Where(r => r.ChangedBack).All(r => !string.IsNullOrEmpty(r.Intended) && r.Ids.Count > 0);
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
        /// <summary>A Created card's counts by category; null when the envelopes carry none.</summary>
        public CreatedSummary? CreatedBy;
        public List<Finding> Findings = new List<Finding>();
        public int Selected;
        public int VisibleInView;
        /// <summary>The turn isolated the result in the active view (a run_revit_code call whose intent or code isolates):
        /// the card then offers "Reset isolate".</summary>
        public bool Isolated;
        /// <summary>The model the result came from (SnapshotService.ActiveDocKey when the turn was sent); "" when not known.
        /// A button that opens a sheet or view checks it against the model that is open then.</summary>
        public string DocKey = "";
        /// <summary>The first type or element group, or null when the card has none ("No net change").</summary>
        public TypeGroup? FirstGroup => Groups.Count > 0 ? Groups[0] : null;
        /// <summary>How long the turn took, as the activity card shows it ("56s"); "" when not known.</summary>
        public string Elapsed = "";
        /// <summary>What the server says to look at (turn_end.result.checks), each with its answer buttons.</summary>
        public List<CheckItem> Checks = new List<CheckItem>();
        /// <summary>Up to three next steps the server suggests (turn_end.result.next).</summary>
        public List<string> Next = new List<string>();
        /// <summary>The rows of what the turn made or found and the Excel mapping (turn_end.result.items / mapping); null when none.</summary>
        public ResultItems? Items;

        /// <summary>Attach the server's own result: the checks with their buttons and the next steps.</summary>
        public ResultCardModel WithServerResult(BinaVibe.Copilot.Contracts.TurnResultDto? result)
        {
            Checks = CheckItems.From(result);
            Next = CheckItems.NextOf(result);
            Items = ResultItems.From(result);
            // The envelopes gave no card (a turn that made things through code, no touched ids), but the server's own result
            // has rows, checks or next steps: show those under the server's one-sentence reply rather than dropping them.
            if (Kind == ResultKind.None && result != null && (Items != null || Checks.Count > 0 || Next.Count > 0))
            {
                Kind = ResultKind.Created;
                Headline = (result.Reply ?? "").Trim();
                Detail = "";
            }
            return this;
        }
        public int Mismatches;
        /// <summary>The headline's counts without the read-back note (the card shows that as a badge).</summary>
        public string Counts = "";
        public int CheckCount => Groups.Sum(g => g.Rows.Count(r => r.NeedsCheck));
        /// <summary>Rows that hold a change now (changed-back rows are not changes).</summary>
        public int ChangeCount => Groups.Sum(g => g.Rows.Count(r => !r.ChangedBack));
        public int InstanceCount => Groups.Where(g => !g.ChangedBack).Sum(g => g.Instances);
        /// <summary>Types (or element groups) whose written values were all put back.</summary>
        public int ChangedBackTypes => Groups.Count(g => g.ChangedBack);
        /// <summary>Types whose put-back values the drafter has not yet accepted with "Leave as is".</summary>
        public int ChangedBackOpen => Groups.Count(g => g.ChangedBack && !g.LeftAsIs);
        /// <summary>Redraw the headline after the card's state changed (a type left as is).</summary>
        public void RefreshHeadline(ReceiptLanguage lang) { if (Kind == ResultKind.Changes) SetHeadline(this, lang); }
        public int ChangedBackRows => Groups.Sum(g => g.Rows.Count(r => r.ChangedBack));

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
            var firstAt = new Dictionary<(long, string), int>();
            var firstAfter = new Dictionary<(long, string), string?>();
            for (var i = 0; i < commits.Count; i++)
                foreach (var r in commits[i].Env.Changes?.Touched?.Modified ?? new List<FieldChangeDto>())
                {
                    var k = (r.Id, r.Field);
                    if (!firstAt.ContainsKey(k)) { firstAt[k] = i; firstAfter[k] = r.After; }
                    net[k] = net.TryGetValue(k, out var first) ? (first.Before, r.After) : (r.Before, r.After);
                    lastAt[k] = i;
                }
            m.Isolated = IsolatedInView(calls);
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
                    .Select(r => new FieldChangeDto { Id = r.Id, Field = r.Field, Before = net[(r.Id, r.Field)].Before, After = r.After, Source = r.Source })
                    .Where(r => (r.Before ?? "") != (r.After ?? ""))
                    .ToList();
                if (modified.Count > 0) AddChanges(m, c.Call, c.Env, modified, source, lang);
                if (touched.Added != null) foreach (var a in touched.Added) m.Created.Add(new CreatedItem { Id = a.Id, Label = Label(c.Env, a.Id) });
            }

            // Written and then put back (by a later call): the type holds its old values. One row each,
            // marked, so the card can say "Changed back" and offer to write them again.
            for (var i = 0; i < commits.Count; i++)
            {
                var back = new List<FieldChangeDto>();
                var wrote = new Dictionary<(long, string), string?>();
                foreach (var kv in net)
                {
                    var k = kv.Key;
                    if (firstAt[k] != i) continue;
                    if ((kv.Value.Before ?? "") != (kv.Value.After ?? "")) continue;          // a net change: shown above
                    if ((firstAfter[k] ?? "") == (kv.Value.Before ?? "")) continue;           // never changed anything
                    back.Add(new FieldChangeDto { Id = k.Item1, Field = k.Item2, Before = kv.Value.Before, After = kv.Value.Before });
                    wrote[k] = firstAfter[k];
                }
                if (back.Count > 0) AddChanges(m, commits[i].Call, commits[i].Env, back, source, lang, wrote);
            }

            // An element group counts the elements that hold a change, not those whose value was put back.
            foreach (var g in m.Groups.Where(g => !g.IsType))
            {
                var live = g.Rows.Where(r => !r.ChangedBack).SelectMany(r => r.Ids).Distinct().Count();
                g.Instances = live > 0 ? live : g.Ids.Count;
            }

            // Rows to check come first; the rest stay in the order they were written.
            foreach (var g in m.Groups)
            {
                foreach (var r in g.Rows) r.NeedsCheck = !r.ChangedBack && r.Ids.Any(id => mismatched.Contains((id, r.Param)));
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
            else if (m.Created.Count > 0) { m.Kind = ResultKind.Created; m.CreatedBy = CreatedSummary.From(commits.Select(c => c.Env), source, lang); }
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

            // The turn isolated the view but left nothing else to show (code that only isolates: no selection, no writes):
            // the card is still there, so the drafter can reset the isolate.
            if (m.Kind == ResultKind.None && m.Isolated) m.Kind = ResultKind.Selection;

            SetHeadline(m, lang);
            return m;
        }

        /// <summary>The one category the call modified, or null when it touched none or several.</summary>
        internal static string? OnlyCategory(Dictionary<string, int>? modifiedByCategory)
        {
            var names = (modifiedByCategory ?? new Dictionary<string, int>()).Where(kv => kv.Value > 0).Select(kv => kv.Key).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            return names.Count == 1 ? names[0] : null;
        }

        /// <summary>A successful run_revit_code whose intent or code isolates elements in the view.</summary>
        internal static bool IsolatedInView(IEnumerable<(ToolCallDto Call, Envelope Env)> calls)
        {
            foreach (var c in calls)
            {
                if (c.Call.Tool != ToolNames.RunRevitCode || !c.Env.Ok) continue;
                var text = (Str(c.Call.Args, "intent") ?? "") + " " + (Str(c.Call.Args, "code") ?? "");
                if (text.IndexOf("IsolateElementsTemporary", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("TemporaryHideIsolate", StringComparison.OrdinalIgnoreCase) >= 0
                    || System.Text.RegularExpressions.Regex.IsMatch(text, @"\bisolat(e|ed|ing|es)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;
            }
            return false;
        }

        private static void AddChanges(ResultCardModel m, ToolCallDto call, Envelope env, List<FieldChangeDto> modified, string source, ReceiptLanguage lang,
            Dictionary<(long, string), string?>? changedBack = null)
        {
            // The one value written to every element of the row, or null when they differed.
            string? Wrote(IEnumerable<FieldChangeDto> rows)
            {
                if (changedBack == null) return null;
                var vals = rows.Select(r => changedBack.TryGetValue((r.Id, r.Field), out var w) ? w ?? "" : "").Distinct().ToList();
                return vals.Count == 1 ? vals[0] : null;
            }
            string? WroteLabel(IEnumerable<FieldChangeDto> rows)
            {
                if (changedBack == null || Wrote(rows) != null) return null;
                var n = rows.Select(r => changedBack.TryGetValue((r.Id, r.Field), out var w) ? w ?? "" : "").Distinct().Count();
                return n.ToString(CultureInfo.InvariantCulture) + " values";
            }

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
                    g.Rows.Add(new ChangeRow { Param = row.Field, Before = row.Before, After = row.After, Source = SourceLabels.Of(row.Source, source, lang), SourceInfo = row.Source, Ids = new List<long> { row.Id },
                        ChangedBack = changedBack != null, Intended = Wrote(new[] { row }), IntendedLabel = WroteLabel(new[] { row }) });
                }
                return;
            }

            // Instance writes: one group per category (or one "elements" group), one row per field.
            var cat = write?.InstancesCategory;
            if (string.IsNullOrWhiteSpace(cat)) cat = OnlyCategory(env.Changes?.TouchedByCategory?.ModifiedByCategory);
            // Never the parameter's name: it is not what the elements are.
            var groupName = string.IsNullOrWhiteSpace(cat) ? "Elements" : cat!;
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
                    ChangedBack = changedBack != null,
                    Intended = Wrote(rows),
                    IntendedLabel = WroteLabel(rows),
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
                    if (m.ChangeCount == 0 && m.ChangedBackRows > 0)
                    {
                        // Everything written was put back.
                        m.Headline = ms ? "Tiada perubahan bersih" : "No net change";
                        m.Counts = m.Detail = ms ? N(m.ChangedBackTypes) + " dipulihkan balik" : N(m.ChangedBackRows) + " value" + (m.ChangedBackRows == 1 ? "" : "s") + " written, then put back";
                        break;
                    }
                    var typeGroups = m.Groups.Count(g => g.IsType && !g.ChangedBack);
                    m.Headline = ms ? N(m.ChangeCount) + " perubahan diterapkan" : N(m.ChangeCount) + " change" + (m.ChangeCount == 1 ? "" : "s") + " applied";
                    var live = m.Groups.Count(g => !g.ChangedBack);
                    var what = typeGroups > 0
                        ? (ms ? N(live) + " jenis · " + N(m.InstanceCount) + " elemen" : N(live) + " type" + (live == 1 ? "" : "s") + " · " + N(m.InstanceCount) + " elements")
                        : (ms ? N(m.InstanceCount) + " elemen" : N(m.InstanceCount) + " element" + (m.InstanceCount == 1 ? "" : "s"));
                    if (m.ChangedBackOpen > 0) what += ms ? " · " + N(m.ChangedBackOpen) + " dipulihkan balik" : " · " + N(m.ChangedBackOpen) + " changed back";
                    m.Counts = what;
                    m.Detail = what + (ms ? " · dibaca balik dari Revit" : " · read back from Revit");
                    if (m.Mismatches > 0) m.Detail += ms ? " · " + N(m.Mismatches) + " tak sepadan" : " · " + N(m.Mismatches) + " mismatch" + (m.Mismatches == 1 ? "" : "es");
                    break;
                }
                case ResultKind.Created:
                    m.Headline = m.CreatedBy?.Headline(lang) ?? (ms ? N(m.Created.Count) + " dicipta" : N(m.Created.Count) + " created");
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
                    m.Headline = m.Selected == 0 && m.Isolated ? (ms ? "View diasingkan" : "View isolated")
                               : ms ? N(m.Selected) + " dipilih" : N(m.Selected) + " selected";
                    m.Detail = m.VisibleInView > 0 ? (ms ? N(m.VisibleInView) + " kelihatan dalam view" : N(m.VisibleInView) + " visible in view") : "";
                    break;
            }
        }

        private static string Label(Envelope env, long id) => "Element " + id.ToString(CultureInfo.InvariantCulture);
        private static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
