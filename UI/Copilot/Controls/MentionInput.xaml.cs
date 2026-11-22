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
    /// Prompt editor with an @-mention picker. "@" alone opens a Project-Browser-style
    /// tree to browse (Levels / Views by type / Sheets / Families as Category → Family →
    /// Type). "@" and a few letters opens one flat, ranked list across kinds instead
    /// (MentionSearch: types with their family, category and counts, levels, views,
    /// sheets, categories, the selection). Arrows move, Enter or Tab picks, Escape
    /// closes, the mouse works. A pick becomes a chip (MentionChips): the box shows
    /// the short name on a tinted background, one Backspace removes it, and on send
    /// its text is swapped for the form the backend matches ("Family: Type").
    /// </summary>
    public partial class MentionInput : UserControl
    {
        /// <summary>Set once by the panel to a Revit-backed provider; nested inputs pick it up.</summary>
        public static IMentionProvider DefaultProvider;
        private static readonly IMentionProvider _static = new StaticMentionProvider();
        private IMentionProvider _provider;
        public IMentionProvider Provider { get => _provider ?? DefaultProvider ?? _static; set => _provider = value; }

        /// <summary>Raised on Enter: the text to send (chips expanded to their sent
        /// form), the text as the drafter sees it (chips as typed), and the mentions.</summary>
        public event Action<string, string, List<Mention>> Submitted;

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

        // Mention tree, fetched once per picker-open and reused across keystrokes
        // (filtering is in-memory; no Revit collector per key). Dropped on close.
        private List<MentionNode> _tree;
        // Manual chevron state, keyed by node path. Auto-expand (query matches)
        // is recomputed per keystroke; _collapsedPaths lets the user re-collapse
        // an auto-expanded branch without the next rebuild reopening it.
        private readonly HashSet<string> _expandedPaths = new HashSet<string>();
        private readonly HashSet<string> _collapsedPaths = new HashSet<string>();

        // The flat search items, indexed from _tree once per opening.
        private List<MentionItem> _items;
        private Dictionary<MentionNode, MentionItem> _itemOf;
        // The picker's rows in order, and the one the arrows have reached.
        private readonly List<(Button row, Action activate)> _rows = new List<(Button, Action)>();
        private int _active = -1;
        private Point? _lastMouse;   // screen point of the last mouse move over a row

        // The chips in the box, kept in step with its text.
        private readonly MentionChips _chips = new MentionChips();
        private bool _settingText;   // our own text change: the chips are already moved
        private int _lastCaret;

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
            Editor.SelectionChanged += (_, __) => SnapCaretOutOfChip();
            Editor.SizeChanged += (_, __) => QueueDrawChips();
            Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, __) => QueueDrawChips()));
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
            if (!_settingText && e.Changes != null)
                foreach (var c in e.Changes) _chips.Edited(c.Offset, c.RemovedLength, c.AddedLength);
            _chips.Reconcile(Editor.Text);
            QueueDrawChips();
            UpdatePlaceholder();
            DetectToken();
        }

        // Set the box's text for a chip insert or removal (the chips are moved already).
        private void SetText(string text, int caret)
        {
            _settingText = true;
            try { Editor.Text = text; }
            finally { _settingText = false; }
            Editor.CaretIndex = Math.Min(Math.Max(caret, 0), Editor.Text.Length);
            _lastCaret = Editor.CaretIndex;
            QueueDrawChips();
        }

        // ─── Chips ───────────────────────────────────────────────────────────

        // The caret never rests inside a chip: it moves to the edge it was heading for.
        private void SnapCaretOutOfChip()
        {
            int caret = Editor.CaretIndex;
            if (Editor.SelectionLength == 0)
            {
                var chip = _chips.All.FirstOrDefault(c => caret > c.Start && caret < c.End);
                if (chip != null)
                {
                    int to = caret < _lastCaret ? chip.Start : chip.End;
                    _lastCaret = to;
                    Editor.CaretIndex = to;
                    return;
                }
            }
            _lastCaret = caret;
        }

        private bool _drawQueued;
        private void QueueDrawChips()
        {
            if (_drawQueued) return;
            _drawQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { _drawQueued = false; DrawChips(); }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // A tinted, rounded background under each chip's text, line by line (a long
        // chip can wrap). The TextBox is transparent, so the layer shows through.
        private void DrawChips()
        {
            if (ChipLayer == null) return;
            ChipLayer.Children.Clear();
            if (_chips.All.Count == 0) return;
            var text = Editor.Text ?? "";
            var fill = CopilotColors.Token("Cp.AccentWash");
            var edge = CopilotColors.Token("Cp.Accent");
            foreach (var chip in _chips.All)
            {
                if (chip.End > text.Length) continue;
                Rect line = Rect.Empty;
                for (int i = chip.Start; i < chip.End; i++)
                {
                    Rect a, b;
                    try { a = Editor.GetRectFromCharacterIndex(i); b = Editor.GetRectFromCharacterIndex(i, true); }
                    catch { return; }   // layout not ready; the next change redraws
                    if (a.IsEmpty || b.IsEmpty) return;
                    var r = new Rect(new Point(Math.Min(a.Left, b.Left), a.Top), new Point(Math.Max(a.Left, b.Left), a.Bottom));
                    if (!line.IsEmpty && Math.Abs(r.Top - line.Top) > 0.5) { AddChipRect(line, fill, edge); line = Rect.Empty; }
                    line = line.IsEmpty ? r : Rect.Union(line, r);
                }
                if (!line.IsEmpty) AddChipRect(line, fill, edge);
            }
        }

        private void AddChipRect(Rect r, Brush fill, Brush edge)
        {
            var b = new Border
            {
                Width = r.Width + 4, Height = r.Height, CornerRadius = new CornerRadius(4),
                Background = fill, BorderBrush = edge, BorderThickness = new Thickness(0, 0, 0, 1),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(b, r.Left - 2);
            Canvas.SetTop(b, r.Top);
            ChipLayer.Children.Add(b);
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

            // The "@" of a chip already in the box is not a new mention.
            if (at < 0 || _chips.Covers(at)) { ClosePicker(); return; }

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
                // Select and clear only the "/query", so chips elsewhere keep their place.
                Editor.Select(_slashIndex, caret - _slashIndex);
                Editor.SelectedText = "";
                Editor.CaretIndex = _slashIndex;
            }
            CloseSlash();
            Editor.Focus();
            ToolPicked?.Invoke(tool);
        }

        // Design popover (lines 502-512): ONE caps header, then either the
        // Project-Browser-style tree (nothing typed after "@": browse) or the flat,
        // ranked search list (letters typed). Pickable rows keep the sunken @-tile.
        private void BuildPicker(string query)
        {
            PickerHost.Children.Clear();
            _rows.Clear();
            if (_tree == null)
            {
                _tree = Provider?.GetTree() ?? new List<MentionNode>();
                _items = MentionSearch.Index(_tree);
                _itemOf = new Dictionary<MentionNode, MentionItem>();
                foreach (var it in _items) if (it.Node != null && !_itemOf.ContainsKey(it.Node)) _itemOf[it.Node] = it;
            }

            bool browse = string.IsNullOrWhiteSpace(query);
            PickerHost.Children.Add(new TextBlock
            {
                Text = browse ? "REFERENCE" : "REFERENCE · BEST MATCHES FIRST", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.Token("Cp.Faint"), Margin = new Thickness(8, 5, 8, 6),
            });

            if (browse)
            {
                var autoExpand = new HashSet<string>();
                var visible = Model.MentionTree.Filter(_tree, query, autoExpand);
                foreach (var n in visible) AddNodeRows(n, "", 0, autoExpand);
            }
            else
            {
                var text = Editor.Text ?? "";
                int caret = Math.Min(Math.Max(Editor.CaretIndex, _atIndex), text.Length);
                // The rest of the message: a category it names ranks that category first.
                string message = _atIndex >= 0 && _atIndex <= text.Length ? text.Remove(_atIndex, caret - _atIndex) : text;
                foreach (var it in MentionSearch.Search(_items, query, message))
                    AddItemRow(it);
            }

            // Arrows start on the first pickable row (the best match).
            _active = _rows.FindIndex(r => (r.row.Tag as MentionItem) != null || (r.row.Tag as MentionNode)?.Pickable == true);
            PaintActive();
            Picker.IsOpen = _rows.Count > 0;
        }

        private void AddNodeRows(MentionNode node, string parentPath, int depth, ISet<string> autoExpand)
        {
            string path = Model.MentionTree.PathOf(parentPath, node);
            bool expandable = node.Children.Count > 0;
            bool expanded = expandable && !_collapsedPaths.Contains(path)
                            && (_expandedPaths.Contains(path) || autoExpand.Contains(path));
            var row = NodeRow(node, path, depth, expandable, expanded);
            PickerHost.Children.Add(row);
            _rows.Add((row, () => { if (node.Pickable) InsertMention(ItemFor(node)); else TogglePath(path, expanded); }));
            if (!expanded) return;
            foreach (var child in node.Children) AddNodeRows(child, path, depth + 1, autoExpand);
        }

        private MentionItem ItemFor(MentionNode node) =>
            node != null && _itemOf != null && _itemOf.TryGetValue(node, out var it)
                ? it
                : new MentionItem { Kind = node?.Kind, Name = node?.Name, Node = node, Selection = node?.Selection };

        private Button NodeRow(MentionNode node, string path, int depth, bool expandable, bool expanded)
        {
            var row = NewRow(node, new Thickness(depth * 14, 0, 0, 0));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // chevron
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // @-tile
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // kind / count

            var chevron = new Border { Width = 16, Height = 22, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            if (expandable)
            {
                chevron.Child = new TextBlock { Text = expanded ? "▾" : "▸", FontSize = 10, Foreground = CopilotColors.Token("Cp.Faint"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                chevron.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePath(path, expanded); };
            }
            grid.Children.Add(chevron);

            if (node.Pickable)
            {
                var tile = AtTile();
                Grid.SetColumn(tile, 1);
                grid.Children.Add(tile);
            }

            var name = new TextBlock
            {
                Text = node.Name, FontSize = 12.5,
                FontWeight = node.Pickable ? FontWeights.Medium : FontWeights.SemiBold,
                Foreground = CopilotColors.Token(node.Pickable ? "Cp.Ink" : "Cp.Faint"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 2);
            grid.Children.Add(name);

            // Group headers show child count; a type shows its count; other rows the kind.
            string rightText = expandable && !node.Pickable ? node.Children.Count.ToString()
                : node.Kind == "type" && node.Count != null ? node.Count.ToString()
                : KindLabel(node.Kind);
            var right = new TextBlock
            {
                Text = rightText, FontSize = 10, Foreground = CopilotColors.Token("Cp.Faint"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(right, 3);
            grid.Children.Add(right);

            row.Content = grid;
            row.Click += (_, __) =>
            {
                if (node.Pickable) InsertMention(ItemFor(node));
                else TogglePath(path, expanded);
            };
            return row;
        }

        // A flat search row: @-tile, the name over a faint detail line
        // ("Pintu Tunggal · Doors · Mark D1"), and on the right the kind over the
        // count ("24 · 3 here": in the model · in the active view).
        private void AddItemRow(MentionItem item)
        {
            var row = NewRow(item, new Thickness(0));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(AtTile());

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = item.Kind == "selection" ? "Selection" : item.Name, FontSize = 12.5, FontWeight = FontWeights.Medium,
                Foreground = CopilotColors.Token("Cp.Ink"), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var detail = item.Detail;
            if (!string.IsNullOrEmpty(detail))
                left.Children.Add(new TextBlock
                {
                    Text = detail, FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0),
                    Foreground = CopilotColors.Token("Cp.Faint"), TextTrimming = TextTrimming.CharacterEllipsis,
                });
            Grid.SetColumn(left, 1);
            grid.Children.Add(left);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            right.Children.Add(new TextBlock
            {
                Text = item.KindLabel, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = CopilotColors.Token("Cp.Faint"),
            });
            if (!string.IsNullOrEmpty(item.CountText))
                right.Children.Add(new TextBlock
                {
                    Text = item.CountText, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = CopilotColors.Token("Cp.Muted"), ToolTip = "In the model · in the active view",
                });
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            row.Content = grid;
            row.Click += (_, __) => InsertMention(item);
            PickerHost.Children.Add(row);
            _rows.Add((row, () => InsertMention(item)));
        }

        private Button NewRow(object tag, Thickness margin)
        {
            var row = new Button
            {
                Cursor = Cursors.Hand, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(9, 8, 9, 8), Tag = tag, Margin = margin,
                Background = Brushes.Transparent, Focusable = false,
            };
            row.Template = RowTemplate();
            // The mouse moves the arrows' place too, so Enter picks what is under it;
            // but only a real move: the list opening under a resting pointer (or
            // rebuilding on a keystroke) must not take the place from the best match.
            row.MouseMove += (_, e) =>
            {
                Point p;
                try { p = row.PointToScreen(e.GetPosition(row)); } catch { return; }
                bool moved = _lastMouse.HasValue && (Math.Abs(p.X - _lastMouse.Value.X) > 1 || Math.Abs(p.Y - _lastMouse.Value.Y) > 1);
                _lastMouse = p;
                if (!moved) return;
                int i = _rows.FindIndex(r => r.row == row);
                if (i >= 0 && i != _active) { _active = i; PaintActive(); }
            };
            return row;
        }

        private static Border AtTile()
        {
            var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = CopilotColors.Token("Cp.Sunken"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            tile.Child = new TextBlock { Text = "@", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = CopilotColors.Token("Cp.Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return tile;
        }

        private void PaintActive()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                // A live resource reference so the highlight follows a theme switch.
                if (i == _active) _rows[i].row.SetResourceReference(BackgroundProperty, "Cp.Hover");
                else _rows[i].row.Background = Brushes.Transparent;
            }
            if (_active >= 0 && _active < _rows.Count) _rows[_active].row.BringIntoView();
        }

        private void MoveActive(int delta)
        {
            if (_rows.Count == 0) return;
            _active = _active < 0 ? 0 : (_active + delta + _rows.Count) % _rows.Count;
            PaintActive();
        }

        private static string KindLabel(string kind)
        {
            switch (kind)
            {
                case "level": return "Level";
                case "category": return "Category";
                case "family": return "Family";
                case "type": return "Type";
                case "view": return "View";
                case "sheet": return "Sheet";
                case "selection": return "Selection";
                default: return "";
            }
        }

        private void TogglePath(string path, bool expanded)
        {
            if (expanded) { _expandedPaths.Remove(path); _collapsedPaths.Add(path); }
            else { _collapsedPaths.Remove(path); _expandedPaths.Add(path); }
            int keep = _active;
            int at = Model.MentionToken.Find(Editor.Text ?? "", Editor.CaretIndex, out string query);
            if (at >= 0) { BuildPicker(query); _active = Math.Min(keep, _rows.Count - 1); PaintActive(); }
            else ClosePicker();
            Editor.Focus();
        }

        private void InsertMention(MentionItem item)
        {
            var text = Editor.Text ?? "";
            if (item == null || _atIndex < 0 || _atIndex > text.Length) { ClosePicker(); return; }
            var (next, caret) = _chips.Insert(text, _atIndex, Editor.CaretIndex, item);
            ClosePicker();
            SetText(next, caret);
            Editor.Focus();
        }

        private void ClosePicker()
        {
            _atIndex = -1;
            _tree = null;
            _items = null;
            _itemOf = null;
            _rows.Clear();
            _active = -1;
            _expandedPaths.Clear();
            _collapsedPaths.Clear();
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
                switch (e.Key)
                {
                    case Key.Escape: ClosePicker(); e.Handled = true; return;
                    case Key.Up: MoveActive(-1); e.Handled = true; return;
                    case Key.Down: MoveActive(1); e.Handled = true; return;
                    case Key.Enter:
                    case Key.Tab:
                        if (_active >= 0 && _active < _rows.Count)
                        {
                            _rows[_active].activate();   // pick, or open/close a group
                            e.Handled = true;
                            return;
                        }
                        break;
                }
            }

            // A chip is one unit: one Backspace or Delete removes it whole.
            if ((e.Key == Key.Back || e.Key == Key.Delete) && Editor.SelectionLength == 0 && _chips.All.Count > 0)
            {
                var text = Editor.Text ?? "";
                var chip = e.Key == Key.Back ? _chips.BackspaceTarget(text, Editor.CaretIndex) : _chips.DeleteTarget(Editor.CaretIndex);
                if (chip != null)
                {
                    var (next, caret) = _chips.Remove(text, chip);
                    SetText(next, caret);
                    e.Handled = true;
                    return;
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
            var shown = (Editor.Text ?? "").Trim();
            // Empty text still submits when a slash command is pending (AllowEmptySubmit)
            // so a bare "/tool" turn can be sent with no extra prose.
            if (string.IsNullOrEmpty(shown) && !AllowEmptySubmit) return;

            // A Selection chip sends what is selected NOW, read again from Revit.
            MentionSelection selNow = null;
            if (_chips.All.Any(c => c.Kind == "selection") && Provider is IMentionSelectionSource src)
            {
                try { selNow = src.ReadSelection(); } catch { selNow = null; }
            }
            var sent = _chips.Expand(Editor.Text ?? "", selNow).Trim();
            var mentions = _chips.Mentions(selNow).Select(m => new Mention(m.kind, m.sent)).ToList();
            Submitted?.Invoke(sent, shown, mentions);
            _chips.Clear();
            Editor.Clear();
            ClosePicker();
            CloseSlash();
        }

        private static ControlTemplate _rowTemplate;
        private static ControlTemplate RowTemplate()
        {
            if (_rowTemplate != null) return _rowTemplate;
            var b = new System.Windows.FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            b.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(Control.BackgroundProperty));
            b.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            b.Name = "bd";
            // No hover trigger: the one highlighted row is the active one (PaintActive),
            // which a real mouse move over a row also sets, so hover and arrows agree.
            var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            b.AppendChild(cp);
            return _rowTemplate = new ControlTemplate(typeof(Button)) { VisualTree = b };
        }
    }
}
