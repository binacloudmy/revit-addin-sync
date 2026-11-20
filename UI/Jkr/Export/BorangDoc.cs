using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Export
{
    /// <summary>Header block of the form (template <c>h</c>).</summary>
    public sealed class BorangHeader
    {
        public string Project = "";
        public string Model = "";
        public string File = "";
        /// <summary>DATE format: dd.MM.yyyy.</summary>
        public string Date = "";
    }

    /// <summary>A standard row: No. · Description · √ · X · Remarks (template <c>r</c>).</summary>
    public sealed class BorangRow
    {
        public string No = "";
        public string Text = "";
        /// <summary>Second line in italics (C4.2's time-stamp note).</summary>
        public string TextItalic = "";
        /// <summary>Sub-item ("a) …"): indented, no number.</summary>
        public bool Indent;
        /// <summary>"y" | "n" | "m" | "" (not assessed).</summary>
        public string Mark = "";
        public string Remark = "";
        public string RemarkColor = BorangDoc.TextColor;
        public string Yes => Mark == "y" ? BorangDoc.Tick : "";
        public string No2 => Mark == "n" ? BorangDoc.Cross : "";
    }

    /// <summary>A Section D row: four aspect columns (template <c>secD1</c> / <c>secD2</c>).</summary>
    public sealed class BorangDRow
    {
        public string No = "";
        public string Cat = "";
        /// <summary>Marks per aspect, Naming · Quality · Information · Geometry ("y"/"n"/"m"/"").</summary>
        public string N = "", Q = "", I = "", G = "";
        public string Remark = "";
        public string RemarkColor = BorangDoc.TextColor;
    }

    /// <summary>
    /// The Borang Model Audit BIM010 as the export template prints it
    /// (2-design/claude-design/borang-export-template.html + DESIGN-HANDOFF.md):
    /// every row of the form, marked from the run's BIM010 summary (which holds a
    /// mark for every row, passing ones included — never rebuilt from the issue
    /// list), with remarks from the issues on that row. With a spec-based summary
    /// (SpecBorang: the marks and summary box of the JKR Model Audit spec, the same
    /// numbers as the Results screen) the remarks list the issues behind each mark.
    /// Three-state rule: comply
    /// → √, not comply → X, a person decides → neither, remark "SEMAK MANUAL — …".
    /// The template is English only, so the form is English whatever the pane's
    /// language. WPF- and Revit-free.
    /// </summary>
    public sealed class BorangDoc
    {
        public const string Tick = "√";
        public const string Cross = "X";
        public const string Dash = "—";
        public const string Green = "#0A6B3D";
        public const string Red = "#A32A18";
        public const string ManualBlue = "#2F4A66";
        public const string TextColor = "#333";
        /// <summary>Most issue titles listed in one remark.</summary>
        public const int MaxRemarkItems = 5;

        public BorangHeader Header = new BorangHeader();
        public List<BorangRow> SecA = new List<BorangRow>();
        public List<BorangRow> SecB = new List<BorangRow>();
        public List<BorangRow> CViews = new List<BorangRow>();
        public BorangRow CLegendsHead;
        public List<BorangRow> CLegends = new List<BorangRow>();
        public BorangRow CSchedHead;
        public List<BorangRow> CSched = new List<BorangRow>();
        public BorangRow CSheetsHead;
        public List<BorangRow> CSheets = new List<BorangRow>();
        public BorangRow C42;
        public BorangRow C51;
        public List<BorangDRow> SecD = new List<BorangDRow>();
        public List<BorangRow> SecE = new List<BorangRow>();
        /// <summary>"84" or "—" when nothing was AI-verifiable.</summary>
        public string SumPct = "—";
        /// <summary>"4,612".</summary>
        public string SumTotal = "0";
        public int SumManual;
        /// <summary>Reference document versions under the summary, as the template prints them.</summary>
        public IReadOnlyList<string> RefVersions = TemplateRefVersions;

        public static readonly IReadOnlyList<string> TemplateRefVersions = new[]
        {
            "Need Statement v3.1 · 14.02.2026", "BPEP v2.0 · 08.11.2025",
            "Buku Parameter v1.4 · 30.06.2026", "JKR AR template v5a",
        };

        // ── wording, exactly as the template prints it ──
        private static readonly string[] AText =
        {
            "Architectural Model developed in accordance to specified architectural design.",
            "Architectural Model files are named according to the PWD’s BIM Guidelines and Standards. Please specify the guidelines and standards if using other than PWD’s BIM Guidelines and Standards.",
            "Architectural Model is structured according to rules that are easily managed. For example, architectural model is managed according to block, tower and podium.",
            "Architectural Model to be positioned appropriately to the Project Base Point preferably Grid A and 1.",
            "Architectural Model should be properly organised and using architectural gridlines and levels. Floor plan should be associated with appropriate levels.",
            "Rooms are catagorised by departments.",
            "All materials are included in model elements.",
        };

        private static readonly (string No, string Text)[] Views =
        {
            ("1.1", "Views (Architectural) created based from PWD’s architectural template."),
            ("1.2", "WIP views are used for design development and design study purposes."),
            ("1.3", "PBT views are used for PBT submission purposes."),
            ("1.4", "BOMBA views are used for BOMBA submission purposes."),
            ("1.5", "Dokumen views are used for contract documentation, contract implementation and final completion."),
            ("1.6", "Spatial and Zoning Analysis are generated from Area Plan."),
        };

        private static readonly string[] Legends = { "a) Legends for Components", "b) General Notes" };
        private static readonly string[] Schedules = { "a) Schedule of Accommodation", "b) Building Component", "c) Material Takeoff Schedule" };
        private static readonly string[] Sheets =
        {
            "a) Title Block", "b) Drawing Title", "c) List of Drawings", "d) General Notes", "e) Plans",
            "f) Elevations", "g) Sections", "h) Details", "i) Others: _________________",
            "Sheets should be organised with appropriate view and scale.",
        };

        public static readonly string[] DCategories =
        {
            "Floors", "Walls", "Ceilings", "Roofs", "Stairs", "Railings", "Ramps", "Room", "Curtain Systems", "Doors",
            "Windows", "Caseworks", "Furniture", "Plumbing Fixtures", "Furniture Systems", "Specialty Equipment",
            "Generic Models", "Columns", "Parking", "Pipes — RWDP", "Structural Column", "Toposurface", "Mass",
        };

        /// <summary>Section D rows on the first D page (template secD1); the rest go with E.</summary>
        public const int DFirstPage = 16;

        /// <summary>
        /// Build the form. <paramref name="summary"/> marks every row;
        /// <paramref name="issues"/> give the remarks (with "fixed, not yet
        /// re-scanned" / "ignored by user"); a row with no issues uses the
        /// summary's notes.
        /// </summary>
        public static BorangDoc Build(Bim010Summary summary, IEnumerable<IssueVm> issues, BorangHeader header)
        {
            var list = (issues ?? Enumerable.Empty<IssueVm>()).Where(i => i != null).ToList();
            var doc = new BorangDoc { Header = header ?? new BorangHeader() };

            BorangRow Row(string key, string no, string text)
            {
                var mark = MarkOf(summary, key);
                var (remark, color) = RemarkFor(key, mark, summary, list);
                return new BorangRow { No = no, Text = text, Mark = mark, Remark = remark, RemarkColor = color };
            }

            BorangRow Sub(string text) => new BorangRow { Text = text, Indent = true };

            for (var i = 0; i < AText.Length; i++) doc.SecA.Add(Row("A" + (i + 1), (i + 1) + ".", AText[i]));
            doc.SecB.Add(Row("B1", "1.", "Project Information should be updated."));
            foreach (var (no, text) in Views) doc.CViews.Add(Row("C" + no, no, text));

            // C2.1 / C3.1 / C4.1 are one row each in the run (one verdict for the whole
            // item), so the mark and remark go on the numbered row and the a) b) c) lines
            // stay unmarked: ticking every sub-item from one verdict would claim more
            // than was checked.
            doc.CLegendsHead = Row("C2.1", "2.1", "Contents of Legends are managed as follows:");
            doc.CLegends.AddRange(Legends.Select(Sub));
            doc.CSchedHead = Row("C3.1", "3.1", "Schedules/Quantities shall be generated according to:");
            doc.CSched.AddRange(Schedules.Select(Sub));
            doc.CSheetsHead = Row("C4.1", "4.1", "Contents of Sheets are architectural drawings that are managed according to the rules for architecture as follows:");
            doc.CSheets.AddRange(Sheets.Select(Sub));
            doc.C42 = Row("C4.2", "4.2", "All architectural drawings should use JKR Title Block with furnished information.");
            doc.C42.TextItalic = "Time stamp should be included on the Sheets.";
            doc.C51 = Row("C5.1", "5.1", "Files for the models linked with Architectural Model are correct and up-to-date");

            for (var i = 0; i < DCategories.Length; i++)
            {
                var key = "D" + (i + 1);
                Bim010RowMark rm = null;
                summary?.Rows.TryGetValue(key, out rm);
                string Col(string c) => rm != null && rm.Columns.TryGetValue(c, out var m) ? m ?? "" : "";
                var (remark, color) = RemarkFor(key, rm?.Mark ?? "", summary, list);
                doc.SecD.Add(new BorangDRow
                {
                    No = (i + 1) + ".", Cat = DCategories[i],
                    N = Col(Bim010Column.Naming), Q = Col(Bim010Column.Quality),
                    I = Col(Bim010Column.Information), G = Col(Bim010Column.Geometry),
                    Remark = remark, RemarkColor = color,
                });
            }

            doc.SecE.Add(Row("E1.1", "1.1", "BOMBA Submission"));
            doc.SecE.Add(Row("E1.2", "1.2", "PBT Submission"));

            doc.SumPct = summary?.Pct?.ToString(CultureInfo.InvariantCulture) ?? Dash;
            doc.SumTotal = (summary?.AiTotal ?? 0).ToString("N0", CultureInfo.GetCultureInfo("en-US"));
            doc.SumManual = summary?.Pending ?? 0;
            return doc;
        }

        public static string MarkOf(Bim010Summary summary, string key) =>
            summary != null && summary.Rows.TryGetValue(key, out var m) ? m.Mark ?? "" : "";

        /// <summary>Remarks for a row: what failed (with its state), then what a person
        /// must decide ("SEMAK MANUAL — reason"). Red when the row is X, blue-grey when
        /// a person decides, else dark grey.</summary>
        public static (string Text, string Color) RemarkFor(string key, string mark, Bim010Summary summary, IReadOnlyList<IssueVm> issues)
        {
            if (summary != null && summary.SpecBased)
                return SpecRemarkFor(key, mark, summary, issues ?? Array.Empty<IssueVm>());
            var onRow = issues.Where(i => string.Equals(i.BorangKey, key, StringComparison.OrdinalIgnoreCase)
                                          && !(i.Status == IssueStatus.Fixed && i.FixVerified)).ToList();
            var parts = new List<string>();

            var fails = onRow.Where(i => !i.NeedsPerson).Select(FailText).Where(t => t.Length > 0).Distinct().ToList();
            parts.AddRange(Cap(fails));
            var reasons = onRow.Where(i => i.NeedsPerson).Select(DescriptionEn).Where(t => t.Length > 0).Distinct().ToList();
            if (reasons.Count > 0) parts.Add("SEMAK MANUAL — " + string.Join(" ", Cap(reasons)));

            if (parts.Count == 0 && summary != null && summary.Rows.TryGetValue(key, out var rm) && rm.Notes.Count > 0 && mark != "y")
            {
                var notes = Cap(rm.Notes.Select(CleanEn).Where(n => n.Length > 0).Distinct().ToList());
                parts.Add(mark == "m" ? "SEMAK MANUAL — " + string.Join(" ", notes) : string.Join("; ", notes));
            }

            var text = string.Join("; ", parts);
            var color = mark == "n" ? Red : mark == "m" ? ManualBlue : TextColor;
            return (text, color);
        }

        /// <summary>
        /// Remarks for a row of a spec-based summary (SpecBorang): the issues behind the
        /// row's mark, picked by id (not by the issue's Borang key: an older checker's
        /// issue on the same row may say X where the spec says a person decides).
        /// Failures first (their state: fixed / ignored); then "SEMAK MANUAL — " with
        /// what a person decides — issues the spec leaves to a person are listed there
        /// even when their checker called them a failure — and the checks not yet
        /// checked. With no issues, the row's notes. A √ row has no remark.
        /// </summary>
        private static (string Text, string Color) SpecRemarkFor(string key, string mark, Bim010Summary summary, IReadOnlyList<IssueVm> issues)
        {
            var color = mark == "n" ? Red : mark == "m" ? ManualBlue : TextColor;
            if (!summary.Rows.TryGetValue(key, out var rm) || rm == null || mark == "y" || mark == "") return ("", color);

            var live = issues.Where(i => i != null && !(i.Status == IssueStatus.Fixed && i.FixVerified)).ToList();
            List<IssueVm> Of(IEnumerable<string> ids) =>
                (ids ?? Enumerable.Empty<string>()).SelectMany(id => live.Where(i => i.Id == id)).Distinct().ToList();
            var failIssues = Of(rm.FailIssueIds);
            var manualIssues = Of(rm.ManualIssueIds).Concat(failIssues.Where(i => i.NeedsPerson)).Distinct().ToList();
            failIssues = failIssues.Where(i => !i.NeedsPerson).ToList();

            var parts = new List<string>();
            var fails = failIssues.Select(FailText).Where(t => t.Length > 0).Distinct().ToList();
            if (fails.Count == 0 && mark == "n")
            {
                var manualNotes = new HashSet<string>(rm.ManualNotes ?? new List<string>());
                fails = rm.Notes.Where(n => !manualNotes.Contains(n) && n != rm.NotCheckedNote).Select(Clean).Where(n => n.Length > 0).Distinct().ToList();
            }
            // A check that lists everything in one note (every missing level / link): it comes first,
            // so the cap of the issue texts below still leaves the whole list in the remark.
            if (fails.Count > 0 && rm.SummaryNotes != null)
                fails.InsertRange(0, rm.SummaryNotes.Select(CleanEn).Where(n => n.Length > 0).Distinct());
            if (fails.Count > 0) parts.Add(string.Join("; ", Cap(fails)));

            var reasons = manualIssues.Select(i => DescriptionEn(i).Length > 0 ? DescriptionEn(i) : FailText(i))
                                      .Where(t => t.Length > 0).Distinct().ToList();
            if (reasons.Count == 0) reasons = (rm.ManualNotes ?? new List<string>()).Select(CleanEn).Where(n => n.Length > 0).Distinct().ToList();
            reasons = Cap(reasons);
            if (!string.IsNullOrEmpty(rm.NotCheckedNote)) reasons.Add(rm.NotCheckedNote);
            if (reasons.Count > 0) parts.Add("SEMAK MANUAL — " + string.Join("; ", reasons));

            return (string.Join("; ", parts), color);
        }

        private static string FailText(IssueVm i)
        {
            // The local BIM010 rows' titles repeat the form row ("D5 · Stairs"): their
            // remark is what was found.
            var local = (i.CheckId ?? "").StartsWith("BIM010-", StringComparison.Ordinal);
            var text = local && !string.IsNullOrWhiteSpace(i.Description) ? DescriptionEn(i) : TitleEn(i);
            if (text.Length == 0) return "";
            if (i.Status == IssueStatus.Fixed) return text + " (fixed, not yet re-scanned)";
            if (i.Status == IssueStatus.ManualFixNeeded) return text + " (handled manually)";
            if (i.Status == IssueStatus.Ignored || i.Status == IssueStatus.Approved)
                return text + " (ignored by user)";
            return text;
        }

        private static List<string> Cap(List<string> items)
        {
            if (items.Count <= MaxRemarkItems) return items;
            var shown = items.Take(MaxRemarkItems).ToList();
            shown.Add("+" + (items.Count - MaxRemarkItems) + " more");
            return shown;
        }

        /// <summary>The Borang is English only: a checker remark written in BM is put in
        /// English where the pane knows its wording (PaneText.AuditRemarkEn); any other
        /// text stays as written.</summary>
        /// <summary>An issue's description in English: the spec result's own English, else the
        /// checker's remark where the pane knows its wording.</summary>
        private static string DescriptionEn(IssueVm i) =>
            !string.IsNullOrWhiteSpace(i.DescriptionEn) ? Clean(i.DescriptionEn) : CleanEn(i.Description);

        private static string TitleEn(IssueVm i) =>
            !string.IsNullOrWhiteSpace(i.TitleEn) ? Clean(i.TitleEn) : CleanEn(i.Title);

        private static string CleanEn(string s)
        {
            var c = Clean(s);
            return c.Length == 0 ? c : Clean(PaneText.AuditRemarkEn(c) ?? c);
        }

        private static string Clean(string s) =>
            string.Join(" ", (s ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)).Trim();

        /// <summary>Glyph for a Section D aspect mark: √, X, — (a person decides) or blank.</summary>
        public static string DGlyph(string mark) => mark == "y" ? Tick : mark == "n" ? Cross : mark == "m" ? Dash : "";
        public static string DColor(string mark) => mark == "y" ? Green : mark == "n" ? Red : ManualBlue;

        // ── header helpers ──
        /// <summary>"Architecture · AR · LOD 300" (AR named, as the template shows; other disciplines by code).</summary>
        public static string ModelLine(string disc, string lod)
        {
            var d = string.IsNullOrWhiteSpace(disc) ? "AR" : disc.Trim().ToUpperInvariant();
            var name = d == "AR" ? "Architecture · AR" : d;
            return string.IsNullOrWhiteSpace(lod) ? name : name + " · LOD " + lod.Trim();
        }

        /// <summary>The project: Revit's Project Name when it is set, else the code in
        /// brackets in the file name ("…(BEde1A_p14-001)…"), else the file name.</summary>
        public static string ProjectName(string projectInfoName, string fileName)
        {
            var p = (projectInfoName ?? "").Trim();
            if (p.Length > 0 && !string.Equals(p, "Project Name", StringComparison.OrdinalIgnoreCase)) return p;
            var f = fileName ?? "";
            var open = f.IndexOf('(');
            var close = open >= 0 ? f.IndexOf(')', open + 1) : -1;
            if (open >= 0 && close > open + 1) return f.Substring(open + 1, close - open - 1);
            return System.IO.Path.GetFileNameWithoutExtension(f);
        }

        public static string DateOf(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }
}
