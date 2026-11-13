using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using RevitWebAppSync.UI.Jkr.Modals;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Export
{
    /// <summary>
    /// Export preview · Borang Model Audit (prototype export view; frames e-p1,
    /// e-sum). Shows the PDF's own pages over Revit's window; PDF saves them
    /// (and opens the file), "Export issues (Excel/CSV)" is the secondary export
    /// of the issue list, Close / Esc leaves.
    /// </summary>
    public partial class BorangPreviewWindow : Window
    {
        private readonly byte[] _pdf;
        private readonly string _folder;
        private readonly string _fileName;
        private readonly Action _issuesExport;

        private BorangPreviewWindow(byte[] pdf, IReadOnlyList<byte[]> pages, string folder, string fileName, Action issuesExport)
        {
            _pdf = pdf;
            _folder = folder;
            _fileName = fileName;
            _issuesExport = issuesExport;
            InitializeComponent();

            TitleText.Text = PanelStrings.Get(PaneLanguage.Current, "exportPreview");
            CloseBtn.Content = PanelStrings.Get(PaneLanguage.Current, "close");
            IssuesBtn.Content = PaneText.ExportIssues();
            IssuesBtn.Visibility = issuesExport != null ? Visibility.Visible : Visibility.Collapsed;
            PagesList.ItemsSource = pages.Select(ToImage).ToList();

            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
            Loaded += (_, __) => { Activate(); PdfBtn.Focus(); };
        }

        /// <summary>Render the form and show the preview over Revit. The PDF and the
        /// page images are made off the UI thread (same engine, same pages).</summary>
        public static async Task<BorangPreviewWindow> OpenAsync(BorangDoc doc, FrameworkElement anchor, string folder,
                                                                 string fileName, Action issuesExport)
        {
            var (pdf, pages) = await Task.Run(() => (BorangPdf.Pdf(doc), (IReadOnlyList<byte[]>)BorangPdf.Pages(doc)));
            var w = new BorangPreviewWindow(pdf, pages, folder, fileName, issuesExport);
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

        private void Issues_Click(object sender, RoutedEventArgs e)
        {
            var export = _issuesExport;
            Close();
            export?.Invoke();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
