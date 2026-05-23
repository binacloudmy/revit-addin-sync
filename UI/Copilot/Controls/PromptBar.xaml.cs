using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Bottom prompt bar. Hosts a MentionInput; Enter or the send button fires SubmitCommand
    /// with the composed text (mentions are parsed inline by the editor).
    ///
    /// IsBusy=true (bound to VM.IsRouting) greys out the send button and swallows Enter so the
    /// user can't fire a second /route while one is in flight (race-condition guard).
    /// </summary>
    public partial class PromptBar : UserControl
    {
        public PromptBar()
        {
            InitializeComponent();
            SendBtn.Click += (_, __) => { if (!IsBusy) Input.TriggerSubmit(); };
            Input.Submitted += (text, mentions) =>
            {
                if (IsBusy) return; // spam guard — VM also drops; this is the visible UX
                if (SubmitCommand != null && SubmitCommand.CanExecute(text))
                    SubmitCommand.Execute(text);
            };
        }

        public static readonly DependencyProperty SubmitCommandProperty = DependencyProperty.Register(
            nameof(SubmitCommand), typeof(ICommand), typeof(PromptBar), new PropertyMetadata(null));
        public ICommand SubmitCommand { get => (ICommand)GetValue(SubmitCommandProperty); set => SetValue(SubmitCommandProperty, value); }

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
            nameof(Placeholder), typeof(string), typeof(PromptBar),
            new PropertyMetadata("Describe a task or ask anything…", OnPlaceholderChanged));
        public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

        public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
            nameof(IsBusy), typeof(bool), typeof(PromptBar), new PropertyMetadata(false, OnBusyChanged));
        public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

        private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pb = (PromptBar)d;
            if (pb.Input != null) pb.Input.PlaceholderText = (string)e.NewValue;
        }

        private static void OnBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pb = (PromptBar)d;
            bool busy = (bool)e.NewValue;
            // Visual: dim the send button + lower opacity so it's obvious you can't send.
            if (pb.SendBtn != null)
            {
                pb.SendBtn.IsEnabled = !busy;
                pb.SendBtn.Opacity = busy ? 0.4 : 1.0;
            }
            if (pb.PlaceholderHint != null)
                pb.PlaceholderHint.Text = busy
                    ? "Waiting for a response… click Cancel above to stop."
                    : "Type @ to reference a level, category, view, or selection";
        }
    }
}
