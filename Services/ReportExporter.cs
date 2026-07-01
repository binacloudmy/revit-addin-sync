using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.Services
{
    /// <summary>Output formats offered by the History "Download report" menu.</summary>
    public enum ReportFormat { Excel, Markdown, Text, Pdf }

    /// <summary>
    /// Exports a single Copilot session (a <see cref="HistoryEntry"/>) to a file.
    /// The report is transcript-level: header + the ordered user/bot messages and
    /// the tool names used per reply — exactly what the session persists. The rich
    /// result data (count bars, issue lists) is not stored in history, so it is not
    /// part of the report; persisting ResultModel would be a follow-up.
    /// </summary>
    public static class ReportExporter
    {
        private static readonly object _pdfInitLock = new object();
        private static bool _pdfReady;

        /// <summary>File-dialog filter + default extension for a format.</summary>
        public static (string filter, string ext) DialogInfo(ReportFormat fmt)
        {
            switch (fmt)
            {
                case ReportFormat.Excel:    return ("Excel Workbook (*.xlsx)|*.xlsx", ".xlsx");
                case ReportFormat.Markdown: return ("Markdown (*.md)|*.md", ".md");
                case ReportFormat.Text:     return ("Text File (*.txt)|*.txt", ".txt");
                case ReportFormat.Pdf:      return ("PDF Document (*.pdf)|*.pdf", ".pdf");
                default:                    return ("All Files (*.*)|*.*", "");
            }
        }

        /// <summary>Default filename (no extension) for a session, sanitized for the filesystem.</summary>
        public static string SuggestedFileName(HistoryEntry session)
        {
            string title = session?.Label ?? session?.Summary ?? "Session";
            var clean = new string(title.Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : ' ').ToArray()).Trim();
            if (clean.Length > 60) clean = clean.Substring(0, 60).Trim();
            if (clean.Length == 0) clean = "Session";
            return $"BINA Copilot Report - {clean} - {DateTime.Now:yyyy-MM-dd}";
        }

        /// <summary>Write <paramref name="session"/> to <paramref name="path"/> in the chosen format.</summary>
        public static void Export(HistoryEntry session, string modelName, ReportFormat fmt, string path)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            switch (fmt)
            {
                case ReportFormat.Excel:    ExportExcel(session, modelName, path); break;
                case ReportFormat.Markdown: File.WriteAllText(path, BuildMarkdown(session, modelName)); break;
                case ReportFormat.Text:     File.WriteAllText(path, BuildText(session, modelName)); break;
                case ReportFormat.Pdf:      ExportPdf(session, modelName, path); break;
                default: throw new ArgumentOutOfRangeException(nameof(fmt));
            }
        }

        // ─── Shared header fields ────────────────────────────────────────────
        private static string StatusLabel(string status) =>
            status == "warn" ? "Completed with warnings" : status == "undone" ? "Undone" : "Completed";

        private static int MessageCount(HistoryEntry s) =>
            s.History?.Count(m => m.Sender == "user") ?? 0;

        // ─── Excel ───────────────────────────────────────────────────────────
        private static void ExportExcel(HistoryEntry session, string modelName, string path)
        {
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Session");

                ws.Cell(1, 1).Value = "BINA AI Copilot Report";
                ws.Cell(1, 1).Style.Font.Bold = true;
                ws.Cell(1, 1).Style.Font.FontSize = 14;
                ws.Range(1, 1, 1, 4).Merge();

                ws.Cell(2, 1).Value =
                    $"Model: {modelName ?? "—"}    |    Session: {session.Label ?? session.Summary ?? "Run"}    |    " +
                    $"Date: {session.Time}    |    {StatusLabel(session.Status)}    |    {MessageCount(session)} message(s)";
                ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;
                ws.Range(2, 1, 2, 4).Merge();

                int headerRow = 4;
                var headers = new[] { "Time", "From", "Message", "Tools used" };
                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = ws.Cell(headerRow, i + 1);
                    cell.Value = headers[i];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#6d28d9");
                    cell.Style.Font.FontColor = XLColor.White;
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                }

                int row = headerRow + 1;
                foreach (var m in session.History ?? new List<History>())
                {
                    ws.Cell(row, 1).Value = m.Time ?? "";
                    ws.Cell(row, 2).Value = m.Sender == "user" ? "User" : "Copilot";
                    ws.Cell(row, 3).Value = m.Text ?? "";
                    ws.Cell(row, 3).Style.Alignment.WrapText = true;
                    ws.Cell(row, 4).Value = (m.Tools != null && m.Tools.Count > 0) ? string.Join(", ", m.Tools) : "";
                    if (m.Sender != "user")
                        ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#f8f7fc");
                    row++;
                }

                ws.Column(1).Width = 12;
                ws.Column(2).Width = 10;
                ws.Column(3).Width = 80;
                ws.Column(4).Width = 24;
                ws.SheetView.FreezeRows(headerRow);
                if (row > headerRow + 1)
                    ws.Range(headerRow, 1, row - 1, 4).SetAutoFilter();

                wb.SaveAs(path);
            }
        }

        // ─── Markdown ────────────────────────────────────────────────────────
        private static string BuildMarkdown(HistoryEntry session, string modelName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# BINA AI Copilot Report");
            sb.AppendLine();
            sb.AppendLine($"- **Model:** {modelName ?? "—"}");
            sb.AppendLine($"- **Session:** {session.Label ?? session.Summary ?? "Run"}");
            sb.AppendLine($"- **Date:** {session.Time}");
            sb.AppendLine($"- **Status:** {StatusLabel(session.Status)}");
            sb.AppendLine($"- **Messages:** {MessageCount(session)}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            foreach (var m in session.History ?? new List<History>())
            {
                string who = m.Sender == "user" ? "🧑 User" : "✨ Copilot";
                sb.AppendLine($"### {who} · {m.Time}");
                sb.AppendLine();
                sb.AppendLine(m.Text ?? "");
                sb.AppendLine();
                if (m.Tools != null && m.Tools.Count > 0)
                {
                    sb.AppendLine($"_Tools used: {string.Join(", ", m.Tools)}_");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        // ─── Plain text ──────────────────────────────────────────────────────
        private static string BuildText(HistoryEntry session, string modelName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("BINA REVIT COPILOT REPORT");
            sb.AppendLine("=========================");
            sb.AppendLine($"Model:    {modelName ?? "—"}");
            sb.AppendLine($"Session:  {session.Label ?? session.Summary ?? "Run"}");
            sb.AppendLine($"Date:     {session.Time}");
            sb.AppendLine($"Status:   {StatusLabel(session.Status)}");
            sb.AppendLine($"Messages: {MessageCount(session)}");
            sb.AppendLine();
            foreach (var m in session.History ?? new List<History>())
            {
                string who = m.Sender == "user" ? "USER" : "COPILOT";
                sb.AppendLine($"[{m.Time}] {who}");
                sb.AppendLine(m.Text ?? "");
                if (m.Tools != null && m.Tools.Count > 0)
                    sb.AppendLine($"  (tools: {string.Join(", ", m.Tools)})");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ─── PDF (QuestPDF) ──────────────────────────────────────────────────

        // AddDllDirectory + SetDefaultDllDirectories let us extend the native DLL
        // search path so QuestPdfSkia.dll's dependents resolve — see EnsureQuestPdfReady.
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AddDllDirectory(string newDirectory);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(uint directoryFlags);

        private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

        /// <summary>
        /// One-time QuestPDF init: put the bundled native folder on the DLL search
        /// path and set the Community license, before any QuestPDF type is touched.
        ///
        /// QuestPDF's native engine <c>QuestPdfSkia.dll</c> (under
        /// runtimes/&lt;rid&gt;/native next to this assembly) is MinGW-built and
        /// depends on <c>libgcc_s_seh-1.dll</c>, <c>libstdc++-6.dll</c> and
        /// <c>libwinpthread-1.dll</c> shipped in the same folder. QuestPDF loads
        /// QuestPdfSkia.dll by explicit path, but Windows then resolves *its*
        /// dependents against the host process dir (Revit.exe) + System32 + PATH —
        /// never that native folder — so in the Revit host the load fails and
        /// QuestPDF.Settings' static ctor throws a TypeInitializationException.
        /// Adding the native dir to the search path fixes the dependent resolution.
        /// </summary>
        private static void EnsureQuestPdfReady()
        {
            if (_pdfReady) return;
            lock (_pdfInitLock)
            {
                if (_pdfReady) return;

                var asmDir = Path.GetDirectoryName(typeof(ReportExporter).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    var rid = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                    var nativeDir = Path.Combine(asmDir, "runtimes", rid, "native");
                    var nativeDll = Path.Combine(nativeDir, "QuestPdfSkia.dll");

                    if (!File.Exists(nativeDll))
                        throw new FileNotFoundException(
                            "The PDF engine's native library is missing from this build:\n" +
                            nativeDll + "\n\n" +
                            "The add-in was deployed without its 'runtimes' folder. Reinstall or " +
                            "rebuild so the native PDF engine ships alongside the plugin.");

                    // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS makes AddDllDirectory entries
                    // part of dependent-DLL resolution for subsequent LoadLibrary calls.
                    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                    AddDllDirectory(nativeDir);
                }

                try
                {
                    // QuestPDF Community license — free for orgs under the revenue threshold.
                    QuestPDF.Settings.License = LicenseType.Community;
                }
                catch (Exception ex)
                {
                    // The static ctor fired here; surface the underlying native cause.
                    var root = ex;
                    while (root.InnerException != null) root = root.InnerException;
                    throw new InvalidOperationException(
                        "The PDF engine (QuestPDF/Skia) failed to initialize: " + root.Message, ex);
                }

                _pdfReady = true;
            }
        }

        private static void ExportPdf(HistoryEntry session, string modelName, string path)
        {
            EnsureQuestPdfReady();

            var messages = session.History ?? new List<History>();

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(36);
                    page.Size(PageSizes.A4);
                    page.DefaultTextStyle(t => t.FontSize(10).FontColor("#111827"));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("BINA AI Copilot Report")
                            .FontSize(16).Bold().FontColor("#5b21b6");
                        col.Item().PaddingTop(2).Text(
                            $"Model: {modelName ?? "—"}   |   {session.Label ?? session.Summary ?? "Run"}   |   " +
                            $"{session.Time}   |   {StatusLabel(session.Status)}   |   {MessageCount(session)} message(s)")
                            .FontSize(9).FontColor("#6b7280");
                        col.Item().PaddingTop(6).LineHorizontal(1).LineColor("#e5e7eb");
                    });

                    page.Content().PaddingVertical(8).Column(col =>
                    {
                        col.Spacing(8);
                        foreach (var m in messages)
                        {
                            bool isUser = m.Sender == "user";
                            col.Item()
                               .Background(isUser ? "#f3f4f6" : "#faf5ff")
                               .Border(1).BorderColor(isUser ? "#e5e7eb" : "#ddd6fe")
                               .Padding(8)
                               .Column(c =>
                               {
                                   c.Item().Text($"{(isUser ? "User" : "Copilot")} · {m.Time}")
                                       .FontSize(8.5f).Bold().FontColor(isUser ? "#374151" : "#5b21b6");
                                   c.Item().PaddingTop(2).Text(m.Text ?? "");
                                   if (m.Tools != null && m.Tools.Count > 0)
                                       c.Item().PaddingTop(3).Text($"Tools used: {string.Join(", ", m.Tools)}")
                                           .FontSize(8).Italic().FontColor("#7c3aed");
                               });
                        }
                    });

                    page.Footer().AlignRight().Text(t =>
                    {
                        t.Span("Generated by BINA AI Copilot · ").FontSize(8).FontColor("#9ca3af");
                        t.Span($"{DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(8).FontColor("#9ca3af");
                    });
                });
            }).GeneratePdf(path);
        }
    }
}
