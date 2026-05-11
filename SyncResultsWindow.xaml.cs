using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace RevitWebAppSync
{
    public partial class SyncResultsWindow : Window
    {
        private SyncResultData _resultData;
        public SyncResultsWindow(SyncResultData resultData)
        {
            InitializeComponent();
            _resultData = resultData;
            PopulateData();
        }
        
        private void PopulateData()
        {
            if (_resultData == null) return;
            
            // Update header based on overall success
            if (_resultData.IsFullySuccessful)
            {
                HeaderText.Text = "🎉 Upload Completed Successfully!";
                HeaderText.Foreground = System.Windows.Media.Brushes.Green;
                SummaryText.Text = "Your file has been uploaded to BINA Cloud with full platform support. Files will be appearing in BINA shortly.";
            }
            else if (_resultData.IsPartiallySuccessful)
            {
                HeaderText.Text = "⚠️ Upload Partially Successful";
                HeaderText.Foreground = System.Windows.Media.Brushes.Orange;
                SummaryText.Text = "Your file was uploaded but some features may be limited. Files will be appearing in BINA shortly.";
            }
            else
            {
                HeaderText.Text = "❌ Upload Failed";
                HeaderText.Foreground = System.Windows.Media.Brushes.Red;
                SummaryText.Text = "There were issues uploading your file";
            }
            
            // Populate file information
            FileNameText.Text = _resultData.FileName ?? "Unknown";
            DisciplineText.Text = _resultData.DisciplineType ?? "Unknown";
            FileSizeText.Text = FormatFileSize(_resultData.FileSize);
            VersionText.Text = _resultData.Version ?? "N/A";
            
            // Update status indicators
            UpdateStatusIndicators();
            
            // Show linked files if any
            if (_resultData.LinkedFiles != null && _resultData.LinkedFiles.Count > 0)
            {
                LinkedFilesSection.Visibility = Visibility.Visible;
                LinkedFilesGrid.ItemsSource = _resultData.LinkedFiles;
            }
            
            // Show errors if any
            if (!string.IsNullOrEmpty(_resultData.ErrorMessage))
            {
                ErrorSection.Visibility = Visibility.Visible;
                ErrorMessageText.Text = _resultData.ErrorMessage;
            }
        }
        
        private void UpdateStatusIndicators()
        {
            // BINA OBS Status
            if (_resultData.BinaObsSuccess)
            {
                BinaStatusText.Text = "✅ Uploaded";
                BinaStatusText.Foreground = System.Windows.Media.Brushes.Green;
                BinaLocationText.Text = $"Location: {_resultData.BinaLocation}";
            }
            else
            {
                BinaStatusText.Text = "❌ Failed";
                BinaStatusText.Foreground = System.Windows.Media.Brushes.Red;
                BinaLocationText.Text = "Upload failed";
            }
            
            // Autodesk OSS Status
            if (_resultData.AutodeskOssSuccess)
            {
                AutodeskStatusText.Text = "✅ Ready";
                AutodeskStatusText.Foreground = System.Windows.Media.Brushes.Green;
                AutodeskUrnText.Text = $"URN: {TruncateUrn(_resultData.AutodeskUrn)}";
            }
            else
            {
                AutodeskStatusText.Text = "❌ Failed";
                AutodeskStatusText.Foreground = System.Windows.Media.Brushes.Red;
                AutodeskUrnText.Text = "Autodesk viewer not available";
            }
            
            // Registration Status
            if (_resultData.RegistrationSuccess)
            {
                RegistrationStatusText.Text = "✅ Saved";
                RegistrationStatusText.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                RegistrationStatusText.Text = "❌ Failed";
                RegistrationStatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }
        
        private string FormatFileSize(long sizeInBytes)
        {
            if (sizeInBytes <= 0) return "0 bytes";
            
            string[] sizeUnits = { "bytes", "KB", "MB", "GB" };
            double size = sizeInBytes;
            int unitIndex = 0;
            
            while (size >= 1024 && unitIndex < sizeUnits.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            
            return $"{size:F1} {sizeUnits[unitIndex]}";
        }
        
        private string TruncateUrn(string urn)
        {
            if (string.IsNullOrEmpty(urn)) return "N/A";
            return urn.Length > 50 ? urn.Substring(0, 50) + "..." : urn;
        }
        
        private void ViewInBinaButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Open BINA Cloud in default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = BinaConfig.Load().ResolvedApiBaseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open BINA Cloud: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

    }
    
    // Data model for sync results
    public class SyncResultData
    {
        public string FileName { get; set; }
        public string DisciplineType { get; set; }
        public long FileSize { get; set; }
        public string Version { get; set; }
        
        public bool BinaObsSuccess { get; set; }
        public string BinaLocation { get; set; }
        
        public bool AutodeskOssSuccess { get; set; }
        public string AutodeskUrn { get; set; }
        
        public bool RegistrationSuccess { get; set; }
        
        public List<LinkedFileInfo> LinkedFiles { get; set; }
        public string ErrorMessage { get; set; }
        
        public bool IsFullySuccessful => BinaObsSuccess && AutodeskOssSuccess && RegistrationSuccess;
        public bool IsPartiallySuccessful => BinaObsSuccess && (AutodeskOssSuccess || RegistrationSuccess);
    }
}