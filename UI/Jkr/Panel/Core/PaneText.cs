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
        public static string FixRedone(int n) =>
            ($"Fix redone — {n} row{(n == 1 ? "" : "s")} pass the check again.");
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

        // ── the BIM010 checker's remarks (fill_audit writes them in English since AI-513) ──

        /// <summary>A remark of the BIM010 checker. The checkers write English, so it shows as written.</summary>
        public static Text AuditRemark(string remark) => Text.Same(remark ?? "");

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
