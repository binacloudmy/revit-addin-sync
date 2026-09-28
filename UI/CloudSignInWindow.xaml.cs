using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Modal wait for "Login to CDE". The sign-in used to block Revit's UI thread
    /// for up to six minutes with no way out; this runs it on the thread pool,
    /// keeps Revit painting, and offers Cancel.
    ///
    /// The window only reports how the work ended (<see cref="WasCancelled"/>,
    /// <see cref="Error"/>); the command reads that after ShowDialog returns and
    /// does every Revit API call itself, on the UI thread.
    /// </summary>
    public partial class CloudSignInWindow : Window
    {
        public enum Phase { Checking, WaitingForBrowser }

        // A cancelled sign-in stops its listener at once; this only guards a task
        // that ignores the token, so the dialog can never become the new freeze.
        private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(5);

        private readonly Func<IProgress<Phase>, CancellationToken, Task> _work;
        private readonly Action _openAgain;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _task;
        private bool _finished;

        /// <summary>True when the drafter cancelled (button, Esc or the close box).</summary>
        public bool WasCancelled { get; private set; }

        /// <summary>The work's failure, if it failed; null on success or cancel.</summary>
        public Exception Error { get; private set; }

        public CloudSignInWindow(string host, Func<IProgress<Phase>, CancellationToken, Task> work, Action openAgain)
        {
            InitializeComponent();
            _work = work;
            _openAgain = openAgain;
            HostText.Text = $"Server: {host}";
            ShowPhase(Phase.Checking);
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Progress is created here, on the UI thread, so reports marshal back.
            var progress = new Progress<Phase>(ShowPhase);
            _task = Task.Run(() => _work(progress, _cts.Token));
            try
            {
                await _task;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                WasCancelled = true;
            }
            catch (Exception ex)
            {
                Error = ex;
            }
            Finish();
        }

        private void ShowPhase(Phase phase)
        {
            if (phase == Phase.Checking)
            {
                HeadingText.Text = "Checking the server…";
                StatusText.Text = "Making sure Cloud Docs sign-in is available before opening your browser.";
                OpenAgainText.Visibility = Visibility.Collapsed;
            }
            else
            {
                HeadingText.Text = "Waiting for browser sign-in…";
                StatusText.Text = "Finish signing in in the browser tab that just opened. " +
                                  "This window closes by itself when you're done.";
                OpenAgainText.Visibility = _openAgain != null ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OpenAgain_Click(object sender, RoutedEventArgs e)
        {
            try { _openAgain?.Invoke(); } catch { /* best effort — the original tab may still be open */ }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancel();

        protected override void OnClosing(CancelEventArgs e)
        {
            // The close box means Cancel. Stay open until the work has actually
            // stopped (the loopback port is released) or the grace period ends.
            if (!_finished)
            {
                e.Cancel = true;
                RequestCancel();
            }
            base.OnClosing(e);
        }

        private void RequestCancel()
        {
            if (_finished || _cts.IsCancellationRequested) return;
            _cts.Cancel();
            CancelButton.IsEnabled = false;
            HeadingText.Text = "Cancelling…";
            OpenAgainText.Visibility = Visibility.Collapsed;

            var grace = new DispatcherTimer { Interval = CancelGrace };
            grace.Tick += (s, e) =>
            {
                grace.Stop();
                if (_finished) return;
                WasCancelled = true;
                Finish();
            };
            grace.Start();
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            // Dispose only once the work is done with the token.
            if (_task != null && _task.IsCompleted) _cts.Dispose();
            Close();
        }
    }
}
