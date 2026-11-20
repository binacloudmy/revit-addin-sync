using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// BM / EN text the add-in writes itself, outside the prototype's strings
    /// (<see cref="PanelStrings"/>): toasts, busy labels, dialogs, Locate
    /// messages, how-to-fix steps and fix-failure reasons. Every method takes the
    /// language; null means the pane's current one (<see cref="PaneLanguage"/>).
    /// Text from the checker or the backend (issue titles, rules, remarks) comes
    /// in one language and is shown as it arrives.
    /// </summary>
    public static class PaneText
    {
        private static bool En(string lang) =>
            string.Equals(lang ?? PaneLanguage.Current, PanelStrings.En, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when <paramref name="lang"/> (null = the pane's) is English.</summary>
        public static bool IsEn(string lang) => En(lang);

        private static string L(string lang, string ms, string en) => En(lang) ? en : ms;

        private static string S(int n) => n == 1 ? "" : "s";

        public const string DialogTitle = "BINA JKR Compliance";

        // ── fix / ignore ──
        public static string NoAutoFixHere(string lang = null) =>
            L(lang, "Tiada isu yang boleh dibaiki secara automatik di sini.", "No auto-fixable issues here.");
        public static string NoAutoFixFound(string lang = null) =>
            L(lang, "Tiada isu yang boleh dibaiki secara automatik ditemui.", "No auto-fixable issues found.");
        public static string FixRunning(string lang = null) =>
            L(lang, "Pembaikan sedang berjalan — sila tunggu.", "A fix is already running — please wait.");
        public static string AutoFixUnavailable(string lang = null) =>
            L(lang, "Auto-fix tidak tersedia — pengendali pembaikan belum sedia. Mulakan semula Revit dan cuba lagi.",
                    "Auto-fix unavailable — the fix handler is not ready. Restart Revit and try again.");
        public static string NoFixData(string lang = null) =>
            L(lang, "Isu ini tiada data pembaikan yang boleh digunakan.", "No machine-readable fix attached to this issue.");
        public static string RenameTargetEmpty(string lang = null) =>
            L(lang, "Nama baharu kosong.", "Rename target is empty.");
        public static string FixAllError(string message, string lang = null) =>
            L(lang, "Ralat semasa Baiki semua:", "Fix all error:") + "\n\n" + message;
        public static string AutoFixError(string message, string lang = null) =>
            L(lang, "Ralat auto-fix:", "Auto-fix error:") + "\n\n" + message;
        public static string AiAnalysing(int n, string lang = null) =>
            L(lang, $"AI sedang menganalisis {n} isu…", $"AI analysing {n} issue{S(n)}…");
        public static string AiUnavailable(string lang = null) =>
            L(lang, "Pembaikan AI tidak tersedia — hanya pembaikan standard digunakan.",
                    "AI fixes unavailable — applying standard fixes only.");
        public static string Applying(int n, string lang = null) =>
            L(lang, $"Menulis {n} perubahan…", $"Applying {n} change{S(n)}…");
        public static string AppliedRescanning(int n, string lang = null) =>
            L(lang, $"{n} pembaikan ditulis. Mengimbas semula…", $"Applied {n} fix{(n == 1 ? "" : "es")}. Re-scanning…");
        public static string Rescanning(string lang = null) =>
            L(lang, "Mengimbas semula model…", "Re-scanning model…");
        public static string RevitBusy(string lang = null) =>
            L(lang, "Revit sedang sibuk. Tutup dialog atau arahan yang terbuka, kemudian cuba lagi.",
                    "Revit is busy. Close any open dialog or command and try again.");
        public static string NothingWritten(string lang = null) =>
            L(lang, "Tiada pembaikan dibuat. Tiada apa-apa ditulis ke model.", "No fixes applied. Nothing was written to the model.");
        public static string FixFailedNothingWritten(string lang = null) =>
            L(lang, "Pembaikan gagal. Tiada apa-apa ditulis ke model.", "Fix failed. Nothing was written to the model.");
        public static string NeedsPerson(string lang = null) =>
            L(lang, "Item ini perlu disemak oleh manusia — AI tidak menilainya.",
                    "This item needs a person to review it — the AI did not judge it.");

        /// <summary>prototype toastFix: "3 change(s) written to model"; with failures
        /// "· 1 failed, still Open" (the failed list follows in a dialog).</summary>
        public static string ChangesWritten(int written, int failed = 0, string lang = null)
        {
            var msg = written + " " + PanelStrings.Get(lang ?? PaneLanguage.Current, "toastFix");
            if (failed > 0) msg += L(lang, $" · {failed} gagal, masih Terbuka", $" · {failed} failed, still Open");
            return msg;
        }

        /// <summary>prototype toastLoc: "Selected in model: Doors [204511] PT2p600a".</summary>
        public static string SelectedInModel(string element, string lang = null) =>
            PanelStrings.Get(lang ?? PaneLanguage.Current, "toastLoc") + ": " + element;

        /// <summary>Toast time: 2.6 s, longer for a long notice so it can be read
        /// (about 60 ms a character, at most 8 s).</summary>
        public static TimeSpan ToastTime(string message)
        {
            var reading = (message ?? "").Length * 60;
            return TimeSpan.FromMilliseconds(Math.Max(2600, Math.Min(8000, reading)));
        }

        public static string RescanAfterFixFailed(string message, string lang = null) =>
            L(lang, "Imbasan semula selepas pembaikan gagal:", "Re-scan after fix failed:") + "\n\n" + message;

        /// <summary>The dialog listing the fixes of a batch that failed.</summary>
        public static string FailedFixes(int applied, int failed, string details, string lang = null)
        {
            var head = applied > 0
                ? L(lang, $"{applied} perubahan ditulis sebagai satu langkah undo. {failed} pembaikan gagal dan tidak ditulis:",
                          $"{applied} change{S(applied)} applied as one undo step. {failed} fix{(failed == 1 ? "" : "es")} failed and {(failed == 1 ? "was" : "were")} not written:")
                : L(lang, $"{failed} pembaikan gagal. Tiada apa-apa ditulis ke model:",
                          $"{failed} fix{(failed == 1 ? "" : "es")} failed. Nothing was written to the model:");
            return head + "\n\n" + details + "\n\n" +
                   L(lang, "Isu ini kekal Terbuka, dengan sebab ditunjukkan pada setiap baris.",
                           "These issues stay Open, with the reason shown on each row.");
        }

        /// <summary>One line of that list: what was changed.</summary>
        public static string RenameWhat(long elementId, string newName, string lang = null) =>
            L(lang, $"Tukar nama {elementId} → '{newName}'", $"Rename {elementId} → '{newName}'");
        public static string ParamWhat(string parameter, long elementId, string lang = null) =>
            L(lang, $"{parameter} pada {elementId}", $"{parameter} on {elementId}");
        public static string AndMore(int n, string lang = null) =>
            L(lang, $"…dan {n} lagi", $"…and {n} more");

        public static string IssuesIgnored(int n, string lang = null) =>
            n + " " + PanelStrings.Get(lang ?? PaneLanguage.Current, "toastIgn");
        public static string NoMedLowToIgnore(string lang = null) =>
            L(lang, "Tiada isu Sederhana/Rendah untuk diabaikan.", "No Medium/Low issues to ignore.");
        public static string IgnoredMedLow(int n, string lang = null) =>
            L(lang, $"{n} isu Sederhana/Rendah diabaikan.", $"Ignored {n} Medium/Low issue{S(n)}.");

        /// <summary>Toast after one decision: "Ignored · Door width…".</summary>
        public static string Decision(string status, string title, string lang = null)
        {
            string label;
            switch (status)
            {
                case "Fixed": label = L(lang, "Dibaiki", "Fixed"); break;
                case "Ignored": label = L(lang, "Diabaikan", "Ignored"); break;
                case "Approved": label = L(lang, "Diluluskan", "Approved"); break;
                case "ManualFixNeeded": label = L(lang, "Ditanda manual", "Marked manual"); break;
                default: label = L(lang, "Dibuka semula", "Reopened"); break;
            }
            return label + " · " + title;
        }

        public static string WrongModel(string scannedFile, string lang = null) =>
            L(lang, $"Pembaikan ini untuk model {scannedFile}. Aktifkan model itu (atau jalankan audit pada model ini) dan cuba lagi. Tiada apa-apa ditulis.",
                    $"These fixes are for {scannedFile}. Make that model active (or run the audit on this one) and try again. Nothing was written.");

        // ── scan / reset ──
        public static string NoDocument(string lang = null) =>
            L(lang, "Tiada dokumen Revit aktif. Buka model dahulu.", "No active Revit document. Open a model first.");
        public static string ScanError(string message, string lang = null) =>
            L(lang, "Ralat imbasan:", "Scan error:") + "\n\n" + message;
        public static string ScanFailed(string message, string lang = null) =>
            L(lang, "Imbasan gagal:", "Scan failed:") + "\n\n" + message;
        public static string WaitThenReset(string lang = null) =>
            L(lang, "Sila tunggu sehingga imbasan atau pembaikan selesai, kemudian Set semula.",
                    "Please wait until the scan or fix finishes, then Reset.");
        public static string ResetDone(string lang = null) =>
            L(lang, "Set semula: keputusan audit dan keputusan anda dikosongkan. Tiada apa-apa dalam model diubah. Jalankan audit sekali lagi untuk bermula semula.",
                    "Reset: results and decisions cleared. Nothing in the model was changed. Run the audit again to start over.");
        public static string LocateFailed(string message, string lang = null) =>
            L(lang, "Tidak dapat menunjukkan elemen dalam paparan — ", "Couldn't locate element in view — ") + message;
        public static string DetailFailed(string message, string lang = null) =>
            L(lang, "Butiran isu tidak dapat dibuka:", "Failed to open issue detail:") + "\n\n" + message;

        // ── export ──
        public static string ExportIssues(string lang = null) =>
            L(lang, "Eksport isu", "Export issues");
        public static string BorangPreviewTitle(string lang = null) =>
            L(lang, "Pratonton Borang BIM010", "Borang BIM010 preview");
        public static string BorangPreviewNote(string lang = null) =>
            L(lang, "Diisi daripada templat BIM010 HOD, bahagian A hingga E, tidak diubah",
                    "Filled from the HOD’s BIM010 template, sections A to E, unchanged");
        public static string PageOf(int page, int pages, string lang = null) =>
            L(lang, $"Halaman {page} daripada {pages}", $"Page {page} of {pages}");
        public static string PreviousPage(string lang = null) => L(lang, "Halaman sebelumnya", "Previous page");
        public static string NextPage(string lang = null) => L(lang, "Halaman seterusnya", "Next page");
        public static string ZoomHint(string lang = null) => L(lang, "Ctrl + skrol untuk zum", "Ctrl + scroll to zoom");
        public static string SavePdf(string lang = null) => L(lang, "Simpan PDF", "Save PDF");
        public static string FormatExcel(string lang = null) => "Excel (.xlsx)";
        public static string FormatCsv(string lang = null) => "CSV (.csv)";
        public static string IssueCount(int n, string lang = null) =>
            L(lang, $"{n} isu", $"{n} issue{S(n)}");
        public static string ExportDialogHint(string lang = null) =>
            L(lang, "Pilih format dan isu yang dimasukkan dalam fail.", "Choose the format and which issues go in the file.");
        public static string ExportFormat(string lang = null) => L(lang, "Format", "Format");
        public static string ExportInclude(string lang = null) => L(lang, "Sertakan", "Include");
        public static string ExportWillWrite(int n, string lang = null) =>
            L(lang, $"{n} isu akan ditulis", $"{n} issue{S(n)} will be written");
        public static string ExportAction(string lang = null) => L(lang, "Eksport", "Export");
        public static string Cancel(string lang = null) => L(lang, "Batal", "Cancel");
        public static string PdfSaved(string path, string lang = null) =>
            L(lang, "PDF disimpan: ", "PDF saved: ") + path;
        public static string PdfSaveFailed(string message, string lang = null) =>
            L(lang, "PDF tidak dapat disimpan: ", "PDF could not be saved: ") + message;
        public static string ExportFailed(string message, string lang = null) =>
            L(lang, "Borang tidak dapat dijana: ", "The Borang could not be generated: ") + message;
        /// <summary>The Excel / CSV issue export wrote its file (the name, not the folder).</summary>
        public static string IssuesExported(string path, string lang = null) =>
            L(lang, "Isu dieksport: ", "Issues exported: ") + System.IO.Path.GetFileName(path ?? "");
        public static string IssuesExportFailed(string message, string lang = null) =>
            L(lang, "Isu tidak dapat dieksport: ", "Issues could not be exported: ") + message;

        // ── the BIM010 checker's remarks (fill_audit writes them in BM) ──

        private const string NoCheckBm = "Tiada semakan automatik untuk baris ini";
        private const string NoCheckEn = "No automatic check for this row";
        private const string ContextBm = "Konteks model: ";

        // fill_audit's context labels (AuditCheckers.ContextProviders), longest first.
        private static readonly (string Ms, string En)[] ContextLabels =
        {
            ("amaran (warning) dalam model", "warnings in the model"),
            ("parameter terikat pada projek", "parameters bound to the project"),
            ("family dimuatkan", "loaded families"),
            ("design option", "design options"),
            ("fasa (phase)", "phases"),
            ("view Legend", "Legend views"),
            ("view grafik", "graphical views"),
            ("Revit link", "Revit links"),
            ("scope box", "scope boxes"),
            ("Area Plan", "Area Plans"),
            ("workset", "worksets"),
            ("jadual", "schedules"),
            ("sheet", "sheets"),
            ("grid", "grids"),
            ("aras", "levels"),
        };

        /// <summary>
        /// A remark of the BIM010 checker in BM and EN. It writes its remarks in BM; the
        /// ones the pane knows ("Tiada semakan automatik untuk baris ini — semak manual.",
        /// with or without "Konteks model: …") get their English text, so a semak manual
        /// row follows the BM / EN switch. Any other remark shows as written in both.
        /// </summary>
        public static Text AuditRemark(string remark)
        {
            var bm = remark ?? "";
            return new Text(bm, AuditRemarkEn(bm) ?? bm);
        }

        /// <summary>The English of a known checker remark; null when there is none.</summary>
        public static string AuditRemarkEn(string remark)
        {
            var s = (remark ?? "").Trim();
            if (s == NoCheckBm + " — semak manual.") return NoCheckEn + " — semak manual.";
            const string tail = " Semak manual.";
            if (s.StartsWith(NoCheckBm + ". ", StringComparison.Ordinal) && s.EndsWith(tail, StringComparison.Ordinal))
            {
                var note = s.Substring(NoCheckBm.Length + 2, s.Length - NoCheckBm.Length - 2 - tail.Length);
                var noteEn = ContextEn(note);
                return noteEn == null ? null : NoCheckEn + ". " + noteEn + tail;
            }
            return SectionHeadingEn(s) ?? GridRemarkEn(s) ?? DepartmentRemarkEn(s) ?? EmptyCategoryRemarkEn(s);
        }

        // The rules the BIM010 checker states (AuditCheckers: RulePattern, "Peraturan: …"), BM → EN.
        private static readonly (string Ms, string En)[] CheckerRules =
        {
            ("setiap bilik (Room) ada nilai parameter 'Department'", "every room (Room) has a value in the 'Department' parameter"),
            ("setiap bilik (Room) yang diletakkan ada Number dan Name", "every placed room (Room) has a Number and a Name"),
            ("setiap elemen Walls/Floors/Ceilings/Roofs ada sekurang-kurangnya satu material", "every Walls/Floors/Ceilings/Roofs element has at least one material"),
            ("setiap view grafik ada View Template", "every graphical view has a View Template"),
            ("sekurang-kurangnya satu Area Plan untuk analisis ruang/zon", "at least one Area Plan for space/zone analysis"),
            ("sekurang-kurangnya satu view Legend untuk komponen dan nota am", "at least one Legend view for components and general notes"),
            ("setiap Sheet mengandungi sekurang-kurangnya satu view", "every Sheet contains at least one view"),
            ("nama family/type title block mengandungi 'JKR'", "the title block family/type name contains 'JKR'"),
            ("setiap Revit link berstatus 'Loaded'", "every Revit link has the status 'Loaded'"),
            (">=3 segmen dipisah '-' atau '_'", ">=3 segments separated by '-' or '_'"),
            (GridRuleBm, GridRuleStatementEn),
            ("grid > 0 dan aras > 0; setiap grid dan aras ada nama yang unik (tiada nama kosong/berganda); setiap pelan lantai terikat pada satu aras",
             "grids > 0 and levels > 0; every grid and level has a unique name (no empty or duplicate names); every floor plan is tied to one level"),
            ("Project Name/Number/Address/Client Name/Status/Issue Date terisi dan bukan nilai lalai Revit",
             "Project Name/Number/Address/Client Name/Status/Issue Date are filled and not the Revit default"),
            ("nama jenis (ElementType) yang digunakan ikut format JKR — kod JKR '(XXnnn)' / awalan 'jkrAR' / nama bahan",
             "the used type names (ElementType) follow the JKR format — JKR code '(XXnnn)' / prefix 'jkrAR' / material name"),
        };

        /// <summary>A rule the BIM010 checker states, in English; the text itself when it is not one it knows.</summary>
        public static string AuditRuleEn(string rule)
        {
            var s = (rule ?? "").Trim();
            foreach (var (ms, en) in CheckerRules) if (s == ms) return en;
            return rule ?? "";
        }

        private static readonly Regex EmptyExpected = R(@"^(\d+) elemen (.+?) dijumpai dalam model walaupun kategori ini biasanya wajib bagi model seni bina — sahkan sama ada memang tiada dalam skop projek\. \(Kualiti/maklumat/geometri: semak manual\.\)$");
        private static readonly Regex EmptyOptional = R(@"^(\d+) elemen (.+?) dijumpai dalam model dan kategori ini tidak wajib — baris ini tidak berkenaan, atau semak manual jika sepatutnya ada\.$");

        /// <summary>D rows (AuditCheckers.EvaluateCategory): a category with no elements, "0 elemen Columns dijumpai …".
        /// The category is a Revit label and stays as written.</summary>
        private static string EmptyCategoryRemarkEn(string s)
        {
            var m = EmptyExpected.Match(s);
            if (m.Success)
                return $"{m.Groups[1].Value} {m.Groups[2].Value} elements found in the model although this category is normally required in an architectural model — confirm whether it is really outside the project scope. (Quality/information/geometry: semak manual.)";
            m = EmptyOptional.Match(s);
            if (m.Success)
                return $"{m.Groups[1].Value} {m.Groups[2].Value} elements found in the model and this category is not required — this row does not apply, or semak manual if there should be some.";
            return null;
        }

        /// <summary>A6 (rooms have a Department): "Peraturan: … Isi parameter Department …" in English.</summary>
        private static string DepartmentRemarkEn(string s)
        {
            const string rule = "setiap bilik (Room) ada nilai parameter 'Department'";
            var head = "Peraturan: " + rule + ". ";
            if (!s.StartsWith(head, StringComparison.Ordinal)) return null;
            var ruleEn = "Rule: " + AuditRuleEn(rule) + ". ";
            var rest = s.Substring(head.Length);
            var all = Regex.Match(rest, @"^Semua (\d+) bilik ada nilai Department — patuh\.$");
            if (all.Success) return ruleEn + "All " + all.Groups[1].Value + " rooms have a Department value — complies.";
            if (rest == "Dijumpai 0 bilik yang diletakkan (placed Room) dalam model — letak Room dahulu sebelum kategori jabatan boleh disemak.")
                return ruleEn + "Found 0 placed rooms in the model — place Rooms first before the department can be checked.";
            var miss = Regex.Match(rest, @"^(\d+)/(\d+) bilik tiada nilai \(cth: (.*)\)\. Isi parameter Department untuk bilik tersebut\.(.*)$", RegexOptions.Singleline);
            if (miss.Success && (string.IsNullOrWhiteSpace(miss.Groups[4].Value) || FullListNoteEn(miss.Groups[4].Value).Length > 0))
                return ruleEn + miss.Groups[1].Value + "/" + miss.Groups[2].Value + " rooms have no value (e.g. " + miss.Groups[3].Value
                       + "). Fill in the Department parameter for those rooms." + FullListNoteEn(miss.Groups[4].Value);
            return null;
        }

        // AuditCheckers.FullListNote: "" or " Senarai penuh N item ada dalam draft_export (element_ids)."
        private static string FullListNoteEn(string note)
        {
            var m = Regex.Match(note ?? "", @"^\s*Senarai penuh (\d+) item ada dalam draft_export \(element_ids\)\.\s*$");
            return m.Success ? " The full list of " + m.Groups[1].Value + " items is in draft_export (element_ids)." : "";
        }

        private const string HeadingBm = "Tajuk seksyen — tiada kriteria untuk diskor. ";
        private const string HeadingEn = "Section heading — no criteria to score. ";
        private const string HeadingTailBm = "Lihat baris di bawah.";
        private const string HeadingTailEn = "See the rows below.";

        /// <summary>A section-heading row ("x.0"): "Tajuk seksyen — … [Konteks model: …] Lihat baris di bawah."</summary>
        private static string SectionHeadingEn(string s)
        {
            if (!s.StartsWith(HeadingBm, StringComparison.Ordinal) || !s.EndsWith(HeadingTailBm, StringComparison.Ordinal)) return null;
            var note = s.Substring(HeadingBm.Length, s.Length - HeadingBm.Length - HeadingTailBm.Length).Trim();
            if (note.Length == 0) return HeadingEn + HeadingTailEn;
            var noteEn = ContextEn(note);
            return noteEn == null ? null : HeadingEn + noteEn + " " + HeadingTailEn;
        }

        // The base-point (grid) checker's remarks (AuditCheckers.BasePoint).
        private const string GridRuleBm = "Project Base Point berada <=10 mm dari persilangan grid 'A' dan '1'";
        private const string GridRuleEn = "the Project Base Point to be within 10 mm of the intersection of grids 'A' and '1'";
        private const string GridRuleStatementEn = "the Project Base Point is within 10 mm of the intersection of grids 'A' and '1'";
        private static readonly Regex GridModelHas = R(@"^Model ada (\d+) grid( \([^)]*\))? tetapi tiada yang bernama (.+?) \(atau berawalan projek, cth 'PT-A' / 'PT1'\)\. ");
        private static readonly Regex GridCurved = R(@"^Grid (.+?) bukan garisan lurus, jadi persilangan tidak boleh dihitung\. ");
        private static readonly Regex GridParallel = R(@"^Grid '(.*)' dan grid '(.*)' selari pada pelan, jadi tiada persilangan untuk diukur — semak manual\.$");

        /// <summary>The grid checker's "not verifiable" remarks in English; null when the
        /// text is not one of them (better all BM than half translated).</summary>
        private static string GridRemarkEn(string s)
        {
            var par = GridParallel.Match(s);
            if (par.Success)
                return $"Grid '{par.Groups[1].Value}' and grid '{par.Groups[2].Value}' are parallel in plan, so there is no intersection to measure — semak manual.";

            const string head = "Semakan perlukan ";
            const string tail = "Semak manual.";
            if (!s.StartsWith(head + GridRuleBm + ". ", StringComparison.Ordinal) || !s.EndsWith(tail, StringComparison.Ordinal)) return null;
            var rest = s.Substring(head.Length + GridRuleBm.Length + 2);
            var en = new System.Text.StringBuilder("The check needs " + GridRuleEn + ". ");
            const string noPbp = "Project Base Point tidak dijumpai. ";
            if (rest.StartsWith(noPbp, StringComparison.Ordinal)) { en.Append("The Project Base Point was not found. "); rest = rest.Substring(noPbp.Length); }
            var m = GridModelHas.Match(rest);
            if (m.Success)
            {
                var n = m.Groups[1].Value;
                en.Append("The model has " + n + (n == "1" ? " grid" : " grids") + m.Groups[2].Value
                          + " but none is named " + m.Groups[3].Value.Replace(" atau ", " or ")
                          + " (or prefixed with the project, e.g. 'PT-A' / 'PT1'). ");
                rest = rest.Substring(m.Length);
            }
            var c = GridCurved.Match(rest);
            if (c.Success)
            {
                en.Append("Grid " + c.Groups[1].Value.Replace(" dan ", " and ") + " is not a straight line, so the intersection cannot be calculated. ");
                rest = rest.Substring(c.Length);
            }
            return rest == tail ? en + tail : null;
        }

        /// <summary>"Konteks model: 0 workset; 3 aras (Aras 01, …)." in English; null when a
        /// label is not known (better all BM than half translated).</summary>
        private static string ContextEn(string note)
        {
            if (!note.StartsWith(ContextBm, StringComparison.Ordinal) || !note.EndsWith(".", StringComparison.Ordinal)) return null;
            var parts = note.Substring(ContextBm.Length, note.Length - ContextBm.Length - 1).Split(new[] { "; " }, StringSplitOptions.None);
            var en = new List<string>();
            foreach (var part in parts)
            {
                var sp = part.IndexOf(' ');
                if (sp <= 0 || !int.TryParse(part.Substring(0, sp), out _)) return null;
                var rest = part.Substring(sp + 1);
                string done = null;
                foreach (var (ms, e) in ContextLabels)
                    if (rest == ms || rest.StartsWith(ms + " (", StringComparison.Ordinal))
                    {
                        done = part.Substring(0, sp + 1) + e + rest.Substring(ms.Length);
                        break;
                    }
                if (done == null) return null;
                en.Add(done);
            }
            return "Model context: " + string.Join("; ", en) + ".";
        }

        public static string ExportNeedsScan(string lang = null) =>
            L(lang, "Jalankan audit dahulu untuk mengeksport Borang.", "Run the audit first to export the Borang.");

        // ── test mode ──
        public static string FixtureUnusable(string path, string message, string lang = null) =>
            L(lang, "Fail fixture tidak dapat digunakan:", "Fixture could not be used:") + "\n" + path + "\n\n" + message;
        public static string FixtureWritten(int n, string path, string lang = null) =>
            L(lang, $"Fixture ujian ditulis ({n} semakan): {path}", $"Test fixture written ({n} checks): {path}");

        // ── element labels from the local BIM010 rows ──
        /// <summary>"Element" / "Model" / "3 elements" (IssueMapper) in the pane's language.</summary>
        public static string ElementName(string name, string lang = null)
        {
            if (name == "Element") return L(lang, "Elemen", "Element");
            if (name == "Model") return "Model";
            var m = Regex.Match(name ?? "", @"^(\d+) elements$");
            if (m.Success) return L(lang, m.Groups[1].Value + " elemen", name);
            return name;
        }

        // ── fix-failure reasons ──
        // The handler and the fix applicator record their reason in English (kept
        // as is in the audit file and the fix blocklist, and matched by the
        // how-to-fix steps). It is put in the pane's language when shown. A reason
        // Revit gives itself (an exception message) is shown as Revit wrote it.
        private static readonly List<(Regex Re, string Ms, string En)> Reasons = new List<(Regex, string, string)>
        {
            (R(@"^Revit did not apply this change\.$"), "Revit tidak menggunakan perubahan ini.", null),
            (R(@"^Revit did not accept this change \((.*)\)\.$"), "Revit tidak menerima perubahan ini ({1}).", null),
            (R(@"^Revit did not accept this change: (.*)$"), "Revit tidak menerima perubahan ini: {1}", null),
            (R(@"^Rename target is empty\.?$"), "Nama baharu kosong.", null),
            (R(@"^No new name provided$"), "Tiada nama baharu diberi.", null),
            (R(@"^No parameter name provided$"), "Tiada nama parameter diberi.", null),
            (R(@"^Element (\d+) was not found in the model\.$"), "Elemen {1} tiada dalam model.", null),
            (R(@"^Element (\d+) not found$"), "Elemen {1} tiada dalam model.", null),
            (R(@"^Type not found for element (\d+)$"), "Jenis bagi elemen {1} tidak ditemui.", null),
            (R(@"^Unknown fix action: (.*)$"), "Tindakan pembaikan tidak dikenali: {1}", null),
            (R(@"^Rename failed: (.*)$"), "Tukar nama gagal: {1}", null),
            (R(@"^Set parameter failed: (.*)$"), "Gagal menetapkan parameter: {1}", null),
            (R(@"^Error: (.*)$"), "Ralat: {1}", null),
            (R(@"^Parameter '(.*)' is read-only$"), "Parameter '{1}' hanya-baca (read-only).", null),
            (R(@"^Parameter '(.*)' was bound but still not visible on element (\d+)\.$"),
                "Parameter '{1}' telah diikat tetapi masih tidak kelihatan pada elemen {2}.", null),
            (R(@"^Parameter '(.*)' not found on element (\d+)\. ?(.*)$"),
                "Parameter '{1}' tiada pada elemen {2}. {3}", null),
            (R(@"^Element (\d+) is inside a model group.*$"),
                "Elemen {1} berada dalam kumpulan model (model group) — Revit tidak membenarkan parameter instance diubah di luar mod edit kumpulan. Edit kumpulan itu (atau leraikan) dan jalankan Baiki semua semula.", null),
            (R(@"^Unsupported storage type for '(.*)'$"), "Jenis simpanan untuk '{1}' tidak disokong.", null),
            (R(@"^Failed to set '(.*)'$"), "Gagal menetapkan '{1}'.", null),
        };

        private static Regex R(string pattern) => new Regex(pattern, RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>A recorded fix-failure reason in the pane's language.</summary>
        public static string FixReason(string reason, string lang = null)
        {
            if (string.IsNullOrEmpty(reason) || En(lang)) return reason ?? "";
            foreach (var (re, ms, _) in Reasons)
            {
                var m = re.Match(reason);
                if (!m.Success) continue;
                var s = ms;
                for (var g = 1; g < m.Groups.Count; g++) s = s.Replace("{" + g + "}", m.Groups[g].Value);
                return s.Trim();
            }
            return reason;
        }
    }
}
