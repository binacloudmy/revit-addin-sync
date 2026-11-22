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
        public static string FixStillFails(string lang = null) =>
            L(lang, "Pembaikan ditulis, tetapi semakan masih gagal. Baris kekal Terbuka.",
                    "The fix was written, but the check still fails. The row stays Open.");
        public static string FixNotRechecked(string lang = null) =>
            L(lang, "Nilai ditulis, tetapi semakan tidak dapat dijalankan. Baris kekal Terbuka; imbas semula untuk mengesahkan.",
                    "The value was written, but the check could not run. The row stays Open; re-scan to confirm.");
        public static string AiAsking(string lang = null) =>
            L(lang, "AI sedang mencari cadangan…", "AI looking for a suggestion…");
        public static string AiNothing(string lang = null) =>
            L(lang, "Pembaiki AI tiada cadangan untuk item ini. Anda boleh masukkan nilai sendiri.",
                    "The AI fixer has no suggestion for this item. You can enter the value yourself.");
        public static string ManualRefused(string reason, string lang = null) =>
            L(lang, "Nilai tidak ditulis: ", "Value not written: ") + reason;
        public static string FixUndone(int n, string lang = null) =>
            L(lang, $"{n} pembaikan dibatalkan — semakan gagal semula, baris dibuka semula.",
                    $"{n} fix{(n == 1 ? "" : "es")} undone — the check fails again, so the row is open again.");
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

        /// <summary>AI-495: the status of a row fixed through Fix via Copilot.</summary>
        public static string FixedViaCopilotRow(string lang = null) =>
            L(lang, "Dibaiki melalui Copilot", "Fixed via Copilot");

        /// <summary>AI-479: the row's mark for a fixable family with no spec id (outside the %).</summary>
        public static string ExtraTag(string lang = null) =>
            L(lang, "Tambahan · di luar %", "Extra · outside the %");

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
        public static string IgnoreAllConfirm(int n, string lang = null) =>
            L(lang, $"Abaikan {n} isu yang dipaparkan? Isu Tinggi tidak diabaikan.", $"Ignore the {n} issue{S(n)} shown? High issues are never ignored.");
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
            return LegacyRuleRemarkEn(s);
        }

        // Remarks of the legacy checkers whose Borang row no spec check answers
        // (SpecReport.MapAudit gives null: views_template, area_plans, schedules_required), plus
        // the "nothing to check" and "checker failed" remarks. They are the ones that still reach
        // the Issues list once a spec verdict replaces the rest (AI-469). Written to match
        // AuditCheckers.cs word for word; anything that does not match stays BM.
        private static readonly Regex AreaPlansNo = new Regex(
            @"^Peraturan: sekurang-kurangnya satu Area Plan untuk analisis ruang/zon\. Dijumpai 0 Area Plan dalam model — jana analisis ruang/zon daripada Area Plan\.$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SchedulesNo = new Regex(
            @"^Peraturan: nama jadual mengandungi \(termasuk awalan JKR\): (?<tokens>.+) / jadual komponen \((?<comp>[^)]*)\)\. Tiada nama jadual yang sepadan untuk: (?<missing>.+) \((?<n>\d+) jadual dalam model disemak\)\. Jana jadual tersebut\.$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private const string ViewTemplateRuleEn = "every graphical view has a View Template";

        private static readonly Regex ViewsTemplateNo = new Regex(
            @"^Peraturan: setiap view grafik ada View Template\. (?<w>\d+)/(?<v>\d+) view tiada template \(cth: (?<names>.*?)\)\. Sapukan template daripada templat seni bina\."
            + @"(?: (?<act>\d+) boleh tindakan, (?<un>\d+) tiada template jenis tersebut wujud dalam model \((?<types>[^)]*)\) — tidak boleh disapukan sehingga template jenis itu diwujudkan\.)?"
            + @"(?: Senarai penuh (?<total>\d+) item ada dalam draft_export \(element_ids\)\.)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ViewsTemplateUnactionable = new Regex(
            @"^Peraturan: setiap view grafik ada View Template\. (?<ok>\d+)/(?<v>\d+) view ada template; (?<w>\d+) view tiada template tetapi tiada template jenis tersebut wujud dalam model \((?<types>[^;]*); cth: (?<names>.*?)\) — tidak boleh tindakan\. Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula\.$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NothingViews = new Regex(
            @"^Tiada view grafik dalam model, jadi peraturan ""setiap view grafik ada View Template"" tiada data untuk disemak \(0 diperiksa\) — semak manual\.$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex CheckerCrash = new Regex(
            @"^Semakan automatik gagal \((?<id>[a-z_]+)\) — semak manual\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string LegacyRuleRemarkEn(string s)
        {
            if (AreaPlansNo.IsMatch(s))
                return "Rule: at least one Area Plan for space / zone analysis. Found 0 Area Plans in the model — generate the space / zone analysis from an Area Plan.";
            var m = SchedulesNo.Match(s);
            if (m.Success)
                return "Rule: a schedule name contains (including the JKR prefix): " + m.Groups["tokens"].Value + " / a component schedule (" + m.Groups["comp"].Value
                    + "). No schedule name matches: " + m.Groups["missing"].Value + " (" + m.Groups["n"].Value + " schedules in the model checked). Generate those schedules.";
            m = ViewsTemplateNo.Match(s);
            if (m.Success)
            {
                var en = "Rule: " + ViewTemplateRuleEn + ". " + m.Groups["w"].Value + "/" + m.Groups["v"].Value + " views have no template (e.g. " + m.Groups["names"].Value
                    + "). Apply the template from the architectural template.";
                if (m.Groups["act"].Success)
                    en += " " + m.Groups["act"].Value + " can be fixed, " + m.Groups["un"].Value + " have no template of their view type in the model (" + m.Groups["types"].Value
                        + ") — they cannot be applied until a template of that type exists.";
                if (m.Groups["total"].Success)
                    en += " The full list of " + m.Groups["total"].Value + " items is in draft_export (element_ids).";
                return en;
            }
            m = ViewsTemplateUnactionable.Match(s);
            if (m.Success)
                return "Rule: " + ViewTemplateRuleEn + ". " + m.Groups["ok"].Value + "/" + m.Groups["v"].Value + " views have a template; " + m.Groups["w"].Value
                    + " have none, and no template of their view type exists in the model (" + m.Groups["types"].Value + "; e.g. " + m.Groups["names"].Value
                    + ") — nothing can be done yet. Create a template of that type first if wanted, then check again.";
            if (NothingViews.IsMatch(s))
                return "There are no graphical views in the model, so the rule \"" + ViewTemplateRuleEn + "\" has nothing to check (0 checked) — semak manual.";
            m = CheckerCrash.Match(s);
            if (m.Success)
                return "The automatic check failed (" + m.Groups["id"].Value + ") — semak manual.";
            return null;
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
