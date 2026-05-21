using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Bottom prompt bar. Hosts a MentionInput; Enter or the send button fires SubmitCommand
    /// with the composed text (mentions are parsed inline by the editor).
    /// </summary>
    public partial class PromptBar : UserControl
    {
        public PromptBar()
        {
            InitializeComponent();
            SendBtn.Click += (_, __) => Input.TriggerSubmit();
            Input.Submitted += (text, mentions) =>
            {
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

        private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pb = (PromptBar)d;
            if (pb.Input != null) pb.Input.PlaceholderText = (string)e.NewValue;
        }
    }
}
