using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Bottom prompt bar. Enter or the send button fires SubmitCommand with the typed text.
    /// The plain TextBox is swapped for the @-mention RichTextBox editor in Task 14.
    /// </summary>
    public partial class PromptBar : UserControl
    {
        public PromptBar()
        {
            InitializeComponent();
            SendBtn.Click += (_, __) => Submit();
            Input.PreviewKeyDown += OnKeyDown;
        }

        public static readonly DependencyProperty SubmitCommandProperty = DependencyProperty.Register(
            nameof(SubmitCommand), typeof(ICommand), typeof(PromptBar), new PropertyMetadata(null));
        public ICommand SubmitCommand { get => (ICommand)GetValue(SubmitCommandProperty); set => SetValue(SubmitCommandProperty, value); }

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
            nameof(Placeholder), typeof(string), typeof(PromptBar), new PropertyMetadata("Describe a task or ask anything…"));
        public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                Submit();
            }
        }

        private void Submit()
        {
            var text = Input.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            if (SubmitCommand != null && SubmitCommand.CanExecute(text))
                SubmitCommand.Execute(text);
            Input.Clear();
        }
    }
}
