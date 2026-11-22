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
    /// a choice opens the export dialog with that format; Close / Esc leaves.
    /// </summary>
    public partial class BorangPreviewWindow : Window
    {
        private readonly byte[] _pdf;
        private readonly string _folder;
        private readonly string _fileName;
        private readonly string _modelFile;
        private readonly int _issueCount;
        private readonly Func<IssueExportFormat, string> _issuesExport;
        private readonly IReadOnlyList<BitmapImage> _pages;
        private int _index;
        private double _zoom = 1.0;
        private const double MinZoom = 1.0, MaxZoom = 3.0, ZoomStep = 0.25;

        private BorangPreviewWindow(byte[] pdf, IReadOnlyList<byte[]> pages, string folder, string fileName, string modelFile,
                                    int issueCount, Func<IssueExportFormat, string> issuesExport)
        {
            _pdf = pdf;
            _folder = folder;
            _fileName = fileName;
            _modelFile = string.IsNullOrEmpty(modelFile) ? fileName : modelFile;
            _issueCount = issueCount;
            _issuesExport = issuesExport;
            InitializeComponent();

            _pages = pages.Select(ToImage).ToList();
            IssuesBtn.Visibility = issuesExport != null ? Visibility.Visible : Visibility.Collapsed;

            ApplyText();
            ShowPage(0);

            SizeChanged += (_, __) => FitCard();
            PreviewKeyDown += OnKey;
            Loaded += (_, __) => { FitCard(); Activate(); PdfBtn.Focus(); };
        }

        /// <summary>All the window's words.</summary>
        private void ApplyText()
        {
            TitleText.Text = PaneText.BorangPreviewTitle();
            Title = TitleText.Text;
            FileText.Text = _modelFile;
            FileText.ToolTip = _modelFile;
            NoteText.Text = PaneText.BorangPreviewNote();
            PageBox.ToolTip = PaneText.ZoomHint();
            PdfLabel.Text = PaneText.SavePdf();
            IssuesLabel.Text = PaneText.ExportIssues();
            ExcelLabel.Text = PaneText.FormatExcel();
            CsvLabel.Text = PaneText.FormatCsv();
            ExcelCount.Text = CsvCount.Text = PaneText.IssueCount(_issueCount);
            PrevBtn.ToolTip = PaneText.PreviousPage();
            NextBtn.ToolTip = PaneText.NextPage();
            CloseBtn.ToolTip = "Close";
            System.Windows.Automation.AutomationProperties.SetName(PrevBtn, PaneText.PreviousPage());
            System.Windows.Automation.AutomationProperties.SetName(NextBtn, PaneText.NextPage());
            System.Windows.Automation.AutomationProperties.SetName(CloseBtn, CloseBtn.ToolTip as string);
            PageText.Text = PaneText.PageOf(_index + 1, _pages.Count);
        }

        private void ShowPage(int index)
        {
            if (_pages.Count == 0) return;
            _index = Math.Max(0, Math.Min(_pages.Count - 1, index));
            PageImage.Source = _pages[_index];
            FitPage();
            PageText.Text = PaneText.PageOf(_index + 1, _pages.Count);
            PrevBtn.IsEnabled = _index > 0;
            NextBtn.IsEnabled = _index < _pages.Count - 1;
        }

        /// <summary>Size the page to fit the area at 100 %, times the zoom (scrollbars appear when it is larger).</summary>
        private void FitPage()
        {
            if (_pages.Count == 0 || _pages[_index].PixelWidth == 0) return;
            var aspect = (double)_pages[_index].PixelWidth / _pages[_index].PixelHeight;
            var availW = Math.Max(50, PageScroll.ViewportWidth > 0 ? PageScroll.ViewportWidth : PageScroll.ActualWidth) - 48;
            var availH = Math.Max(50, PageScroll.ViewportHeight > 0 ? PageScroll.ViewportHeight : PageScroll.ActualHeight) - 30;
            var w = Math.Min(availW, availH * aspect);
            if (w <= 0) return;
            PageImage.Width = w * _zoom;
            PageImage.Height = w * _zoom / aspect;
        }

        private void SetZoom(double z)
        {
            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, z));
            FitPage();
        }

        private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitPage();

        private void PageScroll_Wheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetZoom(_zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
            e.Handled = true;
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
                case Key.D0: case Key.NumPad0:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { SetZoom(1.0); e.Handled = true; }
                    break;
                case Key.Left: case Key.PageUp:
                    ShowPage(_index - 1); e.Handled = true; break;
                case Key.Right: case Key.PageDown:
                    ShowPage(_index + 1); e.Handled = true; break;
            }
        }

        /// <summary>Render the form and show the preview over Revit. The PDF and the
        /// page images are made off the UI thread (same engine, same pages).
        /// <paramref name="issueCount"/> is how many issues the Excel / CSV export would
        /// write with its default options; <paramref name="issuesExport"/> opens that (and returns what it wrote, null if cancelled)
        /// export with the chosen format.</summary>
        public static async Task<BorangPreviewWindow> OpenAsync(BorangDoc doc, FrameworkElement anchor, string folder,
                                                                 string fileName, int issueCount,
                                                                 Func<IssueExportFormat, string> issuesExport)
        {
            var (pdf, pages) = await Task.Run(() => (BorangPdf.Pdf(doc), (IReadOnlyList<byte[]>)BorangPdf.Pages(doc, dpi: 200)));
            var w = new BorangPreviewWindow(pdf, pages, folder, fileName, doc.Header?.File, issueCount, issuesExport);
            if (!RevitWindowCover.Cover(w, anchor)) RevitWindowCover.CenterOnRevit(w, anchor);
            RevitWindowCover.ReturnToOwnerOnClose(w);
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
                Title = PanelStrings.Get("exportPreview"),
                FileName = _fileName,
                DefaultExt = ".pdf",
                Filter = "PDF files (*.pdf)|*.pdf",
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
            // The preview stays open behind the export dialog, which it owns (live F13);
            // what the export wrote shows here, as Save PDF's line does (live F14).
            CloseMenu();
            var outcome = _issuesExport?.Invoke(format);
            if (!string.IsNullOrEmpty(outcome)) ShowStatus(outcome);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
