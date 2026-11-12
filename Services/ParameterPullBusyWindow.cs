using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The visible busy state of a parameter pull: a small window with an
    /// indeterminate bar and, while waiting for BINA, a Cancel button. Built in
    /// code so it needs no XAML. Shown with ShowDialog while the request runs
    /// off the UI thread, so Revit keeps repainting instead of freezing.
    /// </summary>
    public sealed class ParameterPullBusyWindow : Window
    {
        private readonly TextBlock _text;
        private readonly Button _cancel;

        public bool UserCancelled { get; private set; }

        public ParameterPullBusyWindow(string text, Action onCancel)
        {
            Title = "BINA parameters";
            Width = 360;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };
            _text = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            var bar = new ProgressBar { IsIndeterminate = true, Height = 8 };
            panel.Children.Add(_text);
            panel.Children.Add(bar);

            _cancel = new Button
            {
                Content = "Cancel",
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 80,
                Visibility = onCancel == null ? Visibility.Collapsed : Visibility.Visible
            };
            _cancel.Click += (s, e) =>
            {
                UserCancelled = true;
                _cancel.IsEnabled = false;
                _text.Text = "Cancelling…";
                try { onCancel?.Invoke(); } catch { /* the pull is ending either way */ }
            };
            panel.Children.Add(_cancel);
            Content = panel;
        }

        public void SetText(string text) => _text.Text = text;

        /// <summary>Repaint now: the UI thread is about to be busy in a transaction.</summary>
        public void FlushPaint()
        {
            try { Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { })); }
            catch { /* cosmetic */ }
        }
    }
}
