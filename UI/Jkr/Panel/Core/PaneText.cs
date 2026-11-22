using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// English text the add-in writes itself, outside the screen strings
    /// (<see cref="PanelStrings"/>): toasts, busy labels, dialogs, Locate
    /// messages, how-to-fix steps and fix-failure reasons. Text from the checker
    /// or the backend (issue titles, rules, remarks) is shown as it arrives.
    /// </summary>
    public static class PaneText
    {
        private static string S(int n) => n == 1 ? "" : "s";

        public const string DialogTitle = "BINA JKR Compliance";

        // ── fix / ignore ──
        public static string NoAutoFixHere() =>
            "No auto-fixable issues here.";
        public static string NoAutoFixFound() =>
            "No auto-fixable issues found.";
        public static string FixRunning() =>
            "A fix is already running — please wait.";
        public static string AutoFixUnavailable() =>
            "Auto-fix unavailable — the fix handler is not ready. Restart Revit and try again.";
        public static string NoFixData() =>
            "No machine-readable fix attached to this issue.";
        public static string RenameTargetEmpty() =>
            "Rename target is empty.";
        public static string FixAllError(string message) =>
            "Fix all error:" + "\n\n" + message;
        public static string AutoFixError(string message) =>
            "Auto-fix error:" + "\n\n" + message;
        public static string AiAnalysing(int n) =>
            $"AI analysing {n} issue{S(n)}…";
        public static string AiUnavailable() =>
            "AI fixes unavailable — applying standard fixes only.";
        public static string Applying(int n) =>
            $"Applying {n} change{S(n)}…";
        public static string AppliedRescanning(int n) =>
            ($"Applied {n} fix{(n == 1 ? "" : "es")}. Re-scanning…");
        public static string Rescanning() =>
            "Re-scanning model…";
        public static string RevitBusy() =>
            "Revit is busy. Close any open dialog or command and try again.";
        public static string NothingWritten() =>
            "No fixes applied. Nothing was written to the model.";
        public static string FixFailedNothingWritten() =>
            "Fix failed. Nothing was written to the model.";
        public static string FixStillFails() =>
            "The fix was written, but the check still fails. The row stays Open.";
        public static string FixNotRechecked() =>
            "The value was written, but the check could not run. The row stays Open; re-scan to confirm.";
        public static string AiAsking() =>
            "AI looking for a suggestion…";
        public static string AiNothing() =>
            "The AI fixer has no suggestion for this item. You can enter the value yourself.";
        public static string ManualRefused(string reason) =>
            "Value not written: " + reason;
        public static string FixUndone(int n) =>
            ($"{n} fix{(n == 1 ? "" : "es")} undone — the check fails again, so the row is open again.");
        public static string NeedsPerson() =>
            "This item needs a person to review it — the AI did not judge it.";

        /// <summary>prototype toastFix: "3 change(s) written to model"; with failures
        /// "· 1 failed, still Open" (the failed list follows in a dialog).</summary>
        public static string ChangesWritten(int written, int failed = 0)
        {
            var msg = written + " " + PanelStrings.Get("toastFix");
            if (failed > 0) msg += $" · {failed} failed, still Open";
            return msg;
        }

        /// <summary>AI-495: the status of a row fixed through Fix via Copilot.</summary>
        public static string FixedViaCopilotRow() =>
            "Fixed via Copilot";

        /// <summary>AI-479: the row's mark for a fixable family with no spec id (outside the %).</summary>
        public static string ExtraTag() =>
            "Extra · outside the %";

        /// <summary>prototype toastLoc: "Selected in model: Doors [204511] PT2p600a".</summary>
        public static string SelectedInModel(string element) =>
            PanelStrings.Get("toastLoc") + ": " + element;

        /// <summary>Toast time: 2.6 s, longer for a long notice so it can be read
        /// (about 60 ms a character, at most 8 s).</summary>
        public static TimeSpan ToastTime(string message)
        {
            var reading = (message ?? "").Length * 60;
            return TimeSpan.FromMilliseconds(Math.Max(2600, Math.Min(8000, reading)));
        }

        public static string RescanAfterFixFailed(string message) =>
            "Re-scan after fix failed:" + "\n\n" + message;

        /// <summary>The dialog listing the fixes of a batch that failed.</summary>
        public static string FailedFixes(int applied, int failed, string details)
        {
            var head = applied > 0
                ? ($"{applied} change{S(applied)} applied as one undo step. {failed} fix{(failed == 1 ? "" : "es")} failed and {(failed == 1 ? "was" : "were")} not written:")
                : ($"{failed} fix{(failed == 1 ? "" : "es")} failed. Nothing was written to the model:");
            return head + "\n\n" + details + "\n\n" +
                   "These issues stay Open, with the reason shown on each row.";
        }

        /// <summary>One line of that list: what was changed.</summary>
        public static string RenameWhat(long elementId, string newName) =>
            $"Rename {elementId} → '{newName}'";
        public static string ParamWhat(string parameter, long elementId) =>
            $"{parameter} on {elementId}";
        public static string AndMore(int n) =>
            $"…and {n} more";

        public static string IssuesIgnored(int n) =>
            n + " " + PanelStrings.Get("toastIgn");
        public static string NoMedLowToIgnore() =>
            "No Medium/Low issues to ignore.";
        public static string IgnoreAllConfirm(int n, int skipped = 0) =>
            $"Ignore the {n} issue{S(n)} shown?"
            + (skipped > 0 ? $" {skipped} High or marked-manual row{S(skipped)} will not be ignored." : "");
        public static string IgnoredMedLow(int n) =>
            $"Ignored {n} Medium/Low issue{S(n)}.";

        /// <summary>Toast after one decision: "Ignored · Door width…".</summary>
        public static string Decision(string status, string title)
        {
            string label;
            switch (status)
            {
                case "Fixed": label = "Fixed"; break;
                case "Ignored": label = "Ignored"; break;
                case "Approved": label = "Approved"; break;
                case "ManualFixNeeded": label = "Marked manual"; break;
                default: label = "Reopened"; break;
            }
            return label + " · " + title;
        }

        public static string WrongModel(string scannedFile) =>
            $"These fixes are for {scannedFile}. Make that model active (or run the audit on this one) and try again. Nothing was written.";

        // ── scan / reset ──
        public static string NoDocument() =>
            "No active Revit document. Open a model first.";
        public static string ScanError(string message) =>
            "Scan error:" + "\n\n" + message;
        public static string ScanFailed(string message) =>
            "Scan failed:" + "\n\n" + message;
        public static string WaitThenReset() =>
            "Please wait until the scan or fix finishes, then Reset.";
        public static string ResetDone() =>
            "Reset: results and decisions cleared. Nothing in the model was changed. Run the audit again to start over.";
        public static string LocateFailed(string message) =>
            "Couldn't locate element in view — " + message;
        public static string DetailFailed(string message) =>
            "Failed to open issue detail:" + "\n\n" + message;

        // ── export ──
        public static string ExportIssues() =>
            "Export issues";
        public static string BorangPreviewTitle() =>
            "Borang BIM010 preview";
        public static string BorangPreviewNote() =>
            "Filled from the HOD’s BIM010 template, sections A to E, unchanged";
        public static string PageOf(int page, int pages) =>
            $"Page {page} of {pages}";
        public static string PreviousPage() => "Previous page";
        public static string NextPage() => "Next page";
        public static string ZoomHint() => "Ctrl + scroll to zoom";
        public static string SavePdf() => "Save PDF";
        public static string FormatExcel() => "Excel (.xlsx)";
        public static string FormatCsv() => "CSV (.csv)";
        public static string IssueCount(int n) =>
            $"{n} issue{S(n)}";
        public static string ExportDialogHint() =>
            "Choose the format and which issues go in the file.";
        public static string ExportFormat() => "Format";
        public static string ExportInclude() => "Include";
        public static string ExportWillWrite(int n) =>
            $"{n} issue{S(n)} will be written";
        public static string ExportAction() => "Export";
        public static string Cancel() => "Cancel";
        public static string PdfSaved(string path) =>
            "PDF saved: " + path;
        public static string PdfSaveFailed(string message) =>
            "PDF could not be saved: " + message;
        public static string ExportFailed(string message) =>
            "The Borang could not be generated: " + message;
        /// <summary>The Excel / CSV issue export wrote its file (the name, not the folder).</summary>
        public static string IssuesExported(string path) =>
            "Issues exported: " + System.IO.Path.GetFileName(path ?? "");
        public static string IssuesExportFailed(string message) =>
            "Issues could not be exported: " + message;

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

        public static string ExportNeedsScan() =>
            "Run the audit first to export the Borang.";

        // ── test mode ──
        public static string FixtureUnusable(string path, string message) =>
            "Fixture could not be used:" + "\n" + path + "\n\n" + message;
        public static string FixtureWritten(int n, string path) =>
            $"Test fixture written ({n} checks): {path}";

        // ── element labels from the local BIM010 rows ──
    }
}
