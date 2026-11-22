using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.Services.JkrSpec
{
    // Spec checks for Borang sections B (Project Information) and C (Project Browser
    // Organization) — see SpecEval.cs for the contract. The rules live in the nested
    // SpecBC class so their helpers cannot clash with the other section files.
    public static partial class SpecChecks
    {
        static partial void RegisterBC(Dictionary<string, SpecRule> rules)
        {
            rules["B1.1"] = SpecBC.B1_1;
            rules["B1.2"] = SpecBC.B1_2;
            rules["B1.3"] = SpecBC.B1_3;
            rules["C1.0.1"] = SpecBC.C1_0_1;
            rules["C1.1.1"] = SpecBC.C1_1_1;
            rules["C1.1.2"] = SpecBC.C1_1_2;
            rules["C1.1.3"] = SpecBC.C1_1_3;
            rules["C1.2.1"] = SpecBC.C1_2_1;
            rules["C1.3.1"] = SpecBC.C1_3_1;
            rules["C1.4.1"] = SpecBC.C1_4_1;
            rules["C1.5.1"] = SpecBC.C1_5_1;
            rules["C1.5.2"] = SpecBC.C1_5_2;
            rules["C1.6.1"] = SpecBC.C1_6_1;
            rules["C1.6.2"] = SpecBC.C1_6_2;
            rules["C1.6.3"] = SpecBC.C1_6_3;
            rules["C2.1.1"] = SpecBC.C2_1_1;
            rules["C3.1.1"] = SpecBC.C3_1_1;
            rules["C3.1.2"] = SpecBC.C3_1_2;
            rules["C4.1.1"] = SpecBC.C4_1_1;
            rules["C4.1.2"] = SpecBC.C4_1_2;
            rules["C4.1.3"] = SpecBC.C4_1_3;
            rules["C4.2.1"] = SpecBC.C4_2_1;
            rules["C4.2.2"] = SpecBC.C4_2_2;
            rules["C4.2.3"] = SpecBC.C4_2_3;
            rules["C5.1.1"] = SpecBC.C5_1_1;
            rules["C5.1.2"] = SpecBC.C5_1_2;
            rules["C5.1.3"] = SpecBC.C5_1_3;
        }

        static partial void BackendBC(Dictionary<string, string> reasons)
        {
            reasons["C5.1.2"] = "The project's BPEP (its list of model files) is attached on the backend; the add-in has no BPEP to compare the link file names with.";
        }
    }

    /// <summary>Where a view's set (WIP / PBT / BOMBA / Dokumen) was found.</summary>
    internal enum ViewSetSource { None, Folder, Parameter, Template, Name }

    /// <summary>The sets a view belongs to and how that was decided.</summary>
    internal sealed class ViewSetHit
    {
        public static readonly ViewSetHit NoSet = new ViewSetHit(new List<string>(), ViewSetSource.None, "");

        public ViewSetHit(List<string> sets, ViewSetSource source, string by)
        {
            Sets = sets; Source = source; By = by ?? "";
        }

        /// <summary>The sets (SpecViewSets.Names spelling); empty = in none of them.</summary>
        public List<string> Sets { get; }
        public ViewSetSource Source { get; }
        /// <summary>The folder's parameter ("View_jkr_pit") or the template's name; "" otherwise.</summary>
        public string By { get; }

        public bool In(string set) => Sets.Any(s => string.Equals(s, set, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which of the JKR view sets (WIP / PBT / BOMBA / Dokumen, spec C 1.2–1.5 and E) a view
    /// belongs to — the one rule C1.2.1–C1.5.2 and E.1 share. A real JKR model organises its
    /// Project Browser ("00a_All") by the project parameter View_jkr_pit — top folders
    /// 01w_WIP, 03p_Persembahan, 03t_Tender, 05b_Bomba, 06p_PBT, 07d_Dokumen — and names its
    /// view templates after the set (jkrAR_07d_Dokumen_(Pelan Siling)). So, in order:
    /// 1. a browser folder holding a set word ("01w_WIP", "06p_PBT");
    /// 2. View_jkr_pit: a set word → that set; any other value (03p_Persembahan) → no set,
    ///    and the view's name is not looked at;
    /// 3. the view template's name holding a set word;
    /// 4. the view's name, as before folders were read.
    /// The first that answers decides: a view in the 07d_Dokumen folder is not also a WIP
    /// view because its name says "WIP". Set words match whole (letters on neither side),
    /// case-insensitively: "01w_WIP" is WIP, "Wipe" is not.
    /// Real model, 16 Nov 2026 (jkrAR27_2b_(BEsm_19-001)_A-PTBR_w01): by name only, 1 of the
    /// 15 WIP views and 2 of the 66 Dokumen views were found.
    /// </summary>
    internal static class SpecViewSets
    {
        /// <summary>The JKR project parameter that names a view's set ("01w_WIP", "07d_Dokumen" …);
        /// SpecModelReader reads it into SpecView.SetParam.</summary>
        internal const string SetParam = "View_jkr_pit";

        /// <summary>The sets, spelled as the checks show them.</summary>
        internal static readonly string[] Names = { "BOMBA", "PBT", "Dokumen", "WIP" };

        private static readonly Regex[] Tokens = Names
            .Select(n => new Regex(@"(?<![A-Za-z])" + n + @"(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();

        /// <summary>The sets whose word <paramref name="text"/> holds, in <see cref="Names"/> order.</summary>
        internal static List<string> SetsIn(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            for (var i = 0; i < Names.Length; i++)
                if (Tokens[i].IsMatch(text)) list.Add(Names[i]);
            return list;
        }

        private static bool Blank(string folder) =>
            string.IsNullOrWhiteSpace(folder) || folder.Trim() == "???";

        private static string[] Split(string path) =>
            string.IsNullOrWhiteSpace(path) ? new string[0] : path.Split(new[] { " / " }, StringSplitOptions.None);

        private static ViewSetHit FromFolder(SpecView v)
        {
            var folders = Split(v.FolderPath);
            var by = Split(v.FolderBy);
            for (var i = 0; i < folders.Length; i++)
            {
                if (Blank(folders[i])) continue;
                var param = i < by.Length ? by[i].Trim() : "";
                var sets = SetsIn(folders[i]);
                // A View_jkr_pit folder decides even when it names another set (03p_Persembahan).
                if (sets.Count > 0 || string.Equals(param, SetParam, StringComparison.OrdinalIgnoreCase))
                    return new ViewSetHit(sets, ViewSetSource.Folder, param);
            }
            return null;
        }

        private static ViewSetHit FromParameter(SpecView v)
        {
            var p = (v.SetParam ?? "").Trim();
            if (Blank(p)) return null;
            return new ViewSetHit(SetsIn(p), ViewSetSource.Parameter, SetParam);
        }

        private static ViewSetHit FromTemplate(SpecView v)
        {
            var sets = SetsIn(v.TemplateName);
            return sets.Count > 0 ? new ViewSetHit(sets, ViewSetSource.Template, v.TemplateName.Trim()) : null;
        }

        private static ViewSetHit FromName(SpecView v)
        {
            var sets = SetsIn(v.Name);
            return sets.Count > 0 ? new ViewSetHit(sets, ViewSetSource.Name, "") : null;
        }

        /// <summary>
        /// The view's sets: folder, then View_jkr_pit, then template name, then view name.
        /// <paramref name="templateLast"/> (E.1, which judges the template) looks at the view's
        /// name before its template: a set read from the template would always "match" it.
        /// </summary>
        internal static ViewSetHit Of(SpecView v, bool templateLast = false)
        {
            if (v == null) return ViewSetHit.NoSet;
            var hit = FromFolder(v) ?? FromParameter(v);
            if (hit == null)
                hit = templateLast ? FromName(v) ?? FromTemplate(v) : FromTemplate(v) ?? FromName(v);
            return hit ?? ViewSetHit.NoSet;
        }

        /// <summary>The model's views in <paramref name="set"/>.</summary>
        internal static List<SpecView> Views(SpecModel m, string set) =>
            m.Views.Where(v => Of(v).In(set)).ToList();

        /// <summary>"ikut folder Project Browser (View_jkr_pit)" / "by browser folder View_jkr_pit".</summary>
        internal static string SourceText(ViewSetSource source, string by, bool en)
        {
            switch (source)
            {
                case ViewSetSource.Folder:
                    return en ? "by browser folder" + (by.Length > 0 ? " " + by : "")
                              : "ikut folder Project Browser" + (by.Length > 0 ? " (" + by + ")" : "");
                case ViewSetSource.Parameter:
                    return en ? "by parameter " + by : "ikut parameter " + by;
                case ViewSetSource.Template:
                    return en ? "by template name" : "ikut nama template";
                case ViewSetSource.Name:
                    return en ? "by name" : "ikut nama view";
                default:
                    return en ? "in no set" : "tiada set";
            }
        }

        /// <summary>How the views of a set were found, most first: "ikut folder Project Browser
        /// (View_jkr_pit) 15, ikut nama view 1"; "" for no views.</summary>
        internal static string HowFound(IEnumerable<SpecView> views, bool en)
        {
            var groups = views.Select(v => Of(v)).GroupBy(h => SourceText(h.Source, h.By, en))
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
            return string.Join(", ", groups.Select(g => g.Key + " " + g.Count()));
        }
    }

    /// <summary>
    /// The evaluators for spec sections B and C (JKR-Model-Audit-Spec.xlsx, "Peraturan comply";
    /// detail in model-audit-spec.md §B, §C). Pure: they read a <see cref="SpecModel"/> only.
    /// Data the snapshot lacks gives "could not be judged" (or no result), never a pass.
    /// </summary>
    internal static class SpecBC
    {
        // ─── shared helpers ───

        private static bool Has(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && !string.IsNullOrEmpty(needle)
            && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool Same(string a, string b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        private static string Show(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

        /// <summary>The reader reported it could not read this part ("Sheets: …").</summary>
        private static string ReadError(SpecModel m, params string[] parts)
        {
            foreach (var e in m.ReadErrors)
                foreach (var p in parts)
                    if (!string.IsNullOrEmpty(e) && e.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase)) return e.Trim();
            return null;
        }

        /// <summary><paramref name="name"/> occurs in <paramref name="text"/> as a whole word run
        /// (no letter or digit right before or after it): "Aras 1" is in "3D Aras 1" but not in "3D Aras 10".</summary>
        internal static bool ContainsWord(string text, string name)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();
            var start = 0;
            while (start <= text.Length - n.Length)
            {
                var i = text.IndexOf(n, start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return false;
                var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                var end = i + n.Length;
                var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (before && after) return true;
                start = i + 1;
            }
            return false;
        }

        private static List<SpecLevel> StoryLevels(SpecModel m) =>
            m.Levels.Where(l => l.IsStory && !string.IsNullOrWhiteSpace(l.Name)).OrderBy(l => l.Elevation).ToList();

        private static bool IsKind(SpecView v, string kind) => Same(v.Kind, kind);

        private static string KindBm(string kind)
        {
            switch ((kind ?? "").Trim())
            {
                case "FloorPlan": return "floor plan";
                case "CeilingPlan": return "ceiling plan";
                case "Elevation": return "elevation";
                case "Section": return "section";
                case "ThreeD": return "3D";
                case "DraftingView": return "drafting view";
                case "Detail": return "detail";
                case "Legend": return "legend";
                case "AreaPlan": return "area plan";
                default: return string.IsNullOrWhiteSpace(kind) ? "lain-lain" : kind.Trim();
            }
        }

        private static string KindEn(string kind) => KindBm(kind) == "lain-lain" ? "other" : KindBm(kind);

        private static string Tick(bool ok) => ok ? "✓" : "✗";

        /// <summary>An unjudged result when the views were not read (a read error, or none at all); null when they were.</summary>
        private static SpecResult ViewsUnread(SpecModel m, string id)
        {
            var err = ReadError(m, "Views", "View");
            if (err != null) return SpecChecks.Unjudged(id, "View tidak dapat dibaca: " + err).En("Views could not be read: " + err);
            if (m.Views.Count == 0) return SpecChecks.Unjudged(id, "Tiada view dibaca daripada model").En("No views were read from the model");
            return null;
        }

        // ═══════════════════════ B — Project Information ═══════════════════════

        /// <summary>Revit's own placeholder values in Project Information and on sheets.</summary>
        private static readonly HashSet<string> RevitDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Enter address here", "Owner", "Project Name", "Project Number", "Project Status",
            "Issue Date", "Author", "Checker", "Designer", "Approver", "Unnamed", "Client Name",
            "Building Name", "Organization Name", "Organization Description",
        };

        /// <summary>"" = filled; "kosong" = empty or no such parameter; "nilai lalai Revit" = a Revit placeholder.</summary>
        private static string FieldStatus(bool found, string param, string value, out string en)
        {
            var v = (value ?? "").Trim();
            if (!found) { en = "no such parameter"; return "tiada parameter"; }
            if (v.Length == 0) { en = "empty"; return "kosong"; }
            if (RevitDefaults.Contains(v) || Same(v, param)) { en = "Revit default value"; return "nilai lalai Revit"; }
            en = ""; return "";
        }

        private sealed class PiRow
        {
            public PbField Field;
            public string Value = "";
            public string Status = "";     // "" = filled
            public string StatusEn = "";
            public bool Filled => Status.Length == 0;
        }

        private sealed class PiCheck
        {
            public SpecResult Unjudged;    // set when the check cannot run
            public int Loi;                // 0 = project LOi unknown
            public string Pages = "";
            public bool Partial;           // LOi unknown, or the book table has gaps
            public List<PiRow> Rows = new List<PiRow>();
        }

        /// <summary>
        /// The Project Information fields the Buku Parameter marks at the project's LOi, for the
        /// model's discipline (AR p.30–34, ST p.120, ME p.161, EL p.209), with their values.
        /// LOi unknown → only the fields marked at every LOi, and the check is partial.
        /// </summary>
        private static PiCheck ProjectInfo(SpecModel m, string id)
        {
            var c = new PiCheck();
            var err = ReadError(m, "ProjectInfo", "Project Information");
            if (err != null || m.ProjectInfo.Count == 0)
            {
                c.Unjudged = SpecChecks.Unjudged(id, "Project Information tidak dibaca" + (err != null ? ": " + err : ""))
                    .En("Project Information was not read" + (err != null ? ": " + err : ""));
                return c;
            }
            var fields = SpecPbData.ProjectInfoFor(m.Discipline);
            if (fields == null)
            {
                c.Unjudged = SpecChecks.Unjudged(id, "Buku Parameter tiada senarai Project Information untuk disiplin " + m.Discipline)
                    .En("The Buku Parameter has no Project Information list for discipline " + m.Discipline);
                return c;
            }
            c.Pages = SpecPbData.ProjectInfoPages(m.Discipline);
            c.Loi = SpecPbData.LoiOf(m.Lod);
            var required = c.Loi > 0 ? SpecPbData.RequiredAt(fields, c.Loi) : SpecPbData.RequiredAtEveryLoi(fields);
            c.Partial = c.Loi == 0 || SpecPbData.HasGaps(fields);
            foreach (var f in required)
            {
                var found = SpecPbData.TryFind(m.ProjectInfo, f.Name, out var pname, out var value);
                var status = FieldStatus(found, found ? pname : f.Name, value, out var en);
                c.Rows.Add(new PiRow { Field = f, Value = value ?? "", Status = status, StatusEn = en });
            }
            return c;
        }

        private static string LoiBm(PiCheck c) => c.Loi > 0 ? "LOi " + c.Loi : "semua LOi (LOD projek tidak diketahui)";
        private static string LoiEn(PiCheck c) => c.Loi > 0 ? "LOi " + c.Loi : "every LOi (project LOD unknown)";

        /// <summary>
        /// B1.1 — every Project Information field the Buku Parameter marks mandatory at the
        /// project LOi (PB p.30–34 for AR) holds a real value: one fail per field that is
        /// empty, missing or still a Revit placeholder, as `Parameter | Wajib pada LOi | Nilai | Status`.
        /// Full when the LOi is known and the book table has no gaps; otherwise fail-only.
        /// </summary>
        internal static IEnumerable<SpecResult> B1_1(SpecModel m)
        {
            const string id = "B1.1";
            var c = ProjectInfo(m, id);
            if (c.Unjudged != null) { yield return c.Unjudged; yield break; }
            var cov = c.Partial ? SpecCoverage.FailOnly : SpecCoverage.Full;
            var bad = c.Rows.Where(r => !r.Filled).ToList();
            foreach (var r in bad)
            {
                yield return SpecChecks.Fail(id,
                        r.Field.Name + " | wajib LOi " + r.Field.LoiRange + " | " + Show(r.Value) + " | " + r.Status,
                        elementName: "Project Information · " + r.Field.Name, actual: Show(r.Value),
                        required: "diisi (wajib LOi " + r.Field.LoiRange + ", PB p." + r.Field.Page + ")",
                        key: r.Field.Name, coverage: cov)
                    .En(r.Field.Name + " | mandatory at LOi " + r.Field.LoiRange + " | " + Show(r.Value) + " | " + r.StatusEn);
            }
            if (bad.Count == 0)
            {
                yield return SpecChecks.Pass(id,
                        c.Rows.Count + "/" + c.Rows.Count + " field wajib " + LoiBm(c) + " diisi (" + c.Pages + ")", c.Rows.Count, cov)
                    .En(c.Rows.Count + "/" + c.Rows.Count + " fields mandatory at " + LoiEn(c) + " filled (" + c.Pages + ")");
            }
        }

        /// <summary>
        /// B1.2 — other disciplines (ST / ME / EL): one "Tidak patuh" row for the model's
        /// discipline with only the mandatory fields not yet filled (PB p.120 / 161 / 209).
        /// Not applicable to an AR (or CD) model: the check concerns ST / ME / EL.
        /// </summary>
        internal static IEnumerable<SpecResult> B1_2(SpecModel m)
        {
            const string id = "B1.2";
            var d = m.Discipline;
            if (d != "ST" && d != "ME" && d != "EL")
            {
                yield return SpecChecks.NotApplicable(id, "Model " + d + " — B1.2 hanya untuk model ST / ME / EL")
                    .En(d + " model — B1.2 applies to ST / ME / EL models only");
                yield break;
            }
            var c = ProjectInfo(m, id);
            if (c.Unjudged != null) { yield return c.Unjudged; yield break; }
            var cov = c.Partial ? SpecCoverage.FailOnly : SpecCoverage.Full;
            var bad = c.Rows.Where(r => !r.Filled).Select(r => r.Field.Name).ToList();
            if (bad.Count > 0)
            {
                yield return SpecChecks.Fail(id,
                        d + ": Tidak patuh — " + bad.Count + " field belum diisi: " + string.Join(", ", bad),
                        elementName: "Project Information (" + d + ")", actual: bad.Count + " field kosong",
                        required: "field wajib " + LoiBm(c) + " (" + c.Pages + ") diisi", key: d, coverage: cov)
                    .En(d + ": Not compliant — " + bad.Count + " fields not filled: " + string.Join(", ", bad));
            }
            else
            {
                yield return SpecChecks.Pass(id, d + ": semua " + c.Rows.Count + " field wajib " + LoiBm(c) + " diisi", c.Rows.Count, cov)
                    .En(d + ": all " + c.Rows.Count + " fields mandatory at " + LoiEn(c) + " filled");
            }
        }

        /// <summary>B1.3 — summary, list only: "n/m field diisi (x%)" over the fields B1.1 counts.</summary>
        internal static IEnumerable<SpecResult> B1_3(SpecModel m)
        {
            const string id = "B1.3";
            var c = ProjectInfo(m, id);
            if (c.Unjudged != null) yield break;
            var n = c.Rows.Count(r => r.Filled);
            var total = c.Rows.Count;
            var pct = total == 0 ? 0 : (int)Math.Round(100.0 * n / total, MidpointRounding.AwayFromZero);
            yield return SpecChecks.Listing(id, n + "/" + total + " field diisi (" + pct + "%) — wajib " + LoiBm(c) + ", " + c.Pages, total)
                .En(n + "/" + total + " fields filled (" + pct + "%) — mandatory at " + LoiEn(c) + ", " + c.Pages);
        }

        // ═══════════════════════ C1 — Views ═══════════════════════

        /// <summary>C1.0.1 — parent row of C1.1–C1.6: nothing is checked here (list only).</summary>
        internal static IEnumerable<SpecResult> C1_0_1(SpecModel m)
        {
            yield return SpecChecks.Listing("C1.0.1", "Tiada semakan — baris induk; lihat C1.1–C1.6")
                .En("Not checked — parent row; see C1.1–C1.6");
        }

        /// <summary>The view kinds in the order a person reads them; other kinds follow by name.</summary>
        private static readonly string[] KindOrder =
            { "FloorPlan", "CeilingPlan", "Elevation", "Section", "ThreeD", "AreaPlan", "DraftingView", "Detail", "Legend" };

        private static int KindRank(string kind)
        {
            var i = Array.FindIndex(KindOrder, k => Same(k, kind));
            return i < 0 ? KindOrder.Length : i;
        }

        /// <summary>"floor plan (2): A, B; 3D (1): C" — the views grouped by kind, at most
        /// <paramref name="perKind"/> names per kind.</summary>
        private static string ViewsByKind(IEnumerable<SpecView> views, bool en, int perKind = 8)
        {
            var groups = views.GroupBy(v => (v.Kind ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => KindRank(g.Key)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (groups.Count == 0) return en ? "none" : "tiada";
            return string.Join("; ", groups.Select(g =>
            {
                var names = g.Select(v => Show(v.Name)).ToList();
                return (en ? KindEn(g.Key) : KindBm(g.Key)) + " (" + names.Count + "): "
                       + string.Join(", ", names.Take(perKind)) + (names.Count > perKind ? " …" : "");
            }));
        }

        /// <summary>The project's view templates set against the JKR AR template's judged ones (SpecTemplateViews.Judged).</summary>
        internal sealed class TemplateMatch
        {
            public List<string> Intact = new List<string>();
            /// <summary>Expected JKR name → the project name that equals it only without case / spaces / punctuation.</summary>
            public List<KeyValuePair<string, string>> Renamed = new List<KeyValuePair<string, string>>();
            public List<string> Missing = new List<string>();
            /// <summary>Project templates that match no name of the JKR file (any of its 80): a person may know one is a rename.</summary>
            public List<string> Extra = new List<string>();
            public int Expected;
        }

        internal static TemplateMatch MatchTemplates(SpecModel m, IReadOnlyCollection<string> judged = null, IReadOnlyCollection<string> allJkrFileTemplates = null)
        {
            judged = judged ?? SpecTemplateViews.Judged;
            allJkrFileTemplates = allJkrFileTemplates ?? SpecTemplateViews.ViewTemplates.Select(r => r.Name).ToList();
            var project = m.ViewTemplates.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var used = new HashSet<string>(StringComparer.Ordinal);
            var r = new TemplateMatch { Expected = judged.Count };
            foreach (var e in judged)
                if (project.Contains(e, StringComparer.Ordinal)) { r.Intact.Add(e); used.Add(e); }
            foreach (var e in judged.Where(e => !r.Intact.Contains(e)))
            {
                var key = SpecTemplateViews.LooseKey(e);
                var hit = project.FirstOrDefault(p => !used.Contains(p) && SpecTemplateViews.LooseKey(p) == key
                                                      && !allJkrFileTemplates.Contains(p, StringComparer.Ordinal));
                if (hit != null) { used.Add(hit); r.Renamed.Add(new KeyValuePair<string, string>(e, hit)); }
                else r.Missing.Add(e);
            }
            var known = new HashSet<string>(allJkrFileTemplates, StringComparer.Ordinal);
            r.Extra = project.Where(p => !used.Contains(p) && !known.Contains(p)).ToList();
            return r;
        }

        /// <summary>Unjudged when the view templates were not read (a read error, or none came back), else null.</summary>
        private static SpecResult TemplatesUnread(SpecModel m, string id)
        {
            var err = ReadError(m, "Views", "View");
            if (err != null) return SpecChecks.Unjudged(id, "View tidak dapat dibaca: " + err).En("Views could not be read: " + err);
            if (!m.ViewTemplates.Any(t => !string.IsNullOrWhiteSpace(t)))
                return SpecChecks.Unjudged(id, "Tiada view template dibaca daripada model — tidak dapat dibandingkan dengan template JKR AR")
                    .En("No view templates were read from the model — cannot compare with the JKR AR template");
            return null;
        }

        private static string Names(IEnumerable<string> l, int n = 6)
        {
            var x = l.ToList();
            return string.Join(", ", x.Take(n)) + (x.Count > n ? ", … (+" + (x.Count - n) + ")" : "");
        }

        /// <summary>
        /// C1.1.1 — every JKR-made view template of the official JKR AR template (jkrAR27 TEMFLEX, 67 "jkrAR_…" of its
        /// 80 view templates; SpecTemplateViews) exists in the project. A name that differs only in case, spaces or
        /// punctuation counts as present (that rename is C1.1.2's warning). Anything else is missing: one fail each, with
        /// the project's unmatched view templates listed as possible renames for a person. View templates not read: unjudged.
        /// </summary>
        internal static IEnumerable<SpecResult> C1_1_1(SpecModel m)
        {
            const string id = "C1.1.1";
            var unread = TemplatesUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var t = MatchTemplates(m);
            if (t.Missing.Count == 0)
            {
                var skipped = SpecTemplateViews.NotJudged.Count;
                yield return SpecChecks.Pass(id, "Semua " + t.Expected + " view template JKR AR wujud dalam projek"
                        + (t.Renamed.Count > 0 ? " (" + t.Renamed.Count + " ditukar nama sedikit — lihat C1.1.2)" : "")
                        + "; " + skipped + " nama default Revit dalam fail template JKR tidak disemak", t.Expected)
                    .En("All " + t.Expected + " JKR AR view templates exist in the project"
                        + (t.Renamed.Count > 0 ? " (" + t.Renamed.Count + " renamed slightly — see C1.1.2)" : "")
                        + "; " + skipped + " Revit default names in the JKR template file are not checked");
                yield break;
            }
            var extraBm = t.Extra.Count > 0 ? "; view template lain dalam projek (mungkin ditukar nama, orang semak): " + Names(t.Extra) : "";
            var extraEn = t.Extra.Count > 0 ? "; other view templates in the project (possibly renamed, a person checks): " + Names(t.Extra) : "";
            foreach (var name in t.Missing)
                yield return SpecChecks.Fail(id,
                        "View template JKR AR \"" + name + "\" tiada dalam projek (" + t.Missing.Count + " daripada " + t.Expected + " tiada)" + extraBm,
                        elementName: name, actual: "tiada", required: name, key: "tv:" + name)
                    .EnAct("missing")
                    .En("JKR AR view template \"" + name + "\" is not in the project (" + t.Missing.Count + " of " + t.Expected + " missing)" + extraEn);
        }

        /// <summary>
        /// C1.1.2 — a JKR AR view template that exists under a changed name. Researcher D3 (21 Nov): no JKR document says a
        /// rename is acceptable or wrong, so it is a WARNING (status "warning": scored as a pass, still an issue), one per
        /// template, "tidak disahkan oleh dokumen JKR". Only a rename that differs in case, spaces or punctuation is seen;
        /// any other new name looks like a missing template (C1.1.1 lists it, with the unmatched project templates).
        /// </summary>
        internal static IEnumerable<SpecResult> C1_1_2(SpecModel m)
        {
            const string id = "C1.1.2";
            var unread = TemplatesUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var t = MatchTemplates(m);
            if (t.Renamed.Count == 0)
            {
                yield return SpecChecks.Pass(id, t.Intact.Count + " view template JKR AR kekal nama asal"
                        + (t.Missing.Count > 0 ? "; " + t.Missing.Count + " tiada (lihat C1.1.1)" : ""), Math.Max(1, t.Intact.Count))
                    .En(t.Intact.Count + " JKR AR view templates keep their original name"
                        + (t.Missing.Count > 0 ? "; " + t.Missing.Count + " missing (see C1.1.1)" : ""));
                yield break;
            }
            foreach (var kv in t.Renamed)
                yield return new SpecResult
                {
                    CheckId = id, Status = "warning", Coverage = SpecCoverage.Full, Rule = id, ElementCount = 1,
                    ElementName = kv.Value, Actual = kv.Value, Required = kv.Key,
                    Note = "AMARAN: view template \"" + kv.Key + "\" ditukar nama kepada \"" + kv.Value + "\" — tidak disahkan oleh dokumen JKR",
                    NoteEn = "WARNING: view template \"" + kv.Key + "\" was renamed to \"" + kv.Value + "\" — not confirmed by a JKR document",
                    IssueId = SpecChecks.StableId(id, "rn:" + kv.Key),
                };
        }

        /// <summary>
        /// The first 3D view of <paramref name="views"/> that belongs to the level: its name holds
        /// the level name, the level name without its project prefix ("PTBR_Tangki Air" →
        /// "Tangki Air"), or the same level key ("PTBR_Aras 01" and "PTBR_ISOMETRIK ARAS 1" are
        /// both "aras 1"). Null when none does.
        /// Real model, 16 Nov 2026: 3D views per level were reported missing because only
        /// the full level name was looked for.
        /// </summary>
        private static SpecView Find3DFor(IEnumerable<SpecView> views, SpecLevel lv)
        {
            var name = lv.Name ?? "";
            var bare = name.Contains("_") ? name.Substring(name.LastIndexOf('_') + 1).Trim() : "";
            var key = LevelKey(name);
            return views.FirstOrDefault(v => IsKind(v, "ThreeD")
                                             && (ContainsWord(v.Name, name)
                                                 || (bare.Length >= 3 && ContainsWord(v.Name, bare))
                                                 || (key != null && key == LevelKey(v.Name))));
        }

        private static bool Has3DFor(IEnumerable<SpecView> views, SpecLevel lv) => Find3DFor(views, lv) != null;

        private static readonly string[] LevelWords = { "aras", "level", "lvl", "tingkat", "lantai" };

        /// <summary>"PTBR_Aras 01" → "aras 1"; "ISOMETRIK ARAS 1" → "aras 1"; "Level 02 +4.5" → "aras 2";
        /// "L03" → "aras 3"; "Aras Tanah" → "aras tanah". Null when the name has no level word.</summary>
        internal static string LevelKey(string name)
        {
            var t = System.Text.RegularExpressions.Regex.Split((name ?? "").ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(x => x.Length > 0).ToList();
            for (var i = 0; i < t.Count; i++)
            {
                var m = System.Text.RegularExpressions.Regex.Match(t[i], @"^l(\d+)$");
                if (m.Success) return "aras " + int.Parse(m.Groups[1].Value);
                if (Array.IndexOf(LevelWords, t[i]) < 0 || i + 1 >= t.Count) continue;
                var next = t[i + 1];
                if (System.Text.RegularExpressions.Regex.IsMatch(next, @"^\d+$")) return "aras " + int.Parse(next);
                return "aras " + next + (i + 2 < t.Count && !System.Text.RegularExpressions.Regex.IsMatch(t[i + 2], @"^\d") ? " " + t[i + 2] : "");
            }
            return null;
        }

        /// <summary>C1.1.3 — human decides: the add-in lists the levels it found; a person says whether that is enough.</summary>
        internal static IEnumerable<SpecResult> C1_1_3(SpecModel m)
        {
            const string id = "C1.1.3";
            if (ReadError(m, "Levels", "Level") != null) yield break;
            var story = StoryLevels(m);
            var other = m.Levels.Count - story.Count;
            var names = story.Count == 0 ? "—" : string.Join(", ", story.Select(l => l.Name));
            yield return SpecChecks.Context(id,
                    story.Count + " aras dikesan (" + names + ")" + (other > 0 ? " + " + other + " aras bukan building story" : "") + " — manusia sahkan cukup atau tidak")
                .En(story.Count + " levels found (" + names + ")" + (other > 0 ? " + " + other + " non-story levels" : "") + " — a person confirms whether that is enough");
        }

        /// <summary>The views of a set, decided by <see cref="SpecViewSets.Of"/> (browser folder /
        /// View_jkr_pit, then template name, then view name).</summary>
        private static List<SpecView> SetViews(SpecModel m, string set) => SpecViewSets.Views(m, set);

        /// <summary>"; set dikenal pasti ikut folder Project Browser (View_jkr_pit) 15" — how the
        /// views of a set were found, for the note a person reads.</summary>
        private static string FoundBm(List<SpecView> views) =>
            views.Count == 0
                ? "; tiada view dijumpai ikut folder Project Browser, " + SpecViewSets.SetParam + ", nama template atau nama view"
                : "; set dikenal pasti " + SpecViewSets.HowFound(views, false);

        private static string FoundEn(List<SpecView> views) =>
            views.Count == 0
                ? "; no view found by browser folder, " + SpecViewSets.SetParam + ", template name or view name"
                : "; set found " + SpecViewSets.HowFound(views, true);

        private static string TickOrDash(bool ok) => ok ? "✓" : "–";

        /// <summary>
        /// C1.2.1 — list only, never a verdict: the WIP views (SpecViewSets) ordered by level
        /// so a level without WIP views shows. One row per story level
        /// `Aras | floor plan ✓/– | ceiling plan ✓/– | section ✓/– (set)`; sections and
        /// elevations have no level, so they are counted for the set. Then every WIP view as
        /// `Jenis view | Aras | Nama view`.
        /// </summary>
        internal static IEnumerable<SpecResult> C1_2_1(SpecModel m)
        {
            const string id = "C1.2.1";
            if (ViewsUnread(m, id) != null) yield break;
            var views = SetViews(m, "WIP");
            var levels = ReadError(m, "Levels", "Level") != null ? new List<SpecLevel>() : StoryLevels(m);
            var sections = views.Count(v => IsKind(v, "Section"));
            var elevations = views.Count(v => IsKind(v, "Elevation"));
            yield return SpecChecks.Listing(id, views.Count + " view WIP — untuk semakan manusia" + FoundBm(views), views.Count)
                .En(views.Count + " WIP views — for a person to review" + FoundEn(views));
            foreach (var lv in levels)
            {
                var fp = views.Any(v => IsKind(v, "FloorPlan") && Same(v.LevelName, lv.Name));
                var cp = views.Any(v => IsKind(v, "CeilingPlan") && Same(v.LevelName, lv.Name));
                yield return SpecChecks.Listing(id, lv.Name + " | floor plan " + TickOrDash(fp) + " | ceiling plan " + TickOrDash(cp) + " | section " + TickOrDash(sections > 0) + " (set)", 1)
                    .En(lv.Name + " | floor plan " + TickOrDash(fp) + " | ceiling plan " + TickOrDash(cp) + " | section " + TickOrDash(sections > 0) + " (set)");
            }
            yield return SpecChecks.Listing(id,
                    "Set WIP: " + sections + " section, " + elevations + " elevation (tiada aras — dikira bagi set, bukan bagi aras)", sections + elevations)
                .En("WIP set: " + sections + " sections, " + elevations + " elevations (no level — counted for the set, not per level)");

            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < levels.Count; i++) rank[levels[i].Name.Trim()] = i;
            Func<SpecView, int> levelRank = v =>
            {
                int r;
                return rank.TryGetValue((v.LevelName ?? "").Trim(), out r) ? r : levels.Count;
            };
            foreach (var v in views.OrderBy(levelRank).ThenBy(v => (v.LevelName ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(v => KindRank(v.Kind)).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                yield return SpecChecks.Listing(id, KindBm(v.Kind) + " | " + Show(v.LevelName) + " | " + v.Name, 1)
                    .En(KindEn(v.Kind) + " | " + Show(v.LevelName) + " | " + v.Name);
        }

        /// <summary>Checklist of required view kinds in a named set; one fail per kind missing.</summary>
        private static IEnumerable<SpecResult> SetChecklist(SpecModel m, string id, string set, string[] kinds)
        {
            var unread = ViewsUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var views = SetViews(m, set);
            var have = kinds.ToDictionary(k => k, k => views.Any(v => IsKind(v, k)));
            var list = string.Join(" | ", kinds.Select(k => KindBm(k) + " " + Tick(have[k])));
            var listEn = string.Join(" | ", kinds.Select(k => KindEn(k) + " " + Tick(have[k])));
            foreach (var k in kinds.Where(k => !have[k]))
            {
                yield return SpecChecks.Fail(id, "Set " + set + ": tiada " + KindBm(k) + " (" + list + ")" + FoundBm(views),
                        elementName: "Set " + set, actual: views.Count + " view " + set, required: string.Join(", ", kinds.Select(KindBm)),
                        key: set + "|" + k)
                    .En(set + " set: no " + KindEn(k) + " (" + listEn + ")" + FoundEn(views));
            }
            if (have.Values.All(x => x))
                yield return SpecChecks.Pass(id, "Set " + set + ": " + list + " (" + views.Count + " view" + FoundBm(views) + ")", views.Count)
                    .En(set + " set: " + listEn + " (" + views.Count + " views" + FoundEn(views) + ")");
        }

        /// <summary>C1.3.1 — the PBT set (SpecViewSets: folder 06p_PBT, template, else name) has a floor plan, an elevation and a section.</summary>
        internal static IEnumerable<SpecResult> C1_3_1(SpecModel m) =>
            SetChecklist(m, "C1.3.1", "PBT", new[] { "FloorPlan", "Elevation", "Section" });

        /// <summary>C1.4.1 — the BOMBA set (SpecViewSets: folder 05b_Bomba, template, else name) has a floor plan, a section and a drafting view.</summary>
        internal static IEnumerable<SpecResult> C1_4_1(SpecModel m) =>
            SetChecklist(m, "C1.4.1", "BOMBA", new[] { "FloorPlan", "Section", "DraftingView" });

        /// <summary>
        /// C1.5.1 — list only: the Dokumen views (SpecViewSets: folder 07d_Dokumen, template, else name) as
        /// `Nama view | Jenis | Aras | Dalam sheet?`. The 3D view per level is C1.5.2.
        /// </summary>
        internal static IEnumerable<SpecResult> C1_5_1(SpecModel m)
        {
            const string id = "C1.5.1";
            if (ViewsUnread(m, id) != null) yield break;
            var views = SetViews(m, "Dokumen");
            var onSheet = views.Count(v => v.OnSheet);
            yield return SpecChecks.Listing(id, views.Count + " view Dokumen, " + onSheet + " dalam sheet (biru)" + FoundBm(views), views.Count)
                .En(views.Count + " Dokumen views, " + onSheet + " on a sheet (blue)" + FoundEn(views));
            foreach (var v in views)
                yield return SpecChecks.Listing(id, v.Name + " | " + KindBm(v.Kind) + " | " + Show(v.LevelName) + " | dalam sheet " + Tick(v.OnSheet) + (v.OnSheet && v.SheetNumber.Length > 0 ? " (" + v.SheetNumber + ")" : ""), 1)
                    .En(v.Name + " | " + KindEn(v.Kind) + " | " + Show(v.LevelName) + " | on sheet " + Tick(v.OnSheet) + (v.OnSheet && v.SheetNumber.Length > 0 ? " (" + v.SheetNumber + ")" : ""));
        }

        /// <summary>
        /// C1.5.2 — the Dokumen view set (SpecViewSets) has a 3D view for every story
        /// level (every level ticked Building Story, beam / roof levels included). One fail per level without one: "3D view setiap aras: 3/4 ✗ (Aras 03 tiada)".
        /// A 3D view of that level outside the Dokumen set does not count (the rule is about the
        /// set), and the note names it. No story levels or no views read → unjudged.
        /// </summary>
        internal static IEnumerable<SpecResult> C1_5_2(SpecModel m)
        {
            const string id = "C1.5.2";
            var unread = ViewsUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var levelErr = ReadError(m, "Levels", "Level");
            var levels = levelErr != null ? new List<SpecLevel>() : StoryLevels(m);
            if (levels.Count == 0)
            {
                var why = levelErr != null ? ": " + levelErr : "";
                yield return SpecChecks.Unjudged(id, "Tiada aras (building story) dibaca" + why + " — 3D view setiap aras tidak dapat disemak")
                    .En("No story levels were read" + why + " — 3D view for every level cannot be checked");
                yield break;
            }
            var dok = SetViews(m, "Dokumen");
            var others = m.Views.Where(v => !SpecViewSets.Of(v).In("Dokumen")).ToList();
            var noDok3D = !dok.Any(v => IsKind(v, "ThreeD"));
            var missing = levels.Where(l => !Has3DFor(dok, l)).ToList();
            var have = levels.Count - missing.Count;
            foreach (var lv in missing)
            {
                var outside = Find3DFor(others, lv);
                var bm = "3D view setiap aras: " + have + "/" + levels.Count + " ✗ (" + lv.Name + " tiada)";
                var en = "3D view for every level: " + have + "/" + levels.Count + " ✗ (" + lv.Name + " missing)";
                if (outside != null)
                {
                    var oh = SpecViewSets.Of(outside);
                    var where = oh.Sets.Count > 0
                        ? "dalam set " + string.Join("/", oh.Sets) + ", " + SpecViewSets.SourceText(oh.Source, oh.By, false)
                        : oh.Source == ViewSetSource.None ? "folder, template dan nama tiada 'Dokumen'" : SpecViewSets.SourceText(oh.Source, oh.By, false) + ": bukan Dokumen";
                    var whereEn = oh.Sets.Count > 0
                        ? "in the " + string.Join("/", oh.Sets) + " set, " + SpecViewSets.SourceText(oh.Source, oh.By, true)
                        : oh.Source == ViewSetSource.None ? "no 'Dokumen' in its folder, template or name" : SpecViewSets.SourceText(oh.Source, oh.By, true) + ": not Dokumen";
                    bm += " — ada 3D view '" + outside.Name + "' di luar set Dokumen (" + where + "), tidak dikira";
                    en += " — 3D view '" + outside.Name + "' exists outside the Dokumen set (" + whereEn + "), not counted";
                }
                else if (noDok3D)
                {
                    bm += " — tiada 3D view dalam set Dokumen";
                    en += " — no 3D view in the Dokumen set";
                }
                yield return SpecChecks.Fail(id, bm, lv.Id, lv.Name,
                        actual: outside != null ? "3D view di luar set Dokumen: " + outside.Name : "tiada 3D view Dokumen",
                        required: "3D view Dokumen bagi setiap aras", key: lv.Name)
                    .En(en);
            }
            if (missing.Count == 0)
                yield return SpecChecks.Pass(id, "3D view setiap aras: " + have + "/" + levels.Count + " ✓ (set Dokumen)", have)
                    .En("3D view for every level: " + have + "/" + levels.Count + " ✓ (Dokumen set)");
        }

        // ─── C1.6 — room schedule ───

        private static List<SpecSchedule> RoomSchedules(SpecModel m) =>
            m.Schedules.Where(s => !s.IsTemplate && Same(s.Category, "Rooms")).ToList();

        private static SpecResult SchedulesUnread(SpecModel m, string id)
        {
            var err = ReadError(m, "Schedules", "Schedule");
            return err == null ? null : SpecChecks.Unjudged(id, "Schedule tidak dapat dibaca: " + err).En("Schedules could not be read: " + err);
        }

        private static SpecResult RoomsUnread(SpecModel m, string id)
        {
            var err = ReadError(m, "Rooms", "Room");
            if (err != null) return SpecChecks.Unjudged(id, "Room tidak dapat dibaca: " + err).En("Rooms could not be read: " + err);
            if (!m.Rooms.Any(r => r.Area > 0))
                return SpecChecks.Unjudged(id, "Tiada room diletakkan dalam model — tidak dapat disemak").En("No placed rooms in the model — cannot be checked");
            return null;
        }

        private static string RoomLabel(SpecRoom r) =>
            (r.Number.Length > 0 ? r.Number + " " : "") + (r.Name.Length > 0 ? r.Name : "(tiada nama)");

        /// <summary>
        /// C1.6.1 — a room schedule / schedule of accommodation exists (a schedule of category
        /// Rooms). None → fail.
        /// </summary>
        internal static IEnumerable<SpecResult> C1_6_1(SpecModel m)
        {
            const string id = "C1.6.1";
            var unread = SchedulesUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var rs = RoomSchedules(m);
            if (rs.Count == 0)
            {
                yield return SpecChecks.Fail(id, "Tiada jadual ruang (room schedule)",
                        actual: "tiada schedule kategori Rooms", required: "room schedule / schedule of accommodation", key: "none")
                    .En("No room schedule");
                yield break;
            }
            yield return SpecChecks.Pass(id, "Jadual ruang wujud: " + string.Join(", ", rs.Select(s => s.Name)), rs.Count)
                .En("Room schedule exists: " + string.Join(", ", rs.Select(s => s.Name)));
        }

        /// <summary>C1.6.2 — every placed room is a row of a room schedule; one fail per room missing.</summary>
        internal static IEnumerable<SpecResult> C1_6_2(SpecModel m)
        {
            const string id = "C1.6.2";
            var unread = SchedulesUnread(m, id) ?? RoomsUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var rs = RoomSchedules(m);
            var rows = new HashSet<long>(rs.SelectMany(s => s.RowElementIds));
            var placed = m.Rooms.Where(r => r.Area > 0).ToList();
            var missing = placed.Where(r => !rows.Contains(r.Id)).ToList();
            foreach (var r in missing)
            {
                yield return SpecChecks.Fail(id, "Room " + RoomLabel(r) + (r.LevelName.Length > 0 ? " (" + r.LevelName + ")" : "") + " tiada dalam jadual ruang",
                        r.Id, "Rooms · " + RoomLabel(r), actual: rs.Count == 0 ? "tiada jadual ruang" : "tiada dalam jadual",
                        required: "hadir dalam jadual ruang")
                    .En("Room " + RoomLabel(r) + (r.LevelName.Length > 0 ? " (" + r.LevelName + ")" : "") + " is not in the room schedule");
            }
            if (missing.Count == 0)
                yield return SpecChecks.Pass(id, placed.Count + "/" + placed.Count + " room hadir dalam jadual ruang", placed.Count)
                    .En(placed.Count + "/" + placed.Count + " rooms are in the room schedule");
        }

        /// <summary>Schedule fields that are not room parameters and are never "empty".</summary>
        private static readonly HashSet<string> ComputedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Count", "Area", "Perimeter", "Volume", "Level", "Unbounded Height", "Phase", "Family and Type",
        };

        private static bool RoomValue(SpecRoom r, string field, out string value)
        {
            if (SpecPbData.TryFind(r.Params, field, out _, out value)) return true;
            if (Same(field, "Name")) { value = r.Name; return true; }
            if (Same(field, "Number")) { value = r.Number; return true; }
            if (Same(field, "Department")) { value = r.Department; return true; }
            value = "";
            return false;
        }

        /// <summary>
        /// C1.6.3 — every room schedule column has a value for every placed room: the schedules'
        /// visible fields, plus the Room columns the Buku Parameter marks at the project LOi
        /// (PB p.37). One fail per room (`Room | Kolum kosong`), and one per schedule missing a
        /// PB column. Placed areas are judged too: each needs the PB p.36 Area columns marked at the LOi
        /// (AI-475). Fail-only when the LOi is unknown (PB columns not known) or when the model has
        /// areas the snapshot did not read.
        /// </summary>
        internal static IEnumerable<SpecResult> C1_6_3(SpecModel m)
        {
            const string id = "C1.6.3";
            var unread = SchedulesUnread(m, id) ?? RoomsUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var rs = RoomSchedules(m);
            if (rs.Count == 0)
            {
                yield return SpecChecks.Fail(id, "Tiada jadual ruang — kolum tidak dapat diisi", actual: "tiada jadual ruang",
                        required: "jadual ruang lengkap", key: "none")
                    .En("No room schedule — its columns cannot be filled");
                yield break;
            }
            var loi = SpecPbData.LoiOf(m.Lod);
            var pb = loi > 0 ? SpecPbData.RequiredAt(SpecPbData.RoomFields, loi).Select(f => f.Name).ToList() : new List<string>();
            int areas;
            var partial = loi == 0 || (!m.AreasRead && m.CategoryCounts.TryGetValue("Areas", out areas) && areas > 0);
            var cov = partial ? SpecCoverage.FailOnly : SpecCoverage.Full;

            var fails = 0;
            // PB columns the schedules do not show at all.
            foreach (var s in rs)
            {
                var absent = pb.Where(f => !s.Fields.Any(x => SpecPbData.Key(x) == SpecPbData.Key(f))).ToList();
                if (absent.Count == 0) continue;
                fails++;
                yield return SpecChecks.Fail(id, "Jadual '" + s.Name + "' tiada kolum wajib LOi " + loi + " (PB p.37): " + string.Join(", ", absent),
                        s.Id, "Schedule · " + s.Name, actual: absent.Count + " kolum tiada", required: "kolum PB p.37", key: "schedule|" + s.Id + "|" + s.Name,
                        coverage: cov)
                    .En("Schedule '" + s.Name + "' lacks the columns mandatory at LOi " + loi + " (PB p.37): " + string.Join(", ", absent));
            }

            var columns = rs.SelectMany(s => s.Fields).Concat(pb)
                .Where(f => !string.IsNullOrWhiteSpace(f) && !ComputedFields.Contains(f.Trim()))
                .GroupBy(f => SpecPbData.Key(f)).Where(g => g.Key.Length > 0).Select(g => g.First().Trim()).ToList();
            var pbKeys = new HashSet<string>(pb.Select(SpecPbData.Key));
            var placed = m.Rooms.Where(r => r.Area > 0).ToList();
            foreach (var r in placed)
            {
                var empty = new List<string>();
                foreach (var col in columns)
                {
                    var found = RoomValue(r, col, out var v);
                    // A schedule field that is not a room parameter is computed by the schedule;
                    // a PB column the room lacks is empty.
                    if (!found && !pbKeys.Contains(SpecPbData.Key(col))) continue;
                    if (string.IsNullOrWhiteSpace(v)) empty.Add(col);
                }
                if (empty.Count == 0) continue;
                fails++;
                yield return SpecChecks.Fail(id, RoomLabel(r) + " | " + string.Join(", ", empty), r.Id, "Rooms · " + RoomLabel(r),
                        actual: empty.Count + " kolum kosong", required: "setiap kolum berisi", coverage: cov)
                    .En(RoomLabel(r) + " | " + string.Join(", ", empty));
            }
            // PB p.36: each placed area carries the Area columns marked at the LOi.
            var placedAreas = m.AreasRead ? m.Areas.Where(x => x.Area > 0).ToList() : new List<SpecArea>();
            var areaCols = loi > 0 ? SpecPbData.RequiredAt(SpecPbData.AreaFields, loi).Select(f => f.Name).ToList() : new List<string>();
            foreach (var ar in placedAreas)
            {
                var empty = new List<string>();
                foreach (var col in areaCols)
                {
                    string v;
                    if (!SpecPbData.TryFind(ar.Params, col, out _, out v) && Same(col, "Name")) v = ar.Name;
                    if (string.IsNullOrWhiteSpace(v)) empty.Add(col);
                }
                if (empty.Count == 0) continue;
                var label = (ar.Name.Length > 0 ? ar.Name : "Area " + ar.Id) + (ar.LevelName.Length > 0 ? " (" + ar.LevelName + ")" : "");
                fails++;
                yield return SpecChecks.Fail(id, "Area " + label + " | " + string.Join(", ", empty), ar.Id, "Areas · " + label,
                        actual: empty.Count + " kolum kosong", required: "setiap kolum Area PB p.36 berisi", coverage: cov, key: "area|" + ar.Id)
                    .En("Area " + label + " | " + string.Join(", ", empty));
            }
            if (fails == 0)
                yield return SpecChecks.Pass(id, placed.Count + " room × " + columns.Count + " kolum berisi" + (pb.Count > 0 ? " (termasuk kolum wajib LOi " + loi + ", PB p.37)" : ""), placed.Count, cov)
                    .En(placed.Count + " rooms × " + columns.Count + " columns filled" + (pb.Count > 0 ? " (including the columns mandatory at LOi " + loi + ", PB p.37)" : ""));
        }

        // ═══════════════════════ C2 — Legends ═══════════════════════

        /// <summary>
        /// C2.1.1 — at least one legend exists (pass), none (fail). The note lists the legends as
        /// `Nama legend | Jenis | Dalam lukisan?` for a person to review.
        /// </summary>
        internal static IEnumerable<SpecResult> C2_1_1(SpecModel m)
        {
            const string id = "C2.1.1";
            var unread = ViewsUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var legends = m.Views.Where(v => IsKind(v, "Legend")).ToList();
            if (legends.Count == 0)
            {
                yield return SpecChecks.Fail(id, "Tiada legend dalam model", actual: "0 legend", required: "sekurang-kurangnya satu legend", key: "none")
                    .En("No legend in the model");
                yield break;
            }
            var bm = string.Join("; ", legends.Select(v => v.Name + " | legend | dalam lukisan " + Tick(v.OnSheet)));
            var en = string.Join("; ", legends.Select(v => v.Name + " | legend | on a drawing " + Tick(v.OnSheet)));
            yield return SpecChecks.Pass(id, legends.Count + " legend (" + legends.Count(v => v.OnSheet) + " dalam lukisan): " + bm, legends.Count)
                .En(legends.Count + " legends (" + legends.Count(v => v.OnSheet) + " on a drawing): " + en);
        }

        // ═══════════════════════ C3 — Schedules ═══════════════════════

        private static int Modelled(SpecModel m, PbCategory c)
        {
            var n = 0;
            foreach (var kv in m.CategoryCounts)
                if (c.Matches(kv.Key) && kv.Value > 0) n += kv.Value;
            return n;
        }

        private static SpecResult CountsUnread(SpecModel m, string id)
        {
            var err = ReadError(m, "CategoryCounts", "Category");
            if (err != null) return SpecChecks.Unjudged(id, "Bilangan elemen per kategori tidak dapat dibaca: " + err).En("Element counts per category could not be read: " + err);
            if (m.CategoryCounts.Count == 0) return SpecChecks.Unjudged(id, "Bilangan elemen per kategori tidak dibaca").En("Element counts per category were not read");
            return null;
        }

        /// <summary>The schedule lists elements of category <paramref name="c"/>: its row
        /// categories say so, or (older snapshots) a row id is a placed instance of that category.</summary>
        private static bool ListsCategory(SpecModel m, SpecSchedule s, PbCategory c)
        {
            if ((s.RowCategories ?? new List<string>()).Any(c.Matches)) return true;
            var rows = s.RowElementIds ?? new List<long>();
            if (rows.Count == 0) return false;
            var set = new HashSet<long>(rows);
            return m.Types.Where(t => c.Matches(t.Category)).Any(t => (t.InstanceIds ?? new List<long>()).Any(set.Contains));
        }

        private static string ScheduleKindBm(SpecSchedule s) =>
            s.IsMaterialTakeoff ? "material takeoff" : string.IsNullOrWhiteSpace(s.Category) ? "schedule pelbagai kategori" : "schedule " + s.Category;

        private static string ScheduleKindEn(SpecSchedule s) =>
            s.IsMaterialTakeoff ? "material takeoff" : string.IsNullOrWhiteSpace(s.Category) ? "multi-category schedule" : s.Category + " schedule";

        /// <summary>
        /// C3.1.1 — every AR category of the Buku Parameter list (PB p.36–99) that is modelled
        /// (placed elements &gt; 0) has a schedule of that category, or is listed by a
        /// multi-category schedule or a material takeoff (its rows hold elements of that
        /// category). One fail per category without: "Railing: 5 elemen, tiada schedule".
        /// Real model, 16 Nov 2026: 3 ramps scheduled only in multi-category schedules /
        /// material takeoffs were reported "tiada schedule".
        /// </summary>
        internal static IEnumerable<SpecResult> C3_1_1(SpecModel m)
        {
            const string id = "C3.1.1";
            var unread = CountsUnread(m, id) ?? SchedulesUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var schedules = m.Schedules.Where(s => !s.IsTemplate).ToList();
            var modelled = 0;
            var fails = 0;
            var viaBm = new List<string>();
            var viaEn = new List<string>();
            foreach (var c in SpecPbData.ArCategories)
            {
                var n = Modelled(m, c);
                if (n <= 0) continue;
                modelled++;
                if (schedules.Any(s => c.Matches(s.Category))) continue;
                var listing = schedules.Where(s => ListsCategory(m, s, c)).ToList();
                if (listing.Count > 0)
                {
                    viaBm.Add(c.Name + " dalam " + string.Join(", ", listing.Take(4).Select(s => ScheduleKindBm(s) + " '" + s.Name + "'")) + (listing.Count > 4 ? " …" : ""));
                    viaEn.Add(c.Name + " in " + string.Join(", ", listing.Take(4).Select(s => ScheduleKindEn(s) + " '" + s.Name + "'")) + (listing.Count > 4 ? " …" : ""));
                    continue;
                }
                fails++;
                yield return SpecChecks.Fail(id, c.Name + ": " + n + " elemen, tiada schedule", elementName: c.Name + " (PB p." + c.Page + ")",
                        actual: n + " elemen, tiada schedule", required: "schedule kategori " + c.RevitCategories[0], key: c.Name)
                    .En(c.Name + ": " + n + " elements, no schedule");
            }
            var via = viaBm.Count == 0 ? "" : " (" + string.Join("; ", viaBm) + ")";
            var viaE = viaEn.Count == 0 ? "" : " (" + string.Join("; ", viaEn) + ")";
            if (fails == 0)
                yield return SpecChecks.Pass(id, modelled + " kategori PB dimodelkan, semua ada schedule" + via, modelled)
                    .En(modelled + " Buku Parameter categories modelled, all have a schedule" + viaE);
            else if (via.Length > 0)
                yield return SpecChecks.Listing(id, "Ada schedule melalui schedule pelbagai kategori / material takeoff" + via, viaBm.Count)
                    .En("Scheduled through a multi-category schedule / material takeoff" + viaE);
        }

        /// <summary>C3.1.2 — list only: the Buku Parameter AR categories not modelled, marked "n/a" (not a shortfall).</summary>
        internal static IEnumerable<SpecResult> C3_1_2(SpecModel m)
        {
            const string id = "C3.1.2";
            if (CountsUnread(m, id) != null) yield break;
            var none = SpecPbData.ArCategories.Where(c => Modelled(m, c) <= 0).Select(c => c.Name).ToList();
            yield return SpecChecks.Listing(id,
                    none.Count == 0 ? "Semua kategori PB dimodelkan — tiada n/a" : "n/a (tidak dimodelkan, bukan kekurangan): " + string.Join(", ", none), none.Count)
                .En(none.Count == 0 ? "Every Buku Parameter category is modelled — none n/a" : "n/a (not modelled, not a shortfall): " + string.Join(", ", none));
        }

        // ═══════════════════════ C4 — Sheets ═══════════════════════

        private static SpecResult SheetsUnread(SpecModel m, string id, bool emptyIsUnjudged)
        {
            var err = ReadError(m, "Sheets", "Sheet");
            if (err != null) return SpecChecks.Unjudged(id, "Sheet tidak dapat dibaca: " + err).En("Sheets could not be read: " + err);
            if (emptyIsUnjudged && m.Sheets.Count == 0)
                return SpecChecks.Unjudged(id, "Tiada sheet dalam model — tidak dapat disemak").En("No sheets in the model — cannot be checked");
            return null;
        }

        private static string SheetLabel(SpecSheet s) =>
            (s.Number.Length > 0 ? s.Number : "(tiada no.)") + (s.Name.Length > 0 ? " " + s.Name : "");

        private static bool IsEmptySheet(SpecSheet s) =>
            !s.ViewNames.Any(n => !string.IsNullOrWhiteSpace(n)) && !s.ViewKinds.Any(k => !string.IsNullOrWhiteSpace(k));

        /// <summary>C4.1.1 — list only: every sheet as `No. sheet | Nama | Bil. view | Jenis kandungan`.</summary>
        internal static IEnumerable<SpecResult> C4_1_1(SpecModel m)
        {
            const string id = "C4.1.1";
            if (SheetsUnread(m, id, false) != null) yield break;
            yield return SpecChecks.Listing(id, m.Sheets.Count + " sheet — manusia semak (sheet kadang digabung)", m.Sheets.Count)
                .En(m.Sheets.Count + " sheets — for a person to review (sheets are sometimes combined)");
            foreach (var s in m.Sheets)
            {
                var n = Math.Max(s.ViewNames.Count(x => !string.IsNullOrWhiteSpace(x)), s.ViewKinds.Count(x => !string.IsNullOrWhiteSpace(x)));
                var kinds = s.ViewKinds.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                yield return SpecChecks.Listing(id, Show(s.Number) + " | " + Show(s.Name) + " | " + n + " view | " + (kinds.Count == 0 ? "—" : string.Join(", ", kinds.Select(KindBm))), 1)
                    .En(Show(s.Number) + " | " + Show(s.Name) + " | " + n + " views | " + (kinds.Count == 0 ? "—" : string.Join(", ", kinds.Select(KindEn))));
            }
        }

        /// <summary>(kind, name) of every view placed on a sheet: the sheets' own lists, and the
        /// views flagged on a sheet.</summary>
        private static List<KeyValuePair<string, string>> PlacedViews(SpecModel m)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var s in m.Sheets)
            {
                var aligned = s.ViewKinds.Count == s.ViewNames.Count;
                for (var i = 0; i < s.ViewKinds.Count; i++)
                    list.Add(new KeyValuePair<string, string>(s.ViewKinds[i] ?? "", aligned ? s.ViewNames[i] ?? "" : ""));
                if (!aligned)
                    foreach (var n in s.ViewNames)
                    {
                        var v = m.Views.FirstOrDefault(x => Same(x.Name, n));
                        list.Add(new KeyValuePair<string, string>(v != null ? v.Kind : "", n ?? ""));
                    }
            }
            foreach (var v in m.Views.Where(v => v.OnSheet))
                list.Add(new KeyValuePair<string, string>(v.Kind, v.Name));
            return list;
        }

        private static bool IsDetailOf(KeyValuePair<string, string> v, string bm, string en) =>
            (Same(v.Key, "Detail") || Same(v.Key, "DraftingView")) && (Has(v.Value, bm) || Has(v.Value, en));

        /// <summary>The sheets named as the door / window details ("bPT02 Butiran Pintu", "Door
        /// Details") that hold at least one view. JKR drawing sets put the door and window legends
        /// there rather than detail views (real Dewan model, 16 Nov). AI-504 (spec #10): whether such
        /// a sheet counts as the door / window detail is not in any JKR document, so it is only
        /// listed for a person, never counted by the check.</summary>
        private static List<string> DetailSheets(SpecModel m, string bm, string en) =>
            m.Sheets.Where(s => s.ViewKinds.Count > 0
                                && ((Has(s.Name, "butiran") && Has(s.Name, bm)) || (Has(s.Name, en) && Has(s.Name, "detail"))))
                    .Select(s => s.Name).ToList();

        /// <summary>
        /// C4.1.2 — across all sheets there is a floor plan, a ceiling plan, an elevation, a
        /// section, a door detail and a window detail (detail = a Detail or Drafting view whose
        /// name holds pintu/door or tingkap/window). One fail per kind missing from every sheet,
        /// except where the only candidate is a sheet named "Butiran Pintu" / "Door Detail" that
        /// holds a view: that sheet is listed for a person (spec #10), neither counted nor failed.
        /// </summary>
        internal static IEnumerable<SpecResult> C4_1_2(SpecModel m)
        {
            const string id = "C4.1.2";
            var unread = SheetsUnread(m, id, false);
            if (unread != null) { yield return unread; yield break; }
            var placed = PlacedViews(m);
            var items = new[]
            {
                new { Bm = "floor plan", En = "floor plan", Ok = placed.Any(v => Same(v.Key, "FloorPlan")), Sheets = new List<string>() },
                new { Bm = "ceiling plan", En = "ceiling plan", Ok = placed.Any(v => Same(v.Key, "CeilingPlan")), Sheets = new List<string>() },
                new { Bm = "elevation", En = "elevation", Ok = placed.Any(v => Same(v.Key, "Elevation")), Sheets = new List<string>() },
                new { Bm = "section", En = "section", Ok = placed.Any(v => Same(v.Key, "Section")), Sheets = new List<string>() },
                new { Bm = "detail pintu", En = "door detail", Ok = placed.Any(v => IsDetailOf(v, "pintu", "door")), Sheets = DetailSheets(m, "pintu", "door") },
                new { Bm = "detail tingkap", En = "window detail", Ok = placed.Any(v => IsDetailOf(v, "tingkap", "window")), Sheets = DetailSheets(m, "tingkap", "window") },
            };
            string Mark(bool ok, int sheets) => ok ? Tick(true) : sheets > 0 ? "(sheet: manusia)" : Tick(false);
            string MarkEn(bool ok, int sheets) => ok ? Tick(true) : sheets > 0 ? "(sheet: a person)" : Tick(false);
            var list = string.Join(" | ", items.Select(x => x.Bm + " " + Mark(x.Ok, x.Sheets.Count)));
            var listEn = string.Join(" | ", items.Select(x => x.En + " " + MarkEn(x.Ok, x.Sheets.Count)));
            foreach (var x in items.Where(x => !x.Ok && x.Sheets.Count > 0))
            {
                var shown = string.Join(", ", x.Sheets.Take(3)) + (x.Sheets.Count > 3 ? ", …" : "");
                yield return SpecChecks.Context(id, $"Tiada {x.Bm} (view Detail / Drafting) tetapi ada sheet: {shown} — JKR tidak menyatakan sama ada sheet sedemikian dikira {x.Bm}; manusia putuskan",
                        key: "sheet:" + x.En)
                    .En($"No {x.En} (Detail / Drafting view) but there is a sheet: {shown} — no JKR document says whether such a sheet counts as the {x.En}; a person decides");
            }
            foreach (var x in items.Where(x => !x.Ok && x.Sheets.Count == 0))
            {
                yield return SpecChecks.Fail(id, "Tiada " + x.Bm + " dalam mana-mana sheet (" + list + ")", actual: "tiada " + x.Bm,
                        required: "floor plan, ceiling plan, elevation, section, detail pintu, detail tingkap", key: x.En)
                    .En("No " + x.En + " on any sheet (" + listEn + ")");
            }
            if (items.All(x => x.Ok || x.Sheets.Count > 0))
                yield return SpecChecks.Pass(id, list + " (" + m.Sheets.Count + " sheet)", m.Sheets.Count).En(listEn + " (" + m.Sheets.Count + " sheets)");
        }

        /// <summary>C4.1.3 — every sheet holds at least one view; one fail per empty sheet.</summary>
        internal static IEnumerable<SpecResult> C4_1_3(SpecModel m)
        {
            const string id = "C4.1.3";
            var unread = SheetsUnread(m, id, true);
            if (unread != null) { yield return unread; yield break; }
            var empty = m.Sheets.Where(IsEmptySheet).ToList();
            foreach (var s in empty)
                yield return SpecChecks.Fail(id, "Sheet " + SheetLabel(s) + " tiada view", s.Id, "Sheet " + SheetLabel(s),
                        actual: "0 view", required: "sekurang-kurangnya satu view", key: s.Id > 0 ? null : s.Number + "|" + s.Name)
                    .En("Sheet " + SheetLabel(s) + " has no view");
            if (empty.Count == 0)
                yield return SpecChecks.Pass(id, m.Sheets.Count + "/" + m.Sheets.Count + " sheet ada view", m.Sheets.Count)
                    .En(m.Sheets.Count + "/" + m.Sheets.Count + " sheets have a view");
        }

        /// <summary>C4.2.1 — every sheet's title block is a JKR one (family / type holds "JKR"); one fail per sheet that is not or has none.</summary>
        internal static IEnumerable<SpecResult> C4_2_1(SpecModel m)
        {
            const string id = "C4.2.1";
            var unread = SheetsUnread(m, id, true);
            if (unread != null) { yield return unread; yield break; }
            var fails = 0;
            foreach (var s in m.Sheets)
            {
                if (Has(s.TitleBlock, "JKR")) continue;
                fails++;
                var none = string.IsNullOrWhiteSpace(s.TitleBlock);
                yield return SpecChecks.Fail(id,
                        "Sheet " + SheetLabel(s) + (none ? ": tiada title block" : ": title block '" + s.TitleBlock + "' bukan JKR"),
                        s.Id, "Sheet " + SheetLabel(s), actual: none ? "tiada title block" : s.TitleBlock, required: "title block JKR",
                        key: s.Id > 0 ? null : s.Number + "|" + s.Name)
                    .En("Sheet " + SheetLabel(s) + (none ? ": no title block" : ": title block '" + s.TitleBlock + "' is not a JKR one"));
            }
            if (fails == 0)
            {
                var tbs = m.Sheets.Select(s => s.TitleBlock.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                yield return SpecChecks.Pass(id, m.Sheets.Count + " sheet guna title block JKR (" + string.Join(", ", tbs) + ")", m.Sheets.Count)
                    .En(m.Sheets.Count + " sheets use a JKR title block (" + string.Join(", ", tbs) + ")");
            }
        }

        private sealed class TbField
        {
            public string Bm, En;
            public string[] Keys;
        }

        /// <summary>The four title-block fields of C4.2.2 and the parameter names that hold them
        /// (Revit's own and the JKR ones, PB p.103).</summary>
        private static readonly TbField[] TitleBlockFields =
        {
            new TbField { Bm = "tajuk projek", En = "project title", Keys = new[] { "Project Name", "Tajuk_Projek", "Tajuk Projek", "Project Title" } },
            new TbField { Bm = "tajuk lukisan", En = "drawing title", Keys = new[] { "Sheet Name", "Tajuk_Lukisan", "Tajuk Lukisan", "Drawing Title" } },
            new TbField { Bm = "dimodelkan oleh", En = "modelled by", Keys = new[] { "Drawn By", "Dimodel_Oleh", "Dimodelkan Oleh", "Modelled By", "Modeled By", "Dilukis Oleh" } },
            new TbField { Bm = "timestamp", En = "timestamp", Keys = new[] { "Sheet Issue Date", "Date/Time Stamp", "Timestamp", "Time Stamp", "Tarikh", "Issue Date" } },
        };

        /// <summary>The field's value on the sheet: the first of its parameters the sheet has with a
        /// real value; else the first it has at all; the project / sheet name as Revit's own fallback.</summary>
        private static bool TitleBlockValue(SpecModel m, SpecSheet s, TbField f, out string param, out string value)
        {
            param = ""; value = "";
            var found = false;
            foreach (var k in f.Keys)
            {
                if (!SpecPbData.TryFind(s.Params, k, out var p, out var v)) continue;
                if (!found) { param = p; value = v; found = true; }
                if (FieldStatus(true, p, v, out _).Length == 0) { param = p; value = v; return true; }
            }
            if (found) return true;
            if (f.Keys[0] == "Project Name" && SpecPbData.TryFind(m.ProjectInfo, "Project Name", out var pp, out var pv)) { param = pp; value = pv; return true; }
            if (f.Keys[0] == "Sheet Name") { param = "Sheet Name"; value = s.Name; return true; }
            return false;
        }

        /// <summary>
        /// C4.2.2 — every sheet's title block shows a project title, a drawing title, who modelled
        /// it and a timestamp (PB p.103). One fail per sheet and empty field; a Revit placeholder
        /// ("Author") counts as empty. A sheet whose parameters were not read is unjudged.
        /// </summary>
        internal static IEnumerable<SpecResult> C4_2_2(SpecModel m)
        {
            const string id = "C4.2.2";
            var unread = SheetsUnread(m, id, true);
            if (unread != null) { yield return unread; yield break; }
            var fails = 0;
            var judged = 0;
            foreach (var s in m.Sheets)
            {
                if (s.Params.Count == 0)
                {
                    yield return SpecChecks.Unjudged(id, "Sheet " + SheetLabel(s) + ": parameter title block tidak dibaca", s.Id, "Sheet " + SheetLabel(s),
                            key: s.Id > 0 ? null : s.Number + "|" + s.Name)
                        .En("Sheet " + SheetLabel(s) + ": title-block parameters were not read");
                    continue;
                }
                judged++;
                foreach (var f in TitleBlockFields)
                {
                    var found = TitleBlockValue(m, s, f, out var param, out var value);
                    var st = FieldStatus(found, found ? param : f.Keys[0], value, out var stEn);
                    if (st.Length == 0) continue;
                    fails++;
                    yield return SpecChecks.Fail(id, "Sheet " + SheetLabel(s) + ": " + f.Bm + (found ? " (" + param + ")" : "") + " " + st,
                            s.Id, "Sheet " + SheetLabel(s), actual: Show(value), required: f.Bm + " diisi",
                            key: (s.Id > 0 ? s.Id.ToString(CultureInfo.InvariantCulture) : s.Number + "|" + s.Name) + "|" + f.En)
                        .En("Sheet " + SheetLabel(s) + ": " + f.En + (found ? " (" + param + ")" : "") + " " + stEn);
                }
            }
            if (fails == 0 && judged > 0)
                yield return SpecChecks.Pass(id, judged + " sheet: tajuk projek, tajuk lukisan, dimodelkan oleh, timestamp diisi", judged)
                    .En(judged + " sheets: project title, drawing title, modelled by, timestamp filled");
        }

        /// <summary>
        /// C4.2.3 — human decides: the add-in shows the softcopy timestamps it found; matching them
        /// with the hardcopy is manual (no access to the hardcopy).
        /// </summary>
        internal static IEnumerable<SpecResult> C4_2_3(SpecModel m)
        {
            const string id = "C4.2.3";
            if (SheetsUnread(m, id, false) != null) yield break;
            var stamps = new List<string>();
            foreach (var s in m.Sheets)
            {
                var ok = TitleBlockValue(m, s, TitleBlockFields[3], out _, out var v);
                stamps.Add(Show(s.Number) + " = " + (ok ? Show(v) : "—"));
            }
            var shown = stamps.Count == 0 ? "tiada sheet" : string.Join(", ", stamps.Take(10)) + (stamps.Count > 10 ? " …" : "");
            var shownEn = stamps.Count == 0 ? "no sheets" : string.Join(", ", stamps.Take(10)) + (stamps.Count > 10 ? " …" : "");
            yield return SpecChecks.Context(id, "Timestamp softcopy: " + shown + " — padanan dengan hardcopy SEMAK MANUAL")
                .En("Softcopy timestamps: " + shownEn + " — matching the hardcopy is a MANUAL check");
        }

        // ═══════════════════════ C5 — Links ═══════════════════════

        /// <summary>The link is a model of discipline <paramref name="d"/>: its name holds "jkr" + d
        /// ("jkrST24_…"), or a name segment is the code itself ("…_ST_…", "ST24").</summary>
        internal static bool LinkIs(SpecLink l, string d)
        {
            var name = l.Name ?? "";
            if (Has(name, "jkr" + d)) return true;
            var segs = name.Split(new[] { '_', '-', ' ', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segs)
            {
                if (!seg.StartsWith(d, StringComparison.Ordinal)) continue;        // upper-case code only
                var rest = seg.Substring(d.Length);
                if (rest.All(char.IsDigit)) return true;
            }
            return false;
        }

        private static string[] RequiredLinkDisciplines(SpecModel m) =>
            new[] { "ST", "ME", "EL" }.Where(d => d != m.Discipline).ToArray();

        private static SpecResult LinksUnread(SpecModel m, string id)
        {
            var err = ReadError(m, "Links", "Link");
            return err == null ? null : SpecChecks.Unjudged(id, "Link tidak dapat dibaca: " + err).En("Links could not be read: " + err);
        }

        /// <summary>
        /// C5.1.1 — ST, ME and EL are each linked and every link of them is Loaded. A discipline
        /// with no link, or a link of it not Loaded, fails (KRITIKAL). The model's own discipline
        /// is not asked of itself.
        /// </summary>
        internal static IEnumerable<SpecResult> C5_1_1(SpecModel m)
        {
            const string id = "C5.1.1";
            var unread = LinksUnread(m, id);
            if (unread != null) { yield return unread; yield break; }
            var fails = 0;
            var ok = new List<string>();
            foreach (var d in RequiredLinkDisciplines(m))
            {
                var links = m.Links.Where(l => LinkIs(l, d)).ToList();
                if (links.Count == 0)
                {
                    fails++;
                    yield return SpecChecks.Fail(id, "KRITIKAL: tiada link " + d, elementName: "Link " + d, actual: "tiada link",
                            required: "link " + d + " berstatus Loaded", key: d)
                        .En("CRITICAL: no " + d + " link");
                    continue;
                }
                var notLoaded = links.Where(l => !l.IsLoaded).ToList();
                foreach (var l in notLoaded)
                {
                    fails++;
                    yield return SpecChecks.Fail(id, "KRITIKAL: link " + d + " '" + l.Name + "' " + Show(l.Status), elementName: "Link " + l.Name,
                            actual: Show(l.Status), required: "Loaded", key: d + "|" + l.Name)
                        .En("CRITICAL: " + d + " link '" + l.Name + "' " + Show(l.Status));
                }
                if (notLoaded.Count == 0) ok.Add(d + " (" + string.Join(", ", links.Select(l => l.Name)) + ")");
            }
            if (fails == 0)
                yield return SpecChecks.Pass(id, "Link Loaded: " + string.Join("; ", ok), ok.Count)
                    .En("Links Loaded: " + string.Join("; ", ok));
        }

        /// <summary>C5.1.2 — link file names against the BPEP list: the BPEP is backend data, so unjudged here.</summary>
        internal static IEnumerable<SpecResult> C5_1_2(SpecModel m)
        {
            var names = m.Links.Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var list = names.Count == 0 ? "tiada link" : string.Join(", ", names);
            yield return SpecChecks.Unjudged("C5.1.2", "BPEP tidak tersedia → tidak dapat disemak (link: " + list + ")")
                .En("No BPEP available → cannot be checked (links: " + (names.Count == 0 ? "none" : string.Join(", ", names)) + ")");
        }

        /// <summary>C5.1.3 — human decides, always: the add-in has no CDE / sync access to know the latest version.</summary>
        internal static IEnumerable<SpecResult> C5_1_3(SpecModel m)
        {
            var names = m.Links.Select(l => l.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            yield return SpecChecks.Context("C5.1.3", "Kena semak versi terkini secara manual (" + names.Count + " link" + (names.Count > 0 ? ": " + string.Join(", ", names) : "") + ")")
                .En("The latest version must be checked manually (" + names.Count + " links" + (names.Count > 0 ? ": " + string.Join(", ", names) : "") + ")");
        }
    }
}
