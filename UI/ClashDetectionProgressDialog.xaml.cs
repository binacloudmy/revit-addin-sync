using System;
using System.Threading;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Progress dialog for clash detection operations
    /// Displays real-time progress updates and supports cancellation
    /// </summary>
    public partial class ClashDetectionProgressDialog : Window, IProgress<ClashDetectionProgress>
    {
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isCompleted;
        private bool _isCancelled;

        /// <summary>
        /// Gets the cancellation token for the operation
        /// </summary>
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        /// <summary>
        /// Gets whether the operation was cancelled by the user
        /// </summary>
        public bool WasCancelled => _isCancelled;

        /// <summary>
        /// Gets whether the operation completed successfully
        /// </summary>
        public bool IsCompleted => _isCompleted;

        /// <summary>
        /// Initializes a new instance of the ClashDetectionProgressDialog
        /// </summary>
        public ClashDetectionProgressDialog()
        {
            InitializeComponent();
            _cancellationTokenSource = new CancellationTokenSource();
            _isCompleted = false;
            _isCancelled = false;

            // Handle window closing
            Closing += ClashDetectionProgressDialog_Closing;
        }

        #region IProgress<ClashDetectionProgress> Implementation

        /// <summary>
        /// Reports progress update from the clash detection service
        /// </summary>
        /// <param name="progress">Progress information</param>
        public void Report(ClashDetectionProgress progress)
        {
            // Update UI on the dispatcher thread
            Dispatcher.Invoke(() =>
            {
                UpdateProgress(progress);
            });
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Updates the UI with progress information
        /// </summary>
        /// <param name="progress">Progress information</param>
        private void UpdateProgress(ClashDetectionProgress progress)
        {
            if (progress == null)
                return;

            // Update phase text
            if (!string.IsNullOrEmpty(progress.Phase))
            {
                PhaseTextBlock.Text = progress.Phase;
            }

            // Update progress bar
            MainProgressBar.Value = progress.PercentComplete;

            // Update percentage text
            PercentageTextBlock.Text = $"{progress.PercentComplete}%";

            // Check if operation completed
            if (progress.Phase == "Complete" || progress.PercentComplete >= 100)
            {
                OperationCompleted();
            }
            else if (progress.Phase == "Cancelled")
            {
                OperationCancelled();
            }
        }

        /// <summary>
        /// Called when the operation completes successfully
        /// </summary>
        private void OperationCompleted()
        {
            _isCompleted = true;
            PhaseTextBlock.Text = "Clash Detection Complete!";
            MainProgressBar.Value = 100;
            PercentageTextBlock.Text = "100%";
            CancelButton.IsEnabled = false;

            // Auto-close after a short delay
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                DialogResult = true;
                Close();
            };
            timer.Start();
        }

        /// <summary>
        /// Called when the operation is cancelled
        /// </summary>
        private void OperationCancelled()
        {
            _isCancelled = true;
            PhaseTextBlock.Text = "Operation Cancelled";
            CancelButton.IsEnabled = false;
            PercentageTextBlock.Text = "Cancelled";

            // Auto-close after a short delay
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                DialogResult = false;
                Close();
            };
            timer.Start();
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles the Cancel button click
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RequestCancellation();
        }

        /// <summary>
        /// Handles window closing event
        /// </summary>
        private void ClashDetectionProgressDialog_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Prevent closing if operation is still running
            if (!_isCompleted && !_isCancelled)
            {
                var result = MessageBox.Show(
                    "Clash detection is still in progress. Are you sure you want to cancel?",
                    "Cancel Confirmation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    RequestCancellation();
                }
                else
                {
                    e.Cancel = true; // Prevent window from closing
                }
            }
        }

        /// <summary>
        /// Requests cancellation of the operation
        /// </summary>
        private void RequestCancellation()
        {
            if (_isCancelled || _isCompleted)
                return;

            // Update UI to show cancelling state
            PhaseTextBlock.Text = "Cancelling...";
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";

            // Request cancellation
            _cancellationTokenSource.Cancel();
            _isCancelled = true;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Shows the dialog and starts tracking progress
        /// </summary>
        /// <returns>True if completed successfully, false if cancelled</returns>
        public new bool? ShowDialog()
        {
            // Reset state
            _isCompleted = false;
            _isCancelled = false;
            MainProgressBar.Value = 0;
            PhaseTextBlock.Text = "Initializing...";
            PercentageTextBlock.Text = "0%";
            CancelButton.IsEnabled = true;
            CancelButton.Content = "Cancel";

            return base.ShowDialog();
        }

        /// <summary>
        /// Manually sets completion state (for error handling)
        /// </summary>
        /// <param name="success">Whether the operation completed successfully</param>
        public void SetCompleted(bool success)
        {
            Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    OperationCompleted();
                }
                else
                {
                    _isCompleted = false;
                    _isCancelled = false;
                    PhaseTextBlock.Text = "Operation Failed";
                    CancelButton.IsEnabled = false;

                    // Don't auto-close on failure - let user read the error
                }
            });
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Disposes resources used by the dialog
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _cancellationTokenSource?.Dispose();
        }

        #endregion
    }
}
