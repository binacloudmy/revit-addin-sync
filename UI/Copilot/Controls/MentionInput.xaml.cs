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

        /// <summary>Raised when the user pastes an image (e.g. a screenshot) into
        /// the editor. The PromptBar shows it as a pending thumbnail and sends it
        /// with the next prompt. Text pastes are unaffected.</summary>
        public event Action<System.Windows.Media.Imaging.BitmapSource> ImagePasted;

        /// <summary>Raised when the user drops one or more files onto the input area.</summary>
        public event Action<string[]> FileDropped;

        /// <summary>Raised when the user picks a tool from the "/" command palette.</summary>
        public event Action<SlashTool> ToolPicked;

        /// <summary>When set (by the PromptBar while a slash command is pending),
        /// Enter/Send submits even with an empty editor — the command carries the turn.</summary>
        public bool AllowEmptySubmit { get; set; }

        private int _atIndex = -1;
        private int _slashIndex = -1;

        // The "/" command palette is hosted OUTSIDE this control (as an in-panel
        // overlay in ChatView) so it can't float past the pane edges like a Popup.
        // ChatView wires it in via AttachSlashPalette; until then, "/" does nothing
        // special (e.g. the Result/Library follow-up composers have no palette).
        private Controls.CommandPalette _palette;
        private Action<bool> _setSlashVisible;
        private bool _slashOpen;

        /// <summary>Host the palette overlay: give this composer the palette instance
        /// and a callback that shows/hides the overlay layer.</summary>
        public void AttachSlashPalette(Controls.CommandPalette palette, Action<bool> setVisible)
        {
            if (_palette != null) _palette.ToolPicked -= OnSlashToolPicked;
            _palette = palette;
            _setSlashVisible = setVisible;
            if (_palette != null) _palette.ToolPicked += OnSlashToolPicked;
        }

        public MentionInput()
        {
            InitializeComponent();
            Editor.TextChanged += OnTextChanged;
            Editor.PreviewKeyDown += OnPreviewKeyDown;
            DataObject.AddPastingHandler(Editor, OnPaste);
            Loaded += (_, __) => UpdatePlaceholder();
            Editor.DragOver += (_, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            };
            Editor.Drop += (_, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (paths?.Length > 0) FileDropped?.Invoke(paths);
                    e.Handled = true;
                }
            };
        }

        private void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                // Only fires when the paste command actually runs (i.e. the
                // clipboard had a text format too — e.g. copied from Word).
                // Text wins there; image-only pastes are handled in
                // OnPreviewKeyDown because the TextBox DISABLES its paste
                // command for a text-less clipboard and this handler never runs.
                if (!e.DataObject.GetDataPresent(DataFormats.Bitmap)) return;
                if (e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                    || e.DataObject.GetDataPresent(DataFormats.Text)) return;
                var img = Clipboard.GetImage();
                if (img == null) return;
                e.CancelCommand();
                ImagePasted?.Invoke(img);
            }
            catch { /* clipboard access can fail transiently inside Revit — ignore */ }
        }

        public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
            nameof(PlaceholderText), typeof(string), typeof(MentionInput),
            new PropertyMetadata("Ask Copilot...", OnPlaceholderChanged));
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

            // A "/" command token (at line start or after whitespace) takes
            // precedence when it sits closer to the caret than any "@".
            // MentionToken.Find owns the "@" rules (spaces allowed in the query
            // so "@Aras 01" matches; caret==0 during a programmatic Text set
            // yields no token instead of a negative-length Substring throw).
            int slash = LastSlashTrigger(text, caret);
            int at = Model.MentionToken.Find(text, caret, out string query);
            if (slash >= 0 && slash >= at)
            {
                ClosePicker();
                OpenSlash(text, slash, caret);
                return;
            }
            CloseSlash();

            if (at < 0) { ClosePicker(); return; }

            _atIndex = at;
            BuildPicker(query);
        }

        // Index of the "/" that starts the command token ending at the caret, or -1.
        // The slash must sit at the start of the input or right after whitespace
        // (so "and/or", URLs and paths don't trigger the palette), and the run from
        // it to the caret must be a single unbroken token.
        private static int LastSlashTrigger(string text, int caret)
        {
            for (int i = caret - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '/')
                    return (i == 0 || char.IsWhiteSpace(text[i - 1])) ? i : -1;
                if (char.IsWhiteSpace(c)) return -1;   // token broken before a slash
            }
            return -1;
        }

        private void OpenSlash(string text, int slashIdx, int caret)
        {
            if (_palette == null) return;   // no overlay attached (non-chat composer)
            _slashIndex = slashIdx;
            string query = text.Substring(slashIdx + 1, caret - slashIdx - 1);
            if (!_slashOpen) { _palette.Open(query); _slashOpen = true; _setSlashVisible?.Invoke(true); }
            else _palette.SetQuery(query);
        }

        private void CloseSlash()
        {
            _slashIndex = -1;
            if (_slashOpen) { _slashOpen = false; _setSlashVisible?.Invoke(false); }
        }

        /// <summary>Close the palette from the host (scrim click-outside).</summary>
        public void CloseSlashExternal() => CloseSlash();

        // Picked from the palette (click or Enter): strip the "/query" from the
        // editor, close, and hand the tool up to the PromptBar for the chip.
        private void OnSlashToolPicked(SlashTool tool)
        {
            var text = Editor.Text ?? "";
            int caret = Editor.CaretIndex;
            if (_slashIndex >= 0 && _slashIndex <= text.Length)
            {
                if (caret < _slashIndex || caret > text.Length) caret = text.Length;
                string before = text.Substring(0, _slashIndex);
                string after = text.Substring(caret);
                Editor.Text = before + after;
                Editor.CaretIndex = before.Length;
            }
            CloseSlash();
            Editor.Focus();
            ToolPicked?.Invoke(tool);
        }

        // Design popover (lines 502-512): ONE "REFERENCE" caps header, then flat
        // rows — sunken @-tile, label, right-aligned type — no per-group headers.
        private void BuildPicker(string query)
        {
            PickerHost.Children.Clear();
            var groups = Provider?.GetGroups() ?? new List<MentionGroup>();
            bool any = false;

            PickerHost.Children.Add(new TextBlock
            {
                Text = "REFERENCE", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#99a3b3"), Margin = new Thickness(8, 5, 8, 6),
            });

            foreach (var g in groups)
            {
                var matches = g.Items.Where(it => Model.MentionToken.Matches(it, query)).ToList();
                if (matches.Count == 0) continue;
                any = true;

                // Singular type label ("Levels" → "Level") like the design rows.
                var typeLabel = g.Label.EndsWith("s") ? g.Label.Substring(0, g.Label.Length - 1) : g.Label;

                foreach (var item in matches)
                {
                    var row = new Button { Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(9, 8, 9, 8) };
                    row.Template = RowTemplate();
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = CopilotColors.From("#f3f6f9"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
                    tile.Child = new TextBlock { Text = "@", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = CopilotColors.From("#1d4ed8"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    grid.Children.Add(tile);
                    var name = new TextBlock { Text = item, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = CopilotColors.From("#131c2b"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(name, 1);
                    grid.Children.Add(name);
                    var kind = new TextBlock { Text = typeLabel, FontSize = 10, Foreground = CopilotColors.From("#99a3b3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                    Grid.SetColumn(kind, 2);
                    grid.Children.Add(kind);
                    row.Content = grid;
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
            // Ctrl+V with an IMAGE-ONLY clipboard (Win+Shift+S snip, PrtScn):
            // the TextBox disables its paste command when there's no text
            // format, so DataObject.AddPastingHandler never fires — catch the
            // keystroke directly. Text (or text+image) pastes stay native.
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                try
                {
                    if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
                    {
                        var img = Clipboard.GetImage();
                        if (img != null)
                        {
                            e.Handled = true;
                            ImagePasted?.Invoke(img);
                            return;
                        }
                    }
                }
                catch { /* clipboard busy — fall through to the normal paste */ }
            }

            // "/" command palette gets first dibs on the nav keys while it's open.
            if (_slashOpen && _palette != null)
            {
                switch (e.Key)
                {
                    case Key.Escape: CloseSlash(); e.Handled = true; return;
                    case Key.Up: _palette.MoveActive(-1); e.Handled = true; return;
                    case Key.Down: _palette.MoveActive(1); e.Handled = true; return;
                    case Key.Enter:
                    case Key.Tab:
                        _palette.PickActive();   // fires ToolPicked → OnSlashToolPicked
                        e.Handled = true; return;
                    // Backspace with an empty query (caret right after "/") deletes the
                    // "/" and DetectToken then closes the palette — no special-casing.
                }
            }

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
            // Empty text still submits when a slash command is pending (AllowEmptySubmit)
            // so a bare "/tool" turn can be sent with no extra prose.
            if (string.IsNullOrEmpty(text) && !AllowEmptySubmit) return;
            var mentions = ParseMentions(text);
            Submitted?.Invoke(text, mentions);
            Editor.Clear();
            ClosePicker();
            CloseSlash();
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
            // Live DynamicResource (cached template) so hover swaps with the theme
            // instead of freezing to the first-rendered one (black-in-light bug).
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new System.Windows.DynamicResourceExtension("Cp.Hover"), "bd"));
            var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = b };
            t.Triggers.Add(trigger);
            return _rowTemplate = t;
        }
    }
}
