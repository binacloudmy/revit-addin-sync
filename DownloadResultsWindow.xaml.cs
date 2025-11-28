using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace RevitWebAppSync
{
    public partial class DownloadResultsWindow : Window
    {
        private DownloadResultData _resultData;

        public DownloadResultsWindow(DownloadResultData resultData)
        {
            InitializeComponent();
            _resultData = resultData;
            PopulateData();
        }

        private void PopulateData()
        {
            if (_resultData == null) return;

            // Update header based on success
            if (_resultData.DownloadedFiles.Count > 0 && string.IsNullOrEmpty(_resultData.ErrorMessage))
            {
                HeaderText.Text = "Download Completed Successfully!";
                HeaderText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"));
                SummaryText.Text = "BIM discipline files have been downloaded from BINA Cloud";
            }
            else if (_resultData.DownloadedFiles.Count > 0)
            {
                HeaderText.Text = "Download Partially Successful";
                HeaderText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F57C00"));
                SummaryText.Text = "Some files were downloaded but issues occurred";
            }
            else
            {
                HeaderText.Text = "No Files Downloaded";
                HeaderText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
                SummaryText.Text = "No discipline files were available for download";
                TipSection.Visibility = Visibility.Collapsed;
            }

            // Populate summary
            ProjectNameText.Text = _resultData.ProjectName ?? "Unknown";
            TotalFilesText.Text = $"{_resultData.DownloadedFiles.Count} file(s)";
            DownloadLocationText.Text = _resultData.DownloadLocation ?? "Unknown";

            // Populate downloaded files list
            if (_resultData.DownloadedFiles.Count > 0)
            {
                var displayItems = new List<DownloadedFileDisplay>();
                foreach (var file in _resultData.DownloadedFiles)
                {
                    displayItems.Add(new DownloadedFileDisplay
                    {
                        Icon = GetDisciplineIcon(file.DisciplineName),
                        DisciplineName = file.DisciplineName,
                        FileName = file.FileName,
                        StatusText = file.Success ? "Downloaded" : "Failed",
                        StatusColor = file.Success
                            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32"))
                            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"))
                    });
                }
                DownloadedFilesPanel.ItemsSource = displayItems;
                NoFilesMessage.Visibility = Visibility.Collapsed;
            }
            else
            {
                NoFilesMessage.Visibility = Visibility.Visible;
            }

            // Show errors if any
            if (!string.IsNullOrEmpty(_resultData.ErrorMessage))
            {
                ErrorSection.Visibility = Visibility.Visible;
                ErrorMessageText.Text = _resultData.ErrorMessage;
            }
        }

        private string GetDisciplineIcon(string disciplineName)
        {
            return disciplineName?.ToLower() switch
            {
                "structure" => "🏗️",
                "architecture" => "🏠",
                "hvac" => "❄️",
                "electrical" => "⚡",
                _ => "📄"
            };
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_resultData?.DownloadLocation))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = _resultData.DownloadLocation,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }

    // Data model for download results
    public class DownloadResultData
    {
        public string ProjectName { get; set; }
        public string DownloadLocation { get; set; }
        public List<DownloadedFileInfo> DownloadedFiles { get; set; } = new List<DownloadedFileInfo>();
        public string ErrorMessage { get; set; }
    }

    public class DownloadedFileInfo
    {
        public string DisciplineName { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public bool Success { get; set; }
    }

    // Display model for UI binding
    public class DownloadedFileDisplay
    {
        public string Icon { get; set; }
        public string DisciplineName { get; set; }
        public string FileName { get; set; }
        public string StatusText { get; set; }
        public SolidColorBrush StatusColor { get; set; }
    }
}
