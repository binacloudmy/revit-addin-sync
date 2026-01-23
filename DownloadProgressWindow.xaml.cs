using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace RevitWebAppSync
{
    public partial class DownloadProgressWindow : Window
    {
        private readonly ObservableCollection<DownloadProgressItem> _fileItems;
        private int _successCount = 0;
        private int _failCount = 0;
        private int _totalFiles = 0;
        private int _currentFile = 0;

        public DownloadProgressWindow()
        {
            InitializeComponent();
            _fileItems = new ObservableCollection<DownloadProgressItem>();
            FileListControl.ItemsSource = _fileItems;
        }

        public void SetTotalFiles(int total)
        {
            _totalFiles = total;
            UpdateSummary();
        }

        public void SetSaveLocation(string path)
        {
            Dispatcher.Invoke(() =>
            {
                SaveLocationText.Text = $"Files saved to: {path}";
            });
        }

        public void AddFileResult(string discipline, string folder, string fileName, string filePath, bool success)
        {
            Dispatcher.Invoke(() =>
            {
                _currentFile++;

                if (success)
                    _successCount++;
                else
                    _failCount++;

                string displayName = !string.IsNullOrEmpty(folder)
                    ? $"{discipline} / {folder}"
                    : discipline;

                var item = new DownloadProgressItem
                {
                    StatusTag = success ? "OK" : "FAIL",
                    StatusBackground = success
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"))
                        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F")),
                    DisplayName = displayName,
                    FileName = fileName
                };

                _fileItems.Add(item);
                UpdateProgress();
                UpdateSummary();
            });
        }

        private void UpdateProgress()
        {
            if (_totalFiles > 0)
            {
                double progress = (_currentFile / (double)_totalFiles) * 100;
                DownloadProgressBar.Value = progress;
            }
        }

        private void UpdateSummary()
        {
            if (_currentFile == 0)
            {
                SummaryText.Text = $"Downloading {_totalFiles} file(s)...";
            }
            else
            {
                SummaryText.Text = $"Completed: {_successCount} successful, {_failCount} failed";
            }
        }

        public void SetCompleted()
        {
            Dispatcher.Invoke(() =>
            {
                HeaderText.Text = "Download Complete";
                HeaderText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"));
                DownloadProgressBar.Value = 100;
                CloseButton.IsEnabled = true;

                if (_failCount > 0)
                {
                    HeaderText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00"));
                }
            });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }

    public class DownloadProgressItem
    {
        public string StatusTag { get; set; }
        public SolidColorBrush StatusBackground { get; set; }
        public string DisplayName { get; set; }
        public string FileName { get; set; }
    }
}
