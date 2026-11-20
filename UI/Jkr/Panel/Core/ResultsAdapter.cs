using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The pane's issues (IssueVm, owned by the scan / fix / audit-file code) as
    /// prototype items for the Results screen, and the per-section passing cells
    /// that are not issues (prototype BASE_PASS) from the BIM010 summary.
    /// </summary>
    public static class ResultsAdapter
    {
        /// <param name="lang">Language for the text the add-in writes (fix-failure
        /// reasons, element words); null = the pane's language.</param>
        /// <param name="notes">BM / EN pairs of the run's remarks (<see cref="NoteTexts"/>):
        /// an issue from the add-in's own spec evaluators, written in one language at scan
        /// time, shows its text in both. Null = as written.</param>
        public static PanelItem FromIssue(IssueVm i, string lang = null, IReadOnlyDictionary<string, Text> notes = null)
        {
            if (i == null) throw new ArgumentNullException(nameof(i));
            var human = i.NeedsPerson;
            var actual = !string.IsNullOrEmpty(i.Actual) ? i.Actual : i.FixOldValue ?? "";
            if (actual == "(none)" || actual == "(empty)") actual = "";
            var spec = IsSpecIssue(i);
            // The backend sends one language; the same text shows in BM and EN. A spec
            // evaluator's issue takes its title from the catalog and its text from the
            // run's BM / EN remarks, so it follows the BM / EN switch.
            // The BIM010 checker writes BM; the remarks the pane knows get their English (F15).
            var desc = spec ? Bilingual(i.Description ?? "", notes) : PaneText.AuditRemark(i.Description ?? "");
            return new PanelItem
            {
                Id = i.Id,
                Row = string.IsNullOrEmpty(i.BorangKey) ? "—" : i.BorangKey,
                Sec = i.BorangSection ?? "",
                Sev = i.Priority == IssuePriority.High ? "High" : i.Priority == IssuePriority.Low ? "Low" : "Med",
                V = human ? Verify.Human : Verify.Ai,
                Title = (spec ? SpecTitle(i.CheckId) : null) ?? Text.Same(i.Title ?? ""),
                Desc = desc,
                El = ElementLabel(i, lang),
                Req = (lang ?? PaneLanguage.Current) == PanelStrings.En ? (!string.IsNullOrEmpty(i.RequiredEn) ? i.RequiredEn : PaneText.AuditRuleEn(i.Required)) : i.Required ?? "",
                Act = string.IsNullOrEmpty(actual) ? Text.Same("—")
                    : new Text(actual, !string.IsNullOrEmpty(i.ActualEn) && actual == i.Actual ? i.ActualEn : actual),
                Prop = !human && i.AutoFixable && !string.IsNullOrEmpty(i.FixValue) ? i.FixValue : null,
                Cite = CiteOf(i),
                Fix = !human && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction) ? FixKind.Fix : FixKind.None,
                Hash = i.Id ?? "",
                Reason = human ? desc : null,
                FixError = PaneText.FixReason(i.FixError ?? "", lang),
                AiFix = i.AiFixCandidate,
                Status = human ? (i.Reviewed ? ItemStatus.Reviewed : ItemStatus.Open) : StatusOf(i.Status),
                Passed = i.Status == IssueStatus.Fixed && i.FixVerified,
                MarkedManual = i.Status == IssueStatus.ManualFixNeeded,
                CanMarkManual = !human && i.CanMarkManual,
            };
        }

        /// <summary>An issue from the add-in's own spec evaluators (CheckId "SPEC-A1.2").</summary>
        public static bool IsSpecIssue(IssueVm i) =>
            (i?.CheckId ?? "").StartsWith(SpecModelIssues.CheckIdPrefix, StringComparison.Ordinal);

        /// <summary>"A1.2 · Saiz pintu (NS §5.8.5)" / "A1.2 · Door size (NS §5.8.5)" for a
        /// "SPEC-A1.2" check id; null when the id is not a catalog check.</summary>
        public static Text SpecTitle(string checkId)
        {
            var raw = checkId ?? "";
            if (!raw.StartsWith(SpecModelIssues.CheckIdPrefix, StringComparison.Ordinal)) return null;
            var id = SpecReport.ResolveCheckId(raw.Substring(SpecModelIssues.CheckIdPrefix.Length));
            var c = id == null ? null : SpecCatalog.Find(id);
            return c == null ? null : new Text(c.Id + " · " + c.Title.Ms, c.Id + " · " + c.Title.En);
        }

        /// <summary>The issue's title in <paramref name="lang"/> (null = the pane's): a spec
        /// evaluator's issue from the catalog ("A1.1 · Ceiling height (NS §4.1.9)"), any
        /// other as it came. For toasts, which name the issue in the pane's language.</summary>
        public static string TitleIn(IssueVm i, string lang = null)
        {
            if (i == null) return "";
            var t = IsSpecIssue(i) ? SpecTitle(i.CheckId) : null;
            return t != null ? t.In(lang ?? PaneLanguage.Current) : i.Title ?? "";
        }

        /// <summary>Every remark of a run that has an English text, by its BM and by its
        /// EN text, as a BM / EN pair: all of them (<see cref="SpecCheckResult.AllNotes"/>),
        /// not only the few the Borang keeps, so an issue past the third (A7.2's fourth
        /// warning) follows the BM / EN switch too.</summary>
        public static Dictionary<string, Text> NoteTexts(SpecReport spec)
        {
            var d = new Dictionary<string, Text>(StringComparer.Ordinal);
            foreach (var c in spec?.Checks ?? new List<SpecCheckResult>())
            {
                if (c == null) continue;
                Add(d, c.AllNotes, c.AllNotesEn);
                Add(d, c.Notes, c.NotesEn);
            }
            return d;
        }

        private static void Add(Dictionary<string, Text> d, List<string> bm, List<string> english)
        {
            if (bm == null || english == null || bm.Count != english.Count) return;
            for (var k = 0; k < bm.Count; k++)
            {
                var ms = SpecReport.Clean(bm[k]);
                var en = SpecReport.Clean(english[k]);
                if (ms.Length == 0 || en.Length == 0 || ms == en) continue;
                var t = new Text(ms, en);
                if (!d.ContainsKey(ms)) d[ms] = t;
                if (!d.ContainsKey(en)) d[en] = t;
            }
        }

        /// <summary>
        /// Put the text the add-in writes in one language at load time (element words
        /// "Elemen" / "3 elemen", fix-failure reasons) in <paramref name="lang"/> again, on
        /// the pane's items, after a BM / EN switch: the items are not rebuilt, so their
        /// status, selection and the open detail stay. Items with no issue are left as they are.
        /// </summary>
        public static void Relabel(IEnumerable<PanelItem> items, IReadOnlyDictionary<string, IssueVm> issues, string lang)
        {
            if (items == null || issues == null) return;
            foreach (var x in items)
            {
                if (x?.Id == null || !issues.TryGetValue(x.Id, out var i) || i == null) continue;
                x.El = ElementLabel(i, lang);
                x.FixError = PaneText.FixReason(i.FixError ?? "", lang);
            }
        }

        // The fixed words SpecModelIssues.Description adds, BM ↔ EN.
        private static readonly (string Ms, string En)[] DescWords =
        {
            ("Keperluan: ", "Required: "), ("Elemen: ", "Elements: "), (" kesemuanya)", " in all)"),
        };

        /// <summary>A spec issue's text, line by line, in BM and EN: a line that is one of
        /// the run's remarks takes both texts; the words the add-in adds are swapped.
        /// A line with no known translation stays as written in both.</summary>
        public static Text Bilingual(string text, IReadOnlyDictionary<string, Text> notes)
        {
            var lines = (text ?? "").Split('\n');
            var ms = new List<string>();
            var en = new List<string>();
            foreach (var raw in lines)
            {
                var line = SpecReport.Clean(raw);
                if (notes != null && notes.TryGetValue(line, out var t)) { ms.Add(t.Ms); en.Add(t.En); continue; }
                string m = line, e = line;
                foreach (var w in DescWords) { m = m.Replace(w.En, w.Ms); e = e.Replace(w.Ms, w.En); }
                ms.Add(m);
                en.Add(e);
            }
            return new Text(string.Join("\n", ms), string.Join("\n", en));
        }

        /// <summary>Pane status → prototype status. Approved (old "accept") keeps the
        /// item out of Open and still prints it as not comply (the prototype's Ignored).
        /// A user's "mark as manual" stays Open (<see cref="PanelItem.MarkedManual"/>):
        /// still a fail, just not offered for a fix.</summary>
        public static ItemStatus StatusOf(IssueStatus s)
        {
            switch (s)
            {
                case IssueStatus.Fixed: return ItemStatus.Resolved;
                case IssueStatus.Ignored:
                case IssueStatus.Approved: return ItemStatus.Ignored;
                default: return ItemStatus.Open;
            }
        }

        /// <summary>prototype el: "Doors [204511] PT2p600a".</summary>
        public static string ElementLabel(IssueVm i, string lang = null)
        {
            var name = PaneText.ElementName(i.Element?.Name ?? "", lang);
            var id = i.Element?.Id;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(i.RevitCategory) && !name.StartsWith(i.RevitCategory, StringComparison.OrdinalIgnoreCase))
                parts.Add(i.RevitCategory);
            if (!string.IsNullOrEmpty(id) && id != "—") parts.Add("[" + id + "]");
            if (!string.IsNullOrEmpty(name) && name != "—") parts.Add(name);
            return parts.Count == 0 ? "—" : string.Join(" ", parts);
        }

        /// <summary>prototype cite: the spec clause / reference the check cites.</summary>
        public static string CiteOf(IssueVm i)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(i.BorangKey)) parts.Add("Borang " + i.BorangKey);
            var doc = i.Spec?.Doc ?? "";
            if (doc.StartsWith("doc", StringComparison.OrdinalIgnoreCase) && doc.Length > 3) parts.Add("Doc " + doc.Substring(3));
            if (!string.IsNullOrEmpty(i.Spec?.Clause)) parts.Add(i.Spec.Clause);
            if (i.Spec != null && i.Spec.Page > 0) parts.Add("p." + i.Spec.Page);
            if (parts.Count <= 1 && !string.IsNullOrEmpty(i.FixReference) && !i.FixReference.StartsWith("Test fixture", StringComparison.Ordinal))
                parts.Add(i.FixReference);
            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        }

        /// <summary>
        /// Passing cells per section that are not items (prototype BASE_PASS): the
        /// summary's passing cells minus the items already counted as passed (a fix
        /// confirmed by the scan is a passing cell there too). Never below 0.
        /// </summary>
        public static Dictionary<string, int> BasePass(Bim010Summary summary, IEnumerable<PanelItem> items)
        {
            var list = (items ?? Enumerable.Empty<PanelItem>()).ToList();
            return PanelCore.Secs.ToDictionary(s => s, s =>
            {
                var pass = summary?.Section(s)?.Pass ?? 0;
                var passedItems = list.Count(x => x.Sec == s && !x.IsHuman && x.Passed);
                return Math.Max(0, pass - passedItems);
            });
        }
    }
}
