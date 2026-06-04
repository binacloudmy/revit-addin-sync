using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Prompt editor with an @-mention picker. Typing "@" opens a grouped, filterable popup
    /// (Levels / Categories / Views / Current selection). Picking inserts "@Value " into the
    /// text. Enter (picker closed) submits; mentions are parsed from the final text.
    /// </summary>
    public partial class MentionInput : UserControl
    {
        /// <summary>Set once by the panel to a Revit-backed provider; nested inputs pick it up.</summary>
        public static IMentionProvider DefaultProvider;
        private static readonly IMentionProvider _static = new StaticMentionProvider();
        private IMentionProvider _provider;
        public IMentionProvider Provider { get => _provider ?? DefaultProvider ?? _static; set => _provider = value; }

        /// <summary>Raised on Enter — composed text + parsed mentions.</summary>
        public event Action<string, List<Mention>> Submitted;

        private int _atIndex = -1;

        public MentionInput()
        {
            InitializeComponent();
            Editor.TextChanged += OnTextChanged;
            Editor.PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, __) => UpdatePlaceholder();
        }

        public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
            nameof(PlaceholderText), typeof(string), typeof(MentionInput),
            new PropertyMetadata("Ask Copilot — type @ to reference a level, category, room, or view", OnPlaceholderChanged));
        public string PlaceholderText { get => (string)GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }

        private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((MentionInput)d).UpdatePlaceholder();

        private void UpdatePlaceholder()
        {
            if (Placeholder == null) return;
            Placeholder.Text = PlaceholderText;
            Placeholder.Visibility = string.IsNullOrEmpty(Editor.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholder();
            DetectToken();
        }

        private void DetectToken()
        {
            var text = Editor.Text ?? "";
            int caret = Editor.CaretIndex;
            if (caret > text.Length) caret = text.Length;

            int at = text.LastIndexOf('@', Math.Max(0, caret - 1));
            if (at < 0) { ClosePicker(); return; }

            string query = text.Substring(at + 1, caret - at - 1);
            if (query.Contains(' ') || query.Contains('\n')) { ClosePicker(); return; }

            _atIndex = at;
            BuildPicker(query);
        }

        private void BuildPicker(string query)
        {
            PickerHost.Children.Clear();
            var groups = Provider?.GetGroups() ?? new List<MentionGroup>();
            bool any = false;

            foreach (var g in groups)
            {
                var matches = g.Items.Where(it => it.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (matches.Count == 0) continue;
                any = true;

                PickerHost.Children.Add(new TextBlock
                {
                    Text = g.Label.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = CopilotColors.From("#9ca3af"), Margin = new Thickness(8, 6, 8, 3),
                });

                foreach (var item in matches)
                {
                    var row = new Button { Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6) };
                    row.Template = RowTemplate();
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var (bg, fg) = MentionStyle.For(g.Id);
                    var badge = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = CopilotColors.From(bg), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                    badge.Child = new TextBlock { Text = g.Label.Substring(0, 1), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From(fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(badge);
                    sp.Children.Add(new TextBlock { Text = item, FontSize = 12.5, Foreground = CopilotColors.From("#0b0d12"), VerticalAlignment = VerticalAlignment.Center });
                    row.Content = sp;
                    var picked = item;
                    row.Click += (_, __) => InsertMention(picked);
                    PickerHost.Children.Add(row);
                }
            }

            Picker.IsOpen = any;
        }

        private void InsertMention(string item)
        {
            var text = Editor.Text ?? "";
            int caret = Editor.CaretIndex;
            if (_atIndex < 0 || _atIndex > text.Length) { ClosePicker(); return; }
            if (caret < _atIndex) caret = text.Length;

            string before = text.Substring(0, _atIndex);
            string after = caret <= text.Length ? text.Substring(caret) : "";
            string insert = "@" + item + " ";
            Editor.Text = before + insert + after;
            Editor.CaretIndex = (before + insert).Length;
            ClosePicker();
            Editor.Focus();
        }

        private void ClosePicker()
        {
            _atIndex = -1;
            if (Picker != null) Picker.IsOpen = false;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Picker.IsOpen)
            {
                if (e.Key == Key.Escape) { ClosePicker(); e.Handled = true; return; }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    // Pick the first item row in the popup.
                    var first = PickerHost.Children.OfType<Button>().FirstOrDefault();
                    if (first != null)
                    {
                        first.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                Submit();
            }
        }

        /// <summary>Submit from an external trigger (e.g. the send button).</summary>
        public void TriggerSubmit() => Submit();

        private void Submit()
        {
            var text = (Editor.Text ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return;
            var mentions = ParseMentions(text);
            Submitted?.Invoke(text, mentions);
            Editor.Clear();
            ClosePicker();
        }

        private List<Mention> ParseMentions(string text)
        {
            var result = new List<Mention>();
            var groups = Provider?.GetGroups() ?? new List<MentionGroup>();
            foreach (var g in groups)
                foreach (var item in g.Items)
                    if (text.IndexOf("@" + item, StringComparison.OrdinalIgnoreCase) >= 0)
                        result.Add(new Mention(g.Id, item));
            return result;
        }

        private static ControlTemplate _rowTemplate;
        private static ControlTemplate RowTemplate()
        {
            if (_rowTemplate != null) return _rowTemplate;
            var b = new System.Windows.FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            b.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            b.Name = "bd";
            var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, CopilotColors.From("#f1f3f5"), "bd"));
            var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = b };
            t.Triggers.Add(trigger);
            return _rowTemplate = t;
        }
    }
}
