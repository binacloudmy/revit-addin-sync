using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        private readonly StackPanel _candidateRow;
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

            var hint = new TextBlock
            {
                Text = "Select the words that will be different next time — a level, a size, a sheet number — and Copilot will ask you for them instead.",
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            body.Children.Add(hint);

            _templateBox = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                FontSize = 13.5, Padding = new Thickness(11, 9, 11, 9),
                BorderThickness = new Thickness(1), MinHeight = 56, MaxHeight = 140,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            _templateBox.SetResourceReference(TextBox.BackgroundProperty, "Cp.Bg");
            _templateBox.SetResourceReference(TextBox.ForegroundProperty, "Cp.Text");
            _templateBox.SetResourceReference(TextBox.BorderBrushProperty, "Cp.Line");
            _templateBox.SetResourceReference(TextBox.CaretBrushProperty, "Cp.Accent");
            _templateBox.SelectionChanged += (_, __) => UpdateMakeInput();
            _templateBox.TextChanged += (_, __) => { _draft.Template = _templateBox.Text; ClearError(); };
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

            // Suggested candidates (design CANDS): one tap blanks the span.
            _candidateRow = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(_candidateRow);

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
            _title.Text = _draft.EditingId == null ? "Save as command" : "Edit command";
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
            _templateBox.Text = _draft.Template;
            _nameBox.Text = _draft.Name;
            UpdateSlugHint();
            RenderCandidates();
            RenderInputs();
            RenderTools();
            UpdateMakeInput();
        }

        private void UpdateSlugHint()
        {
            _slugHint.Text = "Find it later by typing " + SavedCommandDraft.SuggestSlug(_nameBox.Text);
        }

        private void UpdateMakeInput()
        {
            var sel = _templateBox.SelectedText ?? "";
            bool show = sel.Trim().Length > 0 && !sel.Contains("{") && !sel.Contains("}");
            _makeInputBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                var t = sel.Trim();
                if (t.Length > 24) t = t.Substring(0, 24) + "…";
                _makeInputLabel.Text = "Ask me for “" + t + "” each time";
            }
        }

        private void MakeInputFromSelection()
        {
            var start = _templateBox.SelectionStart;
            var len = _templateBox.SelectionLength;
            if (len <= 0) return;
            _draft.Template = _templateBox.Text;
            var name = _draft.AutoName(_templateBox.SelectedText);
            if (!_draft.MarkInput(start, len, name, out var err)) { ShowError(err); return; }
            Render();
        }

        private void RenderCandidates()
        {
            _candidateRow.Children.Clear();
            var text = _draft.Template ?? "";
            var spans = new List<(int s, int e, string t)>();
            foreach (var re in Cands)
                foreach (Match m in re.Matches(text))
                {
                    if (m.Value.Contains("{") || m.Value.Contains("}")) continue;
                    // skip anything inside an existing hole
                    bool inHole = Regex.Matches(text, @"\{[a-z][a-z0-9_]*\}").Cast<Match>()
                        .Any(h => m.Index < h.Index + h.Length && h.Index < m.Index + m.Length);
                    if (inHole) continue;
                    if (!spans.Any(c => m.Index < c.e && c.s < m.Index + m.Length))
                        spans.Add((m.Index, m.Index + m.Length, m.Value));
                }
            spans = spans.OrderBy(c => c.s).Take(6).ToList();
            if (spans.Count == 0) { _candidateRow.Visibility = Visibility.Collapsed; return; }
            _candidateRow.Visibility = Visibility.Visible;
            var label = new TextBlock
            {
                Text = "Likely to change next time — tap to blank out:",
                FontSize = 11, Margin = new Thickness(0, 0, 0, 5),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            _candidateRow.Children.Add(label);
            var wrap = new WrapPanel();
            _candidateRow.Children.Add(wrap);
            foreach (var c in spans)
            {
                var chipText = new TextBlock
                {
                    Text = c.t, FontSize = 11.5, FontWeight = FontWeight.FromOpenTypeWeight(600),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                chipText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 4, 9, 4),
                    Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Child = chipText,
                    BorderThickness = new Thickness(1),
                };
                chip.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
                chip.SetResourceReference(Border.BorderBrushProperty, "Cp.PurpleLine");
                var span = c;
                chip.MouseLeftButtonUp += (_, __) =>
                {
                    _draft.Template = _templateBox.Text;
                    var idx = (_draft.Template ?? "").IndexOf(span.t, StringComparison.Ordinal);
                    if (idx < 0) { RenderCandidates(); return; }
                    var nm = _draft.AutoName(span.t);
                    if (!_draft.MarkInput(idx, span.t.Length, nm, out var err)) { ShowError(err); return; }
                    Render();
                };
                wrap.Children.Add(chip);
            }
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
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var holeName = new TextBlock
                {
                    Text = "{" + input.Name + "}", FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                };
                holeName.SetResourceReference(TextBlock.ForegroundProperty, "Cp.CodeFg");
                holeName.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
                row.Children.Add(holeName);

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
                Grid.SetColumn(labelBox, 1);
                row.Children.Add(labelBox);

                var typeBtn = PillButton(inp.Type == "number" ? "Number" : "Text", () =>
                {
                    inp.Type = inp.Type == "number" ? "text" : "number";
                    RenderInputs();
                });
                Grid.SetColumn(typeBtn, 2);
                row.Children.Add(typeBtn);

                var reqBtn = PillButton(inp.Required ? "Required" : "Optional", () =>
                {
                    inp.Required = !inp.Required;
                    RenderInputs();
                }, inp.Required);
                Grid.SetColumn(reqBtn, 3);
                row.Children.Add(reqBtn);

                var remove = IconButton("×", () =>
                {
                    _draft.Template = _templateBox.Text;
                    _draft.UnmarkInput(inp.Name);
                    Render();
                });
                remove.ToolTip = "Put the original words back";
                Grid.SetColumn(remove, 4);
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
            _draft.Template = _templateBox.Text;
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
