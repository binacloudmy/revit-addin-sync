using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitWebAppSync.UI.Jkr.Modals;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Export
{
    /// <summary>
    /// Borang BIM010 preview (Clear Sheet). Shows the PDF's own pages one at a time
    /// over Revit's window; Save PDF writes them (and opens the file); Export issues
    /// opens a small menu, Excel / CSV with the number of issues each would write, and
    /// a choice opens the export dialog with that format; Close / Esc leaves. The BM / EN
    /// switch is the pane's own.
    /// </summary>
    public partial class BorangPreviewWindow : Window
    {
        private readonly byte[] _pdf;
        private readonly string _folder;
        private readonly string _fileName;
        private readonly string _modelFile;
        private readonly int _issueCount;
        private readonly Action<IssueExportFormat> _issuesExport;
        private readonly Action<string> _setLanguage;
        private readonly IReadOnlyList<BitmapImage> _pages;
        private int _index;

        private BorangPreviewWindow(byte[] pdf, IReadOnlyList<byte[]> pages, string folder, string fileName, string modelFile,
                                    int issueCount, Action<IssueExportFormat> issuesExport, Action<string> setLanguage)
        {
            _pdf = pdf;
            _folder = folder;
            _fileName = fileName;
            _modelFile = string.IsNullOrEmpty(modelFile) ? fileName : modelFile;
            _issueCount = issueCount;
            _issuesExport = issuesExport;
            _setLanguage = setLanguage;
            InitializeComponent();

            _pages = pages.Select(ToImage).ToList();
            IssuesBtn.Visibility = issuesExport != null ? Visibility.Visible : Visibility.Collapsed;

            ApplyText();
            ShowPage(0);

            PaneLanguage.Changed += OnLanguageChanged;
            Closed += (_, __) => PaneLanguage.Changed -= OnLanguageChanged;
            SizeChanged += (_, __) => FitCard();
            PreviewKeyDown += OnKey;
            Loaded += (_, __) => { FitCard(); Activate(); PdfBtn.Focus(); };
        }

        private void OnLanguageChanged() => Dispatcher.BeginInvoke(new Action(ApplyText));

        /// <summary>All the window's words, in the pane's language.</summary>
        private void ApplyText()
        {
            TitleText.Text = PaneText.BorangPreviewTitle();
            Title = TitleText.Text;
            FileText.Text = _modelFile;
            FileText.ToolTip = _modelFile;
            NoteText.Text = PaneText.BorangPreviewNote();
            PdfLabel.Text = PaneText.SavePdf();
            IssuesLabel.Text = PaneText.ExportIssues();
            ExcelLabel.Text = PaneText.FormatExcel();
            CsvLabel.Text = PaneText.FormatCsv();
            ExcelCount.Text = CsvCount.Text = PaneText.IssueCount(_issueCount);
            PrevBtn.ToolTip = PaneText.PreviousPage();
            NextBtn.ToolTip = PaneText.NextPage();
            CloseBtn.ToolTip = PaneLanguage.Pick("Tutup", "Close");
            System.Windows.Automation.AutomationProperties.SetName(PrevBtn, PaneText.PreviousPage());
            System.Windows.Automation.AutomationProperties.SetName(NextBtn, PaneText.NextPage());
            System.Windows.Automation.AutomationProperties.SetName(CloseBtn, CloseBtn.ToolTip as string);
            BmBtn.IsChecked = !PaneLanguage.IsEn;
            EnBtn.IsChecked = PaneLanguage.IsEn;
            PageText.Text = PaneText.PageOf(_index + 1, _pages.Count);
        }

        private void ShowPage(int index)
        {
            if (_pages.Count == 0) return;
            _index = Math.Max(0, Math.Min(_pages.Count - 1, index));
            PageImage.Source = _pages[_index];
            PageText.Text = PaneText.PageOf(_index + 1, _pages.Count);
            PrevBtn.IsEnabled = _index > 0;
            NextBtn.IsEnabled = _index < _pages.Count - 1;
        }

        /// <summary>The card is 480 x 820 where the screen allows, else it shrinks to fit.</summary>
        private void FitCard()
        {
            Card.Width = Math.Max(320, Math.Min(480, ActualWidth - 48));
            Card.Height = Math.Max(400, Math.Min(820, ActualHeight - 48));
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (IssuesMenu.Visibility == Visibility.Visible) CloseMenu(); else Close();
                    e.Handled = true; break;
                case Key.Left: case Key.PageUp:
                    ShowPage(_index - 1); e.Handled = true; break;
                case Key.Right: case Key.PageDown:
                    ShowPage(_index + 1); e.Handled = true; break;
            }
        }

        /// <summary>Render the form and show the preview over Revit. The PDF and the
        /// page images are made off the UI thread (same engine, same pages).
        /// <paramref name="issueCount"/> is how many issues the Excel / CSV export would
        /// write with its default options; <paramref name="issuesExport"/> opens that
        /// export with the chosen format; <paramref name="setLanguage"/> switches the pane's
        /// language ("ms" / "en").</summary>
        public static async Task<BorangPreviewWindow> OpenAsync(BorangDoc doc, FrameworkElement anchor, string folder,
                                                                 string fileName, int issueCount,
                                                                 Action<IssueExportFormat> issuesExport, Action<string> setLanguage)
        {
            var (pdf, pages) = await Task.Run(() => (BorangPdf.Pdf(doc), (IReadOnlyList<byte[]>)BorangPdf.Pages(doc, dpi: 150)));
            var w = new BorangPreviewWindow(pdf, pages, folder, fileName, doc.Header?.File, issueCount, issuesExport, setLanguage);
            if (!RevitWindowCover.Cover(w, anchor)) RevitWindowCover.CenterOnRevit(w, anchor);
            w.Show();
            return w;
        }

        private static BitmapImage ToImage(byte[] png)
        {
            var img = new BitmapImage();
            using (var ms = new MemoryStream(png))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
            }
            img.Freeze();
            return img;
        }

        private void Pdf_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = PanelStrings.Get(PaneLanguage.Current, "exportPreview"),
                FileName = _fileName,
                DefaultExt = ".pdf",
                Filter = PaneLanguage.Pick("Fail PDF (*.pdf)|*.pdf", "PDF files (*.pdf)|*.pdf"),
            };
            try { if (!string.IsNullOrEmpty(_folder) && Directory.Exists(_folder)) dlg.InitialDirectory = _folder; } catch { }
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllBytes(dlg.FileName, _pdf);
                ShowStatus(PaneText.PdfSaved(dlg.FileName));
                try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { /* no PDF viewer: the file is saved */ }
            }
            catch (Exception ex)
            {
                ShowStatus(PaneText.PdfSaveFailed(ex.Message));
            }
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.ToolTip = text;
            StatusText.Visibility = Visibility.Visible;
        }

        private void Prev_Click(object sender, RoutedEventArgs e) => ShowPage(_index - 1);
        private void Next_Click(object sender, RoutedEventArgs e) => ShowPage(_index + 1);

        private void Bm_Click(object sender, RoutedEventArgs e) => SwitchLanguage(PanelStrings.Ms);
        private void En_Click(object sender, RoutedEventArgs e) => SwitchLanguage(PanelStrings.En);

        private void SwitchLanguage(string lang)
        {
            if (_setLanguage != null) _setLanguage(lang); else PaneLanguage.Set(lang);
            ApplyText();
        }

        private void Issues_Click(object sender, RoutedEventArgs e)
        {
            if (IssuesMenu.Visibility == Visibility.Visible) { CloseMenu(); return; }
            IssuesMenu.Visibility = Visibility.Visible;
            IssuesChevron.Data = Geometry.Parse("M4.5,6.5 L8,10 L11.5,6.5");
            ExcelItem.Focus();
        }

        private void CloseMenu()
        {
            IssuesMenu.Visibility = Visibility.Collapsed;
            IssuesChevron.Data = Geometry.Parse("M4.5,9.5 L8,6 L11.5,9.5");
            IssuesBtn.Focus();
        }

        private void Excel_Click(object sender, RoutedEventArgs e) => OpenIssueExport(IssueExportFormat.Excel);
        private void Csv_Click(object sender, RoutedEventArgs e) => OpenIssueExport(IssueExportFormat.Csv);

        private void OpenIssueExport(IssueExportFormat format)
        {
            var export = _issuesExport;
            Close();
            export?.Invoke(format);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
