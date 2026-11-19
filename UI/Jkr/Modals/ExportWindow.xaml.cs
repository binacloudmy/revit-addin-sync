using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ClosedXML.Excel;
using Microsoft.Win32;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    public partial class ExportWindow : Window
    {
        private readonly PanelVm _vm;

        public ExportWindow(PanelVm vm)
        {
            InitializeComponent();
            _vm = vm;
            // In the pane's language (BM / EN). Replaced by the Borang export in step 14.
            string L(string ms, string en) => RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Pick(ms, en);
            TitleText.Text = L("Eksport laporan", "Export Report");
            SubText.Text = L("Muat turun keputusan pematuhan sebagai CSV atau Excel.", "Download compliance results as CSV or Excel.");
            CsvBtn.Content = L("Muat turun CSV", "Download CSV");
            CloseBtn.ToolTip = L("Tutup", "Close");
            System.Windows.Automation.AutomationProperties.SetName(CloseBtn, L("Tutup", "Close"));
            Title = TitleText.Text;
            // Esc, × or a click on the backdrop closes without exporting.
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
            CbOpen.Content = L($"Terbuka ({_vm.OpenCount})", $"Open ({_vm.OpenCount})");
            CbResolved.Content = L($"Selesai ({_vm.ResolvedCount})", $"Resolved ({_vm.ResolvedCount})");
        }

        private void Scrim_Click(object s, MouseButtonEventArgs e) { if (e.Source == s) Close(); }
        private void Close_Click(object s, RoutedEventArgs e) => Close();
        private void Card_Click(object s, MouseButtonEventArgs e) { e.Handled = true; }
        private void Csv_Click(object s, RoutedEventArgs e) => WriteCsv();
        private void Excel_Click(object s, RoutedEventArgs e) => WriteXlsx();

        private IssueVm[] FilteredRows()
        {
            return _vm.Issues
                .Where(i => (CbOpen.IsChecked == true && i.IsOpen) || (CbResolved.IsChecked == true && !i.IsOpen))
                .ToArray();
        }

        private string[] BuildHeader()
        {
            return new[] { "ID", "Title", "Category", "Priority", "Status", "Required", "Actual" };
        }

        private string[] RowCells(IssueVm i)
        {
            return new[]
            {
                // An item a person marked as reviewed exports as "Reviewed" (the pane's Manual tab).
                i.Id, i.Title, i.Category, i.PriorityLabel, i.NeedsPerson && i.Reviewed ? "Reviewed" : i.StatusLabel, i.Required, i.Actual
            };
        }

        // ── CSV ──────────────────────────────────────────

        private void WriteCsv()
        {
            var dlg = new SaveFileDialog
            {
                Title = RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Pick("Eksport JKR Compliance", "Export JKR Compliance"),
                FileName = $"jkr_compliance_report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".csv",
                Filter = RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Pick("Fail CSV (*.csv)|*.csv", "CSV files (*.csv)|*.csv"),
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
                Title = RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Pick("Eksport JKR Compliance", "Export JKR Compliance"),
                FileName = $"jkr_compliance_report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".xlsx",
                Filter = RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Pick("Fail Excel (*.xlsx)|*.xlsx", "Excel files (*.xlsx)|*.xlsx"),
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

        private static string CsvCell(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
