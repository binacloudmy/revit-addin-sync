using System;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Progress dialog for showing sync operation progress
    /// Provides detailed progress information and cancellation support
    /// TODO: Enhance with more detailed progress reporting and error handling
    /// </summary>
    public partial class ProgressDialog : Window, INotifyPropertyChanged
    {
        #region Private Fields

        private readonly StringBuilder _progressLog;
        private readonly DispatcherTimer _elapsedTimer;
        private DateTime _startTime;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _syncTask;
        private bool _isCompleted;

        #endregion

        #region Properties

        /// <summary>
        /// Whether the sync operation was cancelled
        /// </summary>
        public bool WasCancelled { get; private set; }

        /// <summary>
        /// Whether the sync operation completed successfully
        /// </summary>
        public bool WasSuccessful { get; private set; }

        /// <summary>
        /// Error message if sync failed
        /// </summary>
        public string ErrorMessage { get; private set; }

        /// <summary>
        /// File metadata for display
        /// </summary>
        public FileMetadata FileMetadata { get; set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the progress dialog
        /// </summary>
        public ProgressDialog()
        {
            InitializeComponent();
            
            _progressLog = new StringBuilder();
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Set up elapsed time timer
            _elapsedTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _elapsedTimer.Tick += ElapsedTimer_Tick;
            
            DataContext = this;
            
            // Handle window closing
            Closing += ProgressDialog_Closing;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts monitoring a sync task and shows progress
        /// TODO: Add more granular progress reporting
        /// </summary>
        /// <param name="syncTask">The sync task to monitor</param>
        public void StartProgress(Task syncTask)
        {
            if (syncTask == null)
                throw new ArgumentNullException(nameof(syncTask));

            _syncTask = syncTask;
            _startTime = DateTime.Now;
            
            UpdateFileInfo();
            LogProgress("Sync operation started...");
            
            _elapsedTimer.Start();
            
            // Monitor task completion
            MonitorTask(syncTask);
        }

        /// <summary>
        /// Updates the current step being performed
        /// </summary>
        /// <param name="stepName">Name of current step</param>
        /// <param name="progress">Progress percentage (0-100)</param>
        public void UpdateStep(string stepName, int progress = -1)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateStep(stepName, progress));
                return;
            }

            CurrentStepText.Text = stepName;
            
            if (progress >= 0)
            {
                MainProgressBar.Value = Math.Min(100, Math.Max(0, progress));
                MainProgressBar.IsIndeterminate = false;
            }
            else
            {
                MainProgressBar.IsIndeterminate = true;
            }
            
            LogProgress($"[{DateTime.Now:HH:mm:ss}] {stepName}");
        }

        /// <summary>
        /// Logs a progress message
        /// </summary>
        /// <param name="message">Progress message</param>
        public void LogProgress(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => LogProgress(message));
                return;
            }

            _progressLog.AppendLine(message);
            ProgressLogText.Text = _progressLog.ToString();
            
            // Auto-scroll to bottom
            if (ProgressLogText.Parent is ScrollViewer scrollViewer)
            {
                scrollViewer.ScrollToEnd();
            }
        }

        /// <summary>
        /// Marks the sync as completed successfully
        /// </summary>
        /// <param name="message">Success message</param>
        public void MarkSuccess(string message = "Sync completed successfully!")
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => MarkSuccess(message));
                return;
            }

            WasSuccessful = true;
            _isCompleted = true;
            
            UpdateStep(message, 100);
            StatusText.Text = "Completed";
            StatusText.Foreground = System.Windows.Media.Brushes.Green;
            
            _elapsedTimer.Stop();
            
            CancelButton.IsEnabled = false;
            CloseButton.IsEnabled = true;
            CloseButton.Focus();
            
            LogProgress("✓ Sync operation completed successfully");
        }

        /// <summary>
        /// Marks the sync as failed
        /// </summary>
        /// <param name="errorMessage">Error message</param>
        public void MarkError(string errorMessage)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => MarkError(errorMessage));
                return;
            }

            WasSuccessful = false;
            ErrorMessage = errorMessage;
            _isCompleted = true;
            
            UpdateStep($"Error: {errorMessage}", -1);
            StatusText.Text = "Failed";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            
            _elapsedTimer.Stop();
            
            CancelButton.IsEnabled = false;
            CloseButton.IsEnabled = true;
            CloseButton.Focus();
            
            LogProgress($"✗ Sync operation failed: {errorMessage}");
        }

        /// <summary>
        /// Marks the sync as cancelled
        /// </summary>
        public void MarkCancelled()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => MarkCancelled());
                return;
            }

            WasCancelled = true;
            _isCompleted = true;
            
            UpdateStep("Operation cancelled by user", -1);
            StatusText.Text = "Cancelled";
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            
            _elapsedTimer.Stop();
            
            CancelButton.IsEnabled = false;
            CloseButton.IsEnabled = true;
            CloseButton.Focus();
            
            LogProgress("⚠ Sync operation was cancelled");
        }

        #endregion

        #region Event Handlers

        private void ElapsedTimer_Tick(object sender, EventArgs e)
        {
            var elapsed = DateTime.Now - _startTime;
            ElapsedTimeText.Text = $"{elapsed:mm\\:ss}";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // Request cancellation
            _cancellationTokenSource?.Cancel();
            
            CancelButton.IsEnabled = false;
            LogProgress("Cancellation requested...");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = WasSuccessful;
            Close();
        }

        private void ProgressDialog_Closing(object sender, CancelEventArgs e)
        {
            // If sync is still running and user tries to close, ask for confirmation
            if (!_isCompleted && _syncTask != null && !_syncTask.IsCompleted)
            {
                var result = MessageBox.Show(
                    "Sync operation is still in progress. Do you want to cancel it?",
                    "Sync in Progress",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                // Cancel the operation
                _cancellationTokenSource?.Cancel();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Monitors the sync task for completion
        /// </summary>
        /// <param name="syncTask">Task to monitor</param>
        private async void MonitorTask(Task syncTask)
        {
            try
            {
                await syncTask;
                
                // Task completed successfully
                if (!_isCompleted)
                {
                    MarkSuccess();
                }
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled
                if (!_isCompleted)
                {
                    MarkCancelled();
                }
            }
            catch (Exception ex)
            {
                // Task failed
                if (!_isCompleted)
                {
                    MarkError(ex.Message);
                }
            }
        }

        /// <summary>
        /// Updates file information display
        /// </summary>
        private void UpdateFileInfo()
        {
            if (FileMetadata != null)
            {
                FileInfoText.Text = $"File: {FileMetadata.FileName} ({FileMetadata.FileSizeFormatted})";
            }
            else
            {
                FileInfoText.Text = "File: Unknown";
            }
        }

        #endregion

        #region Step Tracking (TODO: Implement detailed progress)

        /// <summary>
        /// Enum representing sync steps for progress tracking
        /// TODO: Expand based on your sync process steps
        /// </summary>
        public enum SyncStep
        {
            Initializing,
            Authenticating,
            ExtractingMetadata,
            SelectingProject,
            CheckingChanges,
            ExportingFile,
            UploadingToOSS,
            UpdatingWebApp,
            Cleanup,
            Complete
        }

        /// <summary>
        /// Updates progress for a specific sync step
        /// TODO: Implement detailed step progress tracking
        /// </summary>
        /// <param name="step">Current sync step</param>
        /// <param name="stepProgress">Progress within this step (0-100)</param>
        public void UpdateStepProgress(SyncStep step, int stepProgress = -1)
        {
            // Calculate overall progress based on step
            var stepNames = new[]
            {
                "Initializing sync operation...",
                "Authenticating with Autodesk APS...",
                "Extracting file metadata...",
                "Selecting target project...",
                "Checking for file changes...",
                "Exporting file for upload...",
                "Uploading to cloud storage...",
                "Updating web application...",
                "Cleaning up temporary files...",
                "Sync completed!"
            };

            var stepIndex = (int)step;
            var stepName = stepIndex < stepNames.Length ? stepNames[stepIndex] : "Processing...";
            
            // Calculate overall progress (each step is roughly equal weight)
            var overallProgress = (stepIndex * 100 / stepNames.Length) + (stepProgress > 0 ? stepProgress / stepNames.Length : 0);
            
            UpdateStep(stepName, overallProgress);
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Cleanup

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            
            // Clean up resources
            _elapsedTimer?.Stop();
            _cancellationTokenSource?.Dispose();
        }

        #endregion
    }
}