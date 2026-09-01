using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Saved Commands J1 — the Save/Edit sheet (design canvas "Save sheet"
    /// artboard, adapted to WPF). Docked to the pane's bottom inside ChatView's
    /// overlay layer. The drafter marks inputs by selecting text in the prompt
    /// (→ "Ask me for … each time") or tapping a suggested candidate chip; each
    /// hole renders as literal {name} in the editable prompt and gets a row in
    /// the INPUTS list (label / type / required / restore). Code-built like
    /// CommandPalette — no XAML, all colours via Cp.* resources.
    /// </summary>
    public class SaveCommandSheet : Border
    {
        private SavedCommandDraft _draft;
        private Func<SavedCommandDraft, Task<string>> _onSave;

        private readonly TextBlock _title;
        private readonly TextBox _nameBox;
        private readonly TextBlock _slugHint;
        private readonly TextBox _templateBox;
        private readonly RichTextBox _sentenceBox;
        private readonly TextBlock _wordingToggle;
        private readonly TextBlock _noCandsLine;
        private bool _wording;
        // (kind, template start, template end, name-for-holes) of every
        // rendered sentence part — selection offsets map through these.
        private List<(char Kind, int S, int E, string Name)> _parts =
            new List<(char, int, int, string)>();
        private (int S, int E)? _selSpan;
        private readonly Button _makeInputBtn;
        private readonly TextBlock _makeInputLabel;
        private readonly StackPanel _inputsHost;
        private readonly TextBlock _inputsHeader;
        private readonly Border _askRow;
        private readonly TextBlock _askLine;
        private readonly StackPanel _toolsHost;
        private readonly StackPanel _toolsSection;
        private readonly TextBlock _errorLine;
        private readonly Border _saveBtn;
        private readonly TextBlock _saveLabel;
        private bool _busy;

        public event Action Closed;

        // Candidate spans a drafter is likely to want to vary next time —
        // ported verbatim from the design canvas (CANDS).
        private static readonly Regex[] Cands =
        {
            new Regex(@"\b(?:Level|Aras|Storey|Floor|Tingkat)\s+[A-Za-z0-9-]+\b", RegexOptions.IgnoreCase),
            new Regex(@"\b(?:doors?|windows?|walls?|floors?|ceilings?|roofs?|columns?|stairs?|rooms?|pintu|dinding|tingkap|levels?|sheets?|views?|families|types?)\b", RegexOptions.IgnoreCase),
            new Regex(@"\b[A-Z]{1,3}-?\d{2,4}[a-z]?\b"),
            new Regex(@"\b\d+\s?(?:mm|m|cm|ft|in)\b"),
            new Regex(@"\b\d{2,4}\s?[x×]\s?\d{2,4}\b"),
            new Regex(@"\b\d+\b"),
        };

        public SaveCommandSheet()
        {
            CornerRadius = new CornerRadius(14);
            BorderThickness = new Thickness(1);
            SetResourceReference(BackgroundProperty, "Cp.PanelBg");
            SetResourceReference(BorderBrushProperty, "Cp.Line");
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, Opacity = 0.16, BlurRadius = 24, ShadowDepth = 6, Direction = 270 };
            Visibility = Visibility.Collapsed;
            Focusable = true;

            var outer = new DockPanel { LastChildFill = true };
            Child = outer;

            // ── Header ──────────────────────────────────────────────────────
            var header = new Grid { Margin = new Thickness(14, 12, 12, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(6),
                Background = CopilotColors.From("#292A69C6"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = CommandPalette.IconEl("ti-device-floppy", 12, "Cp.Accent"),
            };
            header.Children.Add(tile);
            _title = new TextBlock
            {
                Text = "Save as command", FontSize = 13,
                FontWeight = FontWeight.FromOpenTypeWeight(600),
                Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            _title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            Grid.SetColumn(_title, 1);
            header.Children.Add(_title);
            var close = IconButton("×", () => Cancel());
            Grid.SetColumn(close, 2);
            header.Children.Add(close);
            var headerWrap = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1), Child = header,
            };
            headerWrap.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            DockPanel.SetDock(headerWrap, Dock.Top);
            outer.Children.Add(headerWrap);

            // ── Footer ──────────────────────────────────────────────────────
            var footerStack = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            _errorLine = new TextBlock
            {
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed,
            };
            _errorLine.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Red");
            footerStack.Children.Add(_errorLine);
            var footerRow = new Grid();
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cancel = new TextBlock
            {
                Text = "Cancel", FontSize = 12.5, FontWeight = FontWeight.FromOpenTypeWeight(600),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Padding = new Thickness(4, 6, 8, 6),
            };
            cancel.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            cancel.MouseLeftButtonUp += (_, __) => Cancel();
            footerRow.Children.Add(cancel);
            _saveLabel = new TextBlock
            {
                Text = "Save command", FontSize = 12.5,
                FontWeight = FontWeight.FromOpenTypeWeight(600), Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            _saveBtn = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(15, 8, 15, 8),
                Cursor = Cursors.Hand, Child = _saveLabel, VerticalAlignment = VerticalAlignment.Center,
            };
            _saveBtn.SetResourceReference(Border.BackgroundProperty, "Cp.AccentGrad");
            _saveBtn.MouseLeftButtonUp += async (_, __) => await CommitAsync();
            Grid.SetColumn(_saveBtn, 2);
            footerRow.Children.Add(_saveBtn);
            footerStack.Children.Add(footerRow);
            var footerWrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = footerStack };
            footerWrap.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            footerWrap.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            DockPanel.SetDock(footerWrap, Dock.Bottom);
            outer.Children.Add(footerWrap);

            // ── Body (scrolls) ──────────────────────────────────────────────
            var body = new StackPanel { Margin = new Thickness(14, 14, 14, 14) };
            var bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
            };
            outer.Children.Add(bodyScroll);

            // Hint + "Edit wording" toggle (design row).
            var hintRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            hintRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hintRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var hint = new TextBlock
            {
                Text = "Tap anything that will be different next time, or select any words and blank them out.",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            hintRow.Children.Add(hint);
            _wordingToggle = new TextBlock
            {
                Text = "Edit wording", FontSize = 11.5,
                FontWeight = FontWeight.FromOpenTypeWeight(600),
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 0),
                Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline,
            };
            _wordingToggle.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
            _wordingToggle.MouseLeftButtonUp += (_, __) => ToggleWording();
            Grid.SetColumn(_wordingToggle, 1);
            hintRow.Children.Add(_wordingToggle);
            body.Children.Add(hintRow);

            // The tappable sentence (design): plain runs, dashed-underlined
            // candidate runs (tap to blank), and hole CHIPS showing the label
            // (tap to undo). A read-only RichTextBox so arbitrary words can
            // also be selected → "Ask me for … each time".
            _sentenceBox = new RichTextBox
            {
                IsReadOnly = true, IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(1), FontSize = 14,
                Padding = new Thickness(9, 8, 9, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 170,
            };
            _sentenceBox.SetResourceReference(RichTextBox.BackgroundProperty, "Cp.Bg");
            _sentenceBox.SetResourceReference(RichTextBox.ForegroundProperty, "Cp.Text");
            _sentenceBox.SetResourceReference(RichTextBox.BorderBrushProperty, "Cp.Line");
            _sentenceBox.SelectionChanged += (_, __) => UpdateMakeInput();
            body.Children.Add(_sentenceBox);

            // Raw wording editor — swapped in by the toggle (design textarea).
            _templateBox = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5, Padding = new Thickness(11, 9, 11, 9),
                BorderThickness = new Thickness(1), MinHeight = 56, MaxHeight = 140,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Visibility = Visibility.Collapsed,
            };
            _templateBox.SetResourceReference(TextBox.BackgroundProperty, "Cp.Bg");
            _templateBox.SetResourceReference(TextBox.ForegroundProperty, "Cp.Text");
            _templateBox.BorderBrush = CopilotColors.From("#572A69C6");
            _templateBox.SetResourceReference(TextBox.CaretBrushProperty, "Cp.Accent");
            _templateBox.TextChanged += (_, __) => ClearError();
            body.Children.Add(_templateBox);

            // "Ask me for X each time" — shows while a selection exists.
            _makeInputLabel = new TextBlock
            {
                FontSize = 12, FontWeight = FontWeight.FromOpenTypeWeight(600),
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            };
            _makeInputBtn = new Button
            {
                Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                FocusVisualStyle = null, Focusable = false,
            };
            var mkBd = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 7),
                Child = _makeInputLabel,
            };
            mkBd.SetResourceReference(Border.BackgroundProperty, "Cp.Accent");
            _makeInputBtn.Content = mkBd;
            _makeInputBtn.Click += (_, __) => MakeInputFromSelection();
            body.Children.Add(_makeInputBtn);

            // "Nothing here looks like it varies…" (design noCands).
            _noCandsLine = new TextBlock
            {
                Text = "Nothing here looks like it varies. Select the words that should change — a level, a size, a sheet number — and Copilot will ask you for them instead.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
            };
            _noCandsLine.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            body.Children.Add(_noCandsLine);

            // ── Inputs list ─────────────────────────────────────────────────
            _inputsHeader = Kicker("INPUTS");
            _inputsHeader.Margin = new Thickness(0, 14, 0, 6);
            body.Children.Add(_inputsHeader);
            _inputsHost = new StackPanel();
            body.Children.Add(_inputsHost);
            _askLine = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            _askLine.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Text");
            _askRow = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 8, 0, 0), Background = CopilotColors.From("#142A69C6"),
                Child = _askLine, Visibility = Visibility.Collapsed,
            };
            body.Children.Add(_askRow);

            // ── Name + slug ─────────────────────────────────────────────────
            var nameKick = Kicker("NAME");
            nameKick.Margin = new Thickness(0, 14, 0, 6);
            body.Children.Add(nameKick);
            _nameBox = new TextBox
            {
                FontSize = 13, Padding = new Thickness(11, 8, 11, 8), BorderThickness = new Thickness(1),
            };
            _nameBox.SetResourceReference(TextBox.BackgroundProperty, "Cp.Bg");
            _nameBox.SetResourceReference(TextBox.ForegroundProperty, "Cp.Text");
            _nameBox.SetResourceReference(TextBox.BorderBrushProperty, "Cp.Line");
            _nameBox.SetResourceReference(TextBox.CaretBrushProperty, "Cp.Accent");
            _nameBox.TextChanged += (_, __) => { _draft.Name = _nameBox.Text; UpdateSlugHint(); ClearError(); };
            _nameBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await CommitAsync(); };
            body.Children.Add(_nameBox);
            _slugHint = new TextBlock { FontSize = 11, Margin = new Thickness(1, 4, 0, 0) };
            _slugHint.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            body.Children.Add(_slugHint);

            // ── Tools used ──────────────────────────────────────────────────
            _toolsSection = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            body.Children.Add(_toolsSection);
            var toolsKick = Kicker("TOOLS USED");
            toolsKick.Margin = new Thickness(0, 0, 0, 6);
            _toolsSection.Children.Add(toolsKick);
            _toolsHost = new StackPanel();
            _toolsSection.Children.Add(_toolsHost);
            var toolsLine = new TextBlock
            {
                Text = "Re-runs stay inside this list, so you get the same kind of result as today rather than a fresh interpretation.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            };
            toolsLine.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            _toolsSection.Children.Add(toolsLine);

            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancel(); e.Handled = true; } };
        }

        // ── Public API ───────────────────────────────────────────────────────

        public void Show(SavedCommandDraft d, Func<SavedCommandDraft, Task<string>> onSave)
        {
            _draft = d ?? new SavedCommandDraft();
            _onSave = onSave;
            _busy = false;
            _wording = false;
            _selSpan = null;
            _templateBox.Visibility = Visibility.Collapsed;
            _sentenceBox.Visibility = Visibility.Visible;
            _wordingToggle.Text = "Edit wording";
            _title.Text = _draft.EditingId == null ? "Save this as a command" : "Edit this command";
            _toolsSection.Visibility = _draft.EditingId == null && _draft.ToolsCalled.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            Render();
            ClearError();
            SetBusy(false);
            Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => { _nameBox.Focus(); _nameBox.SelectAll(); }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        public void Hide() { Visibility = Visibility.Collapsed; }

        private void Cancel() { Hide(); Closed?.Invoke(); }

        // ── Rendering ────────────────────────────────────────────────────────

        private void Render()
        {
            _nameBox.Text = _draft.Name;
            UpdateSlugHint();
            RenderSentence();
            RenderInputs();
            RenderTools();
            UpdateMakeInput();
        }

        private void ToggleWording()
        {
            if (_wording)
            {
                // Done editing — the textarea's text becomes the template.
                _draft.Template = _templateBox.Text;
                _wording = false;
                _templateBox.Visibility = Visibility.Collapsed;
                _sentenceBox.Visibility = Visibility.Visible;
                _wordingToggle.Text = "Edit wording";
                Render();
            }
            else
            {
                _wording = true;
                _templateBox.Text = _draft.Template;
                _sentenceBox.Visibility = Visibility.Collapsed;
                _templateBox.Visibility = Visibility.Visible;
                _wordingToggle.Text = "Done editing";
                _makeInputBtn.Visibility = Visibility.Collapsed;
                _noCandsLine.Visibility = Visibility.Collapsed;
                _templateBox.Focus();
            }
            ClearError();
        }

        private void UpdateSlugHint()
        {
            _slugHint.Text = "Find it later by typing /" + SavedCommandDraft.SuggestSlug(_nameBox.Text);
        }

        // ── Sentence rendering (design sheet.sentence) ───────────────────────

        /// <summary>Split the template into plain / candidate / hole parts with
        /// template offsets — the design's parts builder, verbatim logic.</summary>
        private List<(char Kind, int S, int E, string Name)> BuildParts(string template)
        {
            var parts = new List<(char, int, int, string)>();
            var holeRe = new Regex(@"\{([a-z][a-z0-9_]*)\}");
            int at = 0;

            void PushPlain(int from, int to)
            {
                if (to <= from) return;
                var txt = template.Substring(from, to - from);
                var cands = new List<(int s, int e)>();
                foreach (var re in Cands)
                    foreach (Match c in re.Matches(txt))
                        if (!cands.Any(x => c.Index < x.e && x.s < c.Index + c.Length))
                            cands.Add((c.Index, c.Index + c.Length));
                int cur = from;
                foreach (var c in cands.OrderBy(x => x.s))
                {
                    if (from + c.s > cur) parts.Add(('p', cur, from + c.s, null));
                    parts.Add(('c', from + c.s, from + c.e, null));
                    cur = from + c.e;
                }
                if (cur < to) parts.Add(('p', cur, to, null));
            }

            foreach (Match m in holeRe.Matches(template))
            {
                PushPlain(at, m.Index);
                parts.Add(('h', m.Index, m.Index + m.Length, m.Groups[1].Value));
                at = m.Index + m.Length;
            }
            PushPlain(at, template.Length);
            return parts;
        }

        private void RenderSentence()
        {
            var template = _draft.Template ?? "";
            _parts = BuildParts(template);
            var para = new Paragraph { LineHeight = 26, Margin = new Thickness(0) };
            bool anyCand = false;
            foreach (var part in _parts)
            {
                if (part.Kind == 'h')
                {
                    var input = _draft.Inputs.FirstOrDefault(i => i.Name == part.Name);
                    var chipText = new TextBlock
                    {
                        Text = input?.Label ?? part.Name, FontSize = 13,
                        FontWeight = FontWeight.FromOpenTypeWeight(600),
                    };
                    chipText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
                    var chip = new Border
                    {
                        CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 1, 8, 2),
                        Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand,
                        Background = CopilotColors.From("#262A69C6"),
                        BorderBrush = CopilotColors.From("#612A69C6"),
                        BorderThickness = new Thickness(1),
                        Child = chipText, ToolTip = "Tap to put the original words back",
                    };
                    var nm = part.Name;
                    chip.MouseLeftButtonUp += (_, e) =>
                    { e.Handled = true; _draft.UnmarkInput(nm); Render(); };
                    para.Inlines.Add(new InlineUIContainer(chip)
                    { BaselineAlignment = BaselineAlignment.Center });
                    continue;
                }
                var run = new Run(template.Substring(part.S, part.E - part.S)) { Tag = part.S };
                if (part.Kind == 'c')
                {
                    anyCand = true;
                    run.Cursor = Cursors.Hand;
                    run.ToolTip = "Tap to blank this out";
                    // 1.5px dashed accent underline (design candStyle).
                    var pen = new Pen(CopilotColors.From("#8C2A69C6"), 1.5)
                    { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
                    var deco = new TextDecoration
                    { Location = TextDecorationLocation.Underline, Pen = pen, PenOffset = 2 };
                    run.TextDecorations = new TextDecorationCollection { deco };
                    var span = part;
                    run.MouseLeftButtonUp += (_, e) =>
                    {
                        // A drag-selection release also lands here — only treat
                        // it as a tap when nothing is selected.
                        if (!_sentenceBox.Selection.IsEmpty) return;
                        e.Handled = true;
                        var nm2 = _draft.AutoName(template.Substring(span.S, span.E - span.S));
                        if (!_draft.MarkInput(span.S, span.E - span.S, nm2, out var err)) { ShowError(err); return; }
                        Render();
                    };
                }
                para.Inlines.Add(run);
            }
            var doc = new FlowDocument(para) { PagePadding = new Thickness(0) };
            _sentenceBox.Document = doc;
            _noCandsLine.Visibility = !_wording && !anyCand && _draft.Inputs.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Map the RichTextBox selection back to template offsets via
        /// the run Tags; null when empty, crossing a hole, or unmappable.</summary>
        private (int S, int E)? SelectionSpan()
        {
            var sel = _sentenceBox.Selection;
            if (sel == null || sel.IsEmpty) return null;
            int? MapPoint(TextPointer p, bool end)
            {
                var run = p.Parent as Run;
                if (run == null)
                {
                    // Paragraph/container boundary — snap into the adjacent run.
                    var ins = p.GetInsertionPosition(end ? LogicalDirection.Backward : LogicalDirection.Forward);
                    run = ins?.Parent as Run;
                    p = ins;
                    if (run == null) return null;
                }
                if (!(run.Tag is int start)) return null;   // hole chips carry no tag
                return start + run.ContentStart.GetOffsetToPosition(p);
            }
            var a = MapPoint(sel.Start, end: false);
            var b = MapPoint(sel.End, end: true);
            if (a == null || b == null) return null;
            int s = Math.Min(a.Value, b.Value), e = Math.Max(a.Value, b.Value);
            // trim whitespace the drag picked up
            var template = _draft.Template ?? "";
            while (s < e && char.IsWhiteSpace(template[s])) s++;
            while (e > s && char.IsWhiteSpace(template[e - 1])) e--;
            if (e <= s) return null;
            // crossing an existing hole → not blankable (design error case)
            foreach (var part in _parts)
                if (part.Kind == 'h' && s < part.E && part.S < e) return null;
            return (s, e);
        }

        private void UpdateMakeInput()
        {
            if (_wording) { _makeInputBtn.Visibility = Visibility.Collapsed; return; }
            _selSpan = SelectionSpan();
            bool show = _selSpan != null;
            _makeInputBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                var t = (_draft.Template ?? "").Substring(_selSpan.Value.S, _selSpan.Value.E - _selSpan.Value.S).Trim();
                if (t.Length > 24) t = t.Substring(0, 24) + "…";
                _makeInputLabel.Text = "Ask me for “" + t + "” each time";
            }
        }

        private void MakeInputFromSelection()
        {
            var span = _selSpan;
            if (span == null) return;
            var name = _draft.AutoName((_draft.Template ?? "").Substring(span.Value.S, span.Value.E - span.Value.S));
            if (!_draft.MarkInput(span.Value.S, span.Value.E - span.Value.S, name, out var err)) { ShowError(err); return; }
            _selSpan = null;
            Render();
        }

        private void RenderInputs()
        {
            _inputsHost.Children.Clear();
            bool any = _draft.Inputs.Count > 0;
            _inputsHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _inputsHeader.Text = "INPUTS · " + _draft.Inputs.Count;
            _askRow.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any)
                _askLine.Text = "Each time you run this, Copilot asks you for "
                    + string.Join(", ", _draft.Inputs.Select(i => i.Label ?? i.Name))
                    + " — everything else stays as written.";
            foreach (var input in _draft.Inputs)
            {
                // Design input row: label field · type pill · required pill · ×
                // (no raw {name} — that's tech detail the sheet keeps hidden).
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var labelBox = new TextBox
                {
                    Text = input.Label ?? "", FontSize = 12.5,
                    Padding = new Thickness(7, 4, 7, 4), BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                labelBox.SetResourceReference(TextBox.BackgroundProperty, "Cp.PanelBg");
                labelBox.SetResourceReference(TextBox.ForegroundProperty, "Cp.Text");
                labelBox.SetResourceReference(TextBox.BorderBrushProperty, "Cp.Line");
                var inp = input;
                labelBox.TextChanged += (_, __) => inp.Label = labelBox.Text;
                Grid.SetColumn(labelBox, 0);
                row.Children.Add(labelBox);

                var typeBtn = PillButton(inp.Type == "number" ? "Number" : "Text", () =>
                {
                    inp.Type = inp.Type == "number" ? "text" : "number";
                    RenderInputs();
                });
                Grid.SetColumn(typeBtn, 1);
                row.Children.Add(typeBtn);

                var reqBtn = PillButton(inp.Required ? "Required" : "Optional", () =>
                {
                    inp.Required = !inp.Required;
                    RenderInputs();
                }, inp.Required);
                Grid.SetColumn(reqBtn, 2);
                row.Children.Add(reqBtn);

                var remove = IconButton("×", () =>
                {
                    _draft.UnmarkInput(inp.Name);
                    Render();
                });
                remove.ToolTip = "Put the original words back";
                Grid.SetColumn(remove, 3);
                row.Children.Add(remove);

                var rowWrap = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 7, 7, 7),
                    BorderThickness = new Thickness(1), Child = row,
                    Margin = new Thickness(0, 0, 0, 0),
                };
                rowWrap.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
                rowWrap.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                _inputsHost.Children.Add(rowWrap);
            }
        }

        private void RenderTools()
        {
            _toolsHost.Children.Clear();
            var wrap = new WrapPanel();
            _toolsHost.Children.Add(wrap);
            foreach (var t in _draft.ToolsCalled)
            {
                var chipText = new TextBlock { Text = t, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
                chipText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                chipText.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(0, 0, 5, 5), BorderThickness = new Thickness(1), Child = chipText,
                };
                chip.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
                chip.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                wrap.Children.Add(chip);
            }
        }

        // ── Commit ───────────────────────────────────────────────────────────

        private async Task CommitAsync()
        {
            if (_busy || _onSave == null) return;
            _draft.Name = _nameBox.Text;
            if (_wording) _draft.Template = _templateBox.Text;   // sentence mode edits the draft directly
            if (string.IsNullOrWhiteSpace(_draft.Name)) { ShowError("Give the command a name."); return; }
            var orphan = _draft.Inputs.FirstOrDefault(i => (_draft.Template ?? "").IndexOf("{" + i.Name + "}", StringComparison.Ordinal) < 0);
            if (orphan != null)
            {
                ShowError("The blank for “" + (orphan.Label ?? orphan.Name)
                    + "” is no longer in the wording. Remove it below, or put those words back.");
                return;
            }
            SetBusy(true);
            string err;
            try { err = await _onSave(_draft); }
            catch (Exception ex) { err = "Could not save: " + ex.Message; }
            SetBusy(false);
            if (err != null) { ShowError(err); return; }
            Hide();
            Closed?.Invoke();
        }

        // ── Small helpers ────────────────────────────────────────────────────

        private void SetBusy(bool on)
        {
            _busy = on;
            _saveLabel.Text = on ? "Saving…" : "Save command";
            _saveBtn.Opacity = on ? 0.55 : 1.0;
            _saveBtn.IsHitTestVisible = !on;
        }

        private void ShowError(string msg)
        {
            _errorLine.Text = msg ?? "";
            _errorLine.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ClearError() => ShowError(null);

        private static TextBlock Kicker(string text)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            return t;
        }

        private FrameworkElement PillButton(string label, Action onClick, bool active = false)
        {
            var t = new TextBlock
            {
                Text = label, FontSize = 10.5, FontWeight = FontWeight.FromOpenTypeWeight(600),
                VerticalAlignment = VerticalAlignment.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, active ? "Cp.BlueText" : "Cp.Muted");
            var bd = new Border
            {
                CornerRadius = new CornerRadius(99), Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, Child = t, VerticalAlignment = VerticalAlignment.Center,
            };
            bd.SetResourceReference(Border.BorderBrushProperty, active ? "Cp.PurpleLine" : "Cp.Line");
            if (active) bd.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
            bd.MouseLeftButtonUp += (_, __) => onClick();
            return bd;
        }

        private FrameworkElement IconButton(string glyph, Action onClick)
        {
            var t = new TextBlock
            {
                Text = glyph, FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            var bd = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(7),
                Cursor = Cursors.Hand, Child = t, Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            bd.MouseLeftButtonUp += (_, __) => onClick();
            return bd;
        }
    }
}
