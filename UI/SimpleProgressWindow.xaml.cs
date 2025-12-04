using System;
using System.Windows;
using System.Windows.Threading;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// A simple non-modal progress window that can be updated during long operations
    /// </summary>
    public partial class SimpleProgressWindow : Window
    {
        private static SimpleProgressWindow _instance;

        public SimpleProgressWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows the progress window with specified title and status
        /// </summary>
        public static SimpleProgressWindow Show(string title, string status)
        {
            // Close any existing instance
            _instance?.Close();

            _instance = new SimpleProgressWindow();
            _instance.TitleText.Text = title;
            _instance.StatusText.Text = status;
            _instance.Show();

            // Force UI to update
            DoEvents();

            return _instance;
        }

        /// <summary>
        /// Updates the status text
        /// </summary>
        public void UpdateStatus(string status)
        {
            StatusText.Text = status;
            DoEvents();
        }

        /// <summary>
        /// Updates title and status
        /// </summary>
        public void Update(string title, string status)
        {
            TitleText.Text = title;
            StatusText.Text = status;
            DoEvents();
        }

        /// <summary>
        /// Sets progress bar to determinate mode with percentage
        /// </summary>
        public void SetProgress(int percent)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = percent;
            DoEvents();
        }

        /// <summary>
        /// Closes the progress window
        /// </summary>
        public static void CloseWindow()
        {
            _instance?.Close();
            _instance = null;
        }

        /// <summary>
        /// Process pending UI messages to keep window responsive
        /// </summary>
        private static void DoEvents()
        {
            try
            {
                Application.Current?.Dispatcher?.Invoke(
                    DispatcherPriority.Background,
                    new Action(delegate { }));
            }
            catch
            {
                // Ignore errors during shutdown
            }
        }
    }
}
