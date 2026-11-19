using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ClosedXML.Excel;
using Microsoft.Win32;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    public partial class ExportWindow : Window
    {
        private readonly PanelVm _vm;
        private IssueExportFormat _format;

        /// <param name="format">Which format starts selected (the Borang preview's
        /// Excel / CSV entries open this with theirs).</param>
        public ExportWindow(PanelVm vm, IssueExportFormat format = IssueExportFormat.Excel)
        {
            InitializeComponent();
            _vm = vm;
            string L(string ms, string en) => PaneLanguage.Pick(ms, en);
            TitleText.Text = PaneText.ExportIssues();
            Title = TitleText.Text;
            SubText.Text = PaneText.ExportDialogHint();
            FormatLabel.Text = PaneText.ExportFormat();
            IncludeLabel.Text = PaneText.ExportInclude();
            ExcelOpt.Content = PaneText.FormatExcel();
            CsvOpt.Content = PaneText.FormatCsv();
            CancelBtn.Content = PaneText.Cancel();
            ExportBtn.Content = PaneText.ExportAction();
            CloseBtn.ToolTip = L("Tutup", "Close");
            System.Windows.Automation.AutomationProperties.SetName(CloseBtn, L("Tutup", "Close"));
            CbOpen.Content = L($"Terbuka ({_vm.OpenCount})", $"Open ({_vm.OpenCount})");
            CbResolved.Content = L($"Selesai ({_vm.ResolvedCount})", $"Resolved ({_vm.ResolvedCount})");
            SetFormat(format);
            UpdateCount();
            // Esc, x or a click on the backdrop closes without exporting.
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        }

        private void SetFormat(IssueExportFormat f)
        {
            _format = f;
            ExcelOpt.IsChecked = f == IssueExportFormat.Excel;
            CsvOpt.IsChecked = f == IssueExportFormat.Csv;
        }

        private void UpdateCount() => CountText.Text = PaneText.ExportWillWrite(FilteredRows().Length);

        private void Scrim_Click(object s, MouseButtonEventArgs e) { if (e.Source == s) Close(); }
        private void Close_Click(object s, RoutedEventArgs e) => Close();
        private void Card_Click(object s, MouseButtonEventArgs e) { e.Handled = true; }
        private void Excel_Select(object s, RoutedEventArgs e) => SetFormat(IssueExportFormat.Excel);
        private void Csv_Select(object s, RoutedEventArgs e) => SetFormat(IssueExportFormat.Csv);
        private void Options_Changed(object s, RoutedEventArgs e) => UpdateCount();
        private void Export_Click(object s, RoutedEventArgs e)
        {
            if (_format == IssueExportFormat.Csv) WriteCsv(); else WriteXlsx();
        }

        private IssueVm[] FilteredRows() =>
            IssueExportRows.Select(_vm.Issues, CbOpen.IsChecked == true, CbResolved.IsChecked == true);

        private static string[] BuildHeader() => IssueExportRows.Header;
        private static string[] RowCells(IssueVm i) => IssueExportRows.Cells(i);

        // ── CSV ──────────────────────────────────────────

        private void WriteCsv()
        {
            var dlg = new SaveFileDialog
            {
                Title = PaneLanguage.Pick("Eksport JKR Compliance", "Export JKR Compliance"),
                FileName = $"jkr_compliance_report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".csv",
                Filter = PaneLanguage.Pick("Fail CSV (*.csv)|*.csv", "CSV files (*.csv)|*.csv"),
            };
            if (dlg.ShowDialog() != true) return;

            var rows = FilteredRows();
            var header = BuildHeader();
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", header.Select(CsvCell)));

            foreach (var i in rows)
                sb.AppendLine(string.Join(",", RowCells(i).Select(CsvCell)));

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            Close();
        }

        // ── Excel (ClosedXML) ────────────────────────────

        private void WriteXlsx()
        {
            var dlg = new SaveFileDialog
            {
                Title = PaneLanguage.Pick("Eksport JKR Compliance", "Export JKR Compliance"),
                FileName = $"jkr_compliance_report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".xlsx",
                Filter = PaneLanguage.Pick("Fail Excel (*.xlsx)|*.xlsx", "Excel files (*.xlsx)|*.xlsx"),
            };
            if (dlg.ShowDialog() != true) return;

            var rows = FilteredRows();
            var header = BuildHeader();

            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("JKR Compliance");

                // Header row
                for (int c = 0; c < header.Length; c++)
                {
                    var cell = ws.Cell(1, c + 1);
                    cell.Value = header[c];
                    cell.Style.Font.Bold = true;
                }

                // Data rows
                int row = 2;
                foreach (var i in rows)
                {
                    var cells = RowCells(i);
                    for (int c = 0; c < cells.Length; c++)
                        ws.Cell(row, c + 1).Value = cells[c];
                    row++;
                }

                ws.Columns().AdjustToContents();
                wb.SaveAs(dlg.FileName);
            }

            Close();
        }

        // ── Helpers ──────────────────────────────────────

        private static string CsvCell(string s) => IssueExportRows.CsvCell(s);
    }
}
