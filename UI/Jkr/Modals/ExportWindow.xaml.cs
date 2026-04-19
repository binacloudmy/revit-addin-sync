using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
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
            CbOpen.Content = $"Open ({_vm.OpenCount})";
            CbResolved.Content = $"Resolved ({_vm.ResolvedCount})";
        }

        private void Scrim_Click(object s, MouseButtonEventArgs e) { if (e.Source == s) Close(); }
        private void Card_Click(object s, MouseButtonEventArgs e) { e.Handled = true; }
        private void Excel_Click(object s, RoutedEventArgs e) => WriteCsv(".xlsx is coming — CSV for now.");
        private void Csv_Click(object s, RoutedEventArgs e) => WriteCsv(null);

        private void WriteCsv(string note)
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export JKR Compliance",
                FileName = $"jkr_compliance_report_{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".csv",
                Filter = "CSV files (*.csv)|*.csv",
            };
            if (dlg.ShowDialog() != true) return;

            var rows = _vm.Issues
                .Where(i => (CbOpen.IsChecked == true && i.IsOpen) || (CbResolved.IsChecked == true && !i.IsOpen));

            var sb = new StringBuilder();
            var header = new[] { "ID", "Title", "Category", "Priority", "Status", "Required", "Actual" };
            if (CbSpec.IsChecked == true)
                header = header.Concat(new[] { "Spec", "Clause", "Page" }).ToArray();
            sb.AppendLine(string.Join(",", header.Select(CsvCell)));

            foreach (var i in rows)
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
                sb.AppendLine(string.Join(",", cells.Select(CsvCell)));
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            Close();
        }

        private static string CsvCell(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
