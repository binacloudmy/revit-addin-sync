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
        private readonly JkrComplianceService _svc;

        public ExportWindow(PanelVm vm, JkrComplianceService svc = null)
        {
            InitializeComponent();
            _vm = vm;
            _svc = svc;
            CbOpen.Content = $"Open ({_vm.OpenCount})";
            CbResolved.Content = $"Resolved ({_vm.ResolvedCount})";
        }

        private void Scrim_Click(object s, MouseButtonEventArgs e) { if (e.Source == s) Close(); }
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
            var header = new[] { "ID", "Title", "Category", "Priority", "Status", "Required", "Actual" };
            if (CbSpec.IsChecked == true)
                header = header.Concat(new[] { "Spec", "Clause", "Page" }).ToArray();
            return header;
        }

        private string[] RowCells(IssueVm i)
        {
            var cells = new System.Collections.Generic.List<string>
            {
                i.Id, i.Title, i.Category, i.PriorityLabel, i.StatusLabel, i.Required, i.Actual
            };
            if (CbSpec.IsChecked == true)
            {
                var spec = SpecDoc.Get(i.Spec.Doc);
                cells.Add(spec.Short);
                cells.Add(i.Spec.Clause);
                cells.Add(i.Spec.Page.ToString());
            }
            return cells.ToArray();
        }

        // ── CSV ──────────────────────────────────────────

        private void WriteCsv()
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export JKR Compliance",
                FileName = $"jkr_compliance_report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".csv",
                Filter = "CSV files (*.csv)|*.csv",
            };
            if (dlg.ShowDialog() != true) return;

            var rows = FilteredRows();
            var header = BuildHeader();
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", header.Select(CsvCell)));

            foreach (var i in rows)
                sb.AppendLine(string.Join(",", RowCells(i).Select(CsvCell)));

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            WriteSiblingJson(dlg.FileName);
            Close();
        }

        // ── Excel (ClosedXML) ────────────────────────────

        private void WriteXlsx()
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export JKR Compliance",
                FileName = $"jkr_compliance_report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".xlsx",
                Filter = "Excel files (*.xlsx)|*.xlsx",
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

            WriteSiblingJson(dlg.FileName);
            Close();
        }

        // ── Sibling JSON (benchmark) ─────────────────────

        private void WriteSiblingJson(string exportPath)
        {
            if (CbJson.IsChecked != true || _svc == null) return;

            var dir = Path.GetDirectoryName(exportPath);
            var stem = Path.GetFileNameWithoutExtension(exportPath);

            if (!string.IsNullOrEmpty(_svc.LastRequestJson))
                File.WriteAllText(Path.Combine(dir, $"{stem}_request.json"), _svc.LastRequestJson, Encoding.UTF8);

            if (!string.IsNullOrEmpty(_svc.LastResponseJson))
                File.WriteAllText(Path.Combine(dir, $"{stem}_response.json"), _svc.LastResponseJson, Encoding.UTF8);
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
