using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitWebAppSync.Helpers
{
    /// <summary>
    /// Lightweight markdown → WPF converter for the Copilot chat bubbles.
    /// Light-themed (dark text on white). Supports headers, **bold**,
    /// *italic*, `code`, ```code blocks```, bullet + numbered lists,
    /// > blockquotes, [links](url), and GitHub-style | tables |.
    ///
    /// Renders into a FlowDocument hosted in a read-only borderless
    /// RichTextBox so the user can HIGHLIGHT AND COPY any part of an AI
    /// reply (a plain TextBlock cannot be selected). Tables keep their
    /// auto-sizing Grid look via BlockUIContainer (not text-selectable —
    /// the bubble's ⧉ copy button covers them).
    /// </summary>
    public static class MarkdownRenderer
    {
        // Palette resolved through CopilotColors so DARK MODE maps each light hex
        // to its Slate equivalent AT RENDER TIME (ChatView re-renders replies on
        // ThemeChanged). These were `static readonly` light-only brushes before,
        // which left AI prose near-invisible (#131c2b on #131d2b) in dark mode.
        private static Brush Ink     => UI.Copilot.Controls.CopilotColors.From("#131c2b"); // headers
        private static Brush Text    => UI.Copilot.Controls.CopilotColors.From("#131c2b"); // body (design --text)
        private static Brush Muted   => UI.Copilot.Controls.CopilotColors.From("#586273"); // quotes/citations
        private static Brush Accent  => UI.Copilot.Controls.CopilotColors.From("#1d4ed8"); // bullets/links
        private static Brush Line    => UI.Copilot.Controls.CopilotColors.From("#290F1B2D"); // table borders
        private static Brush CodeBg  => UI.Copilot.Controls.CopilotColors.From("#f3f6f9");
        private static Brush CodeFg  => UI.Copilot.Controls.CopilotColors.From("#1e40af");
        private static Brush BlockBg => UI.Copilot.Controls.CopilotColors.From("#f6f8fa");
        private static Brush CellBg  => UI.Copilot.Controls.CopilotColors.From("#ffffff"); // table data-cell surface (dark → #1a2433)
        private static Brush IdLinkHoverBg => UI.Copilot.Controls.CopilotColors.From("#dbeafe"); // clickable-id hover chip
        private static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono, Consolas, monospace");

        // Ambient FlowDocument font size (Render sets doc.FontSize to this). Runs
        // inherit it through the WPF property tree, but EmojiInline must be given
        // it EXPLICITLY at construction — see AppendTextWithEmoji remarks.
        private const double BodyFontSize = 12.5;

        /// <summary>Raised when the drafter clicks a detected element-id table cell
        /// or a `bina://select/&lt;id&gt;` inline link. ChatView subscribes ONCE
        /// (guarded — this event is static and bubbles are rebuilt per message/
        /// theme-flip) and runs the addin's own `select_elements` tool locally
        /// (no backend round-trip) to select + zoom the element in Revit.</summary>
        public static event Action<long> ElementIdClicked;

        public static FrameworkElement Render(string markdown, double maxWidth = 350)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = BodyFontSize,
                Foreground = Text,
            };

            if (!string.IsNullOrWhiteSpace(markdown))
                BuildBlocks(doc.Blocks, markdown, maxWidth);

            var box = new RichTextBox
            {
                Document = doc,
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                MaxWidth = maxWidth,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            // TextBoxBase swallows the mouse wheel even when it has nothing to
            // scroll — re-raise it as a bubbling event so the CHAT scrolls when
            // the pointer happens to sit over a reply.
            box.PreviewMouseWheel += (s, e) =>
            {
                if (e.Handled) return;
                e.Handled = true;
                box.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = s,
                });
            };
            return box;
        }

        private static void BuildBlocks(BlockCollection blocks, string markdown, double maxWidth)
        {
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCode = false;
            var codeLines = new List<string>();
            // A blank markdown line becomes extra top-margin on the NEXT block
            // (an empty FlowDocument paragraph would render a full line tall).
            double pendingSpace = 0;

            void Add(Block b)
            {
                if (pendingSpace > 0)
                {
                    b.Margin = new Thickness(b.Margin.Left, b.Margin.Top + pendingSpace, b.Margin.Right, b.Margin.Bottom);
                    pendingSpace = 0;
                }
                blocks.Add(b);
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // ``` fenced code block toggle
                if (trimmed.StartsWith("```"))
                {
                    if (inCode) { Add(CodeBlock(string.Join("\n", codeLines))); codeLines.Clear(); inCode = false; }
                    else inCode = true;
                    continue;
                }
                if (inCode) { codeLines.Add(line); continue; }

                // | table | — gather the contiguous block of pipe rows
                if (IsTableRow(trimmed))
                {
                    var rows = new List<string>();
                    while (i < lines.Length && IsTableRow(lines[i].TrimStart()))
                        rows.Add(lines[i++].Trim());
                    i--; // step back; for-loop will advance
                    var table = TableBlock(rows, maxWidth);
                    if (table != null) Add(new BlockUIContainer(table) { Margin = new Thickness(0, 4, 0, 4) });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed)) { pendingSpace = 6; continue; }

                if (trimmed.StartsWith("### ")) { Add(Header(trimmed.Substring(4), 13)); continue; }
                if (trimmed.StartsWith("## "))  { Add(Header(trimmed.Substring(3), 14)); continue; }
                if (trimmed.StartsWith("# "))   { Add(Header(trimmed.Substring(2), 15)); continue; }

                if (trimmed.StartsWith("> "))   { Add(Blockquote(trimmed.Substring(2))); continue; }

                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                { Add(ListItem("•", trimmed.Substring(2))); continue; }

                var numbered = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
                if (numbered.Success)
                { Add(ListItem(numbered.Groups[1].Value + ".", numbered.Groups[2].Value)); continue; }

                // paragraph
                var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1), LineHeight = 18 };
                AddInlines(p.Inlines, trimmed, BodyFontSize);
                Add(p);
            }

            if (inCode && codeLines.Count > 0) Add(CodeBlock(string.Join("\n", codeLines)));
        }

        private static Paragraph Header(string text, double fontSize)
        {
            var p = new Paragraph
            {
                Foreground = Ink, FontSize = fontSize, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 2),
            };
            AddInlines(p.Inlines, text, fontSize);
            return p;
        }

        private static Paragraph ListItem(string marker, string text)
        {
            var p = new Paragraph { Margin = new Thickness(8, 1, 0, 1) };
            p.Inlines.Add(new Run(marker + "  ") { Foreground = Accent });
            AddInlines(p.Inlines, text, BodyFontSize);
            return p;
        }

        private static Paragraph Blockquote(string text)
        {
            var p = new Paragraph
            {
                Foreground = Muted, FontSize = 12,
                Background = BlockBg, BorderBrush = Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 3, 0, 3),
            };
            AddInlines(p.Inlines, text, 12); // matches the paragraph's FontSize above
            return p;
        }

        private static Paragraph CodeBlock(string code)
        {
            return new Paragraph(new Run(code))
            {
                Foreground = Ink, FontFamily = CodeFont, FontSize = 11,
                Background = BlockBg, BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9),
                Margin = new Thickness(0, 4, 0, 4),
            };
        }

        private static bool IsTableRow(string t) =>
            t.StartsWith("|") && t.TrimEnd().EndsWith("|") && t.Count(c => c == '|') >= 2;

        private static bool IsSeparatorRow(string t) =>
            t.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0
            && t.Contains("-");

        private static string[] SplitCells(string row)
        {
            var t = row.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(c => c.Trim()).ToArray();
        }

        // Detection rule (deliberately strict, see ElementIdClicked doc-comment):
        // the trimmed cell text must parse whole as a positive long ≥ 100000 —
        // real Revit element ids are always in that range, so this never fires
        // on a small "Count"/"Level" style numeric column.
        private static bool TryParseElementId(string cellText, out long id) =>
            long.TryParse((cellText ?? "").Trim(), out id) && id >= 100000;

        /// <summary>Accent-underlined, hand-cursor TextBlock for a detected
        /// element-id cell. Hover background + click both mirror the `[text](url)`
        /// Hyperlink branch below (same event, two different WPF primitives —
        /// table cells are plain TextBlocks, not text-flow Inlines).</summary>
        private static TextBlock IdLinkText(string text, long elementId)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Accent,
                FontSize = 11.5,
                TextDecorations = TextDecorations.Underline,
                Cursor = Cursors.Hand,
            };
            tb.MouseEnter += (_, __) => tb.Background = IdLinkHoverBg;
            tb.MouseLeave += (_, __) => tb.Background = Brushes.Transparent;
            tb.MouseLeftButtonDown += (_, __) => { try { ElementIdClicked?.Invoke(elementId); } catch { /* listener's problem, not ours */ } };
            return tb;
        }

        // Per-column readable floor. Star columns shrink to fit the bubble; this
        // stops any column collapsing to nothing at very narrow panel widths.
        private const double MinColWidth = 46;

        /// <summary>Width-responsive markdown table. Columns are STAR-sized
        /// (proportional to their longest cell) so the table always fits the bubble
        /// and cell text WRAPS instead of clipping — never Auto (Auto measures
        /// unbounded and overflows the narrow Revit dock). The grid is hosted in a
        /// horizontal ScrollViewer and its width is bound to the viewport, so at
        /// extreme widths where even wrapped columns can't fit their minimum, the
        /// TABLE scrolls inside its own box — the thread never scrolls sideways.</summary>
        private static FrameworkElement TableBlock(List<string> rows, double maxWidth)
        {
            var dataRows = rows.Where(r => !IsSeparatorRow(r)).Select(SplitCells).ToList();
            if (dataRows.Count == 0) return null;
            int cols = dataRows.Max(r => r.Length);

            // Element-id columns: header cell text is exactly ID / Id / Element ID
            // (case-insensitive, trimmed) — deliberately strict so a "Count" or
            // "Area" column never lights up as a false-positive link.
            var idCols = new HashSet<int>();
            var header0 = dataRows[0];
            for (int c = 0; c < header0.Length; c++)
            {
                var h = (header0[c] ?? "").Trim();
                if (string.Equals(h, "ID", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(h, "Element ID", StringComparison.OrdinalIgnoreCase))
                    idCols.Add(c);
            }

            // Proportional weights from each column's longest cell (clamped) so a
            // wide "Tujuan" column gets more share than a narrow index column.
            var weights = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                int longest = 4;
                foreach (var row in dataRows)
                    if (c < row.Length && row[c] != null) longest = Math.Max(longest, row[c].Length);
                weights[c] = Math.Min(30, Math.Max(4, longest));
            }

            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            for (int c = 0; c < cols; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(weights[c], GridUnitType.Star),
                    MinWidth = MinColWidth,
                });
            for (int r = 0; r < dataRows.Count; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int r = 0; r < dataRows.Count; r++)
            {
                bool header = r == 0;
                for (int c = 0; c < cols; c++)
                {
                    var cell = new Border
                    {
                        BorderBrush = Line,
                        BorderThickness = new Thickness(0.5),
                        Background = header ? CodeBg : CellBg,
                        Padding = new Thickness(7, 4, 7, 4),
                    };
                    string cellText = c < dataRows[r].Length ? dataRows[r][c] : "";
                    TextBlock tb;
                    // Body cell in an id column whose text parses as a positive
                    // long ≥ 100000 → clickable element-id link (never the header
                    // row itself, so the column label stays plain text).
                    if (!header && idCols.Contains(c) && TryParseElementId(cellText, out long elementId))
                    {
                        tb = IdLinkText(cellText.Trim(), elementId);
                    }
                    else
                    {
                        tb = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = header ? Ink : Text,
                            FontSize = 11.5,
                            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                        };
                        AddInlines(tb.Inlines, cellText, 11.5); // matches the cell TextBlock's FontSize above
                    }
                    cell.Child = tb;
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            grid.MaxWidth = maxWidth;
            return grid;
        }

        // bina://select/<id> is the only URI scheme AddInlines treats as "live" —
        // everything else (http links etc.) stays an inert underlined Run, same
        // as before this task.
        private static readonly Regex SelectUriPattern = new Regex(@"^bina://select/(\d+)$");

        // fontSize = the effective text size of the surrounding element (each call
        // site knows its own: body/list 12.5, blockquote 12, headings 13-15, table
        // cells 11.5). Runs don't need it (they inherit), but any EmojiInline built
        // for this text does — see AppendTextWithEmoji remarks.
        private static void AddInlines(InlineCollection inlines, string text, double fontSize)
        {
            var pattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))";
            int last = 0;
            foreach (Match m in Regex.Matches(text, pattern))
            {
                if (m.Index > last) AppendTextWithEmoji(inlines, text.Substring(last, m.Index - last), fontSize);
                if (m.Groups[2].Success) AppendTextWithEmoji(inlines, m.Groups[2].Value, fontSize, r => r.FontWeight = FontWeights.Bold);
                else if (m.Groups[4].Success) AppendTextWithEmoji(inlines, m.Groups[4].Value, fontSize, r => r.FontStyle = FontStyles.Italic);
                // Code spans stay literal/monochrome — never emoji-substituted.
                else if (m.Groups[6].Success) inlines.Add(new Run(m.Groups[6].Value) { FontFamily = CodeFont, Foreground = CodeFg, FontSize = 11 });
                // Link text/click-handling is untouched — element ids never contain emoji.
                else if (m.Groups[8].Success) inlines.Add(LinkInline(m.Groups[8].Value, m.Groups[9].Value));
                last = m.Index + m.Length;
            }
            if (last < text.Length) AppendTextWithEmoji(inlines, text.Substring(last), fontSize);
            if (inlines.Count == 0) inlines.Add(new Run(text));
        }

        /// <summary>Splits <paramref name="text"/> on any Emoji.Wpf-detected emoji
        /// and appends each segment to <paramref name="inlines"/>: plain segments
        /// as a <see cref="Run"/> styled via <paramref name="style"/> (identical to
        /// what every call site built inline before this helper existed), emoji
        /// segments as a full-color <c>Emoji.Wpf.EmojiInline</c> — WPF's own text
        /// stack has no color-font support and renders emoji as monochrome outline
        /// glyphs otherwise.
        ///
        /// Detection reuses Emoji.Wpf's own compiled regex, <c>EmojiData.MatchOne</c>
        /// — the name is misleading; Emoji.Wpf's own <c>TextBlock.RecomputeInlines</c>
        /// and <c>FlowDocumentExtensions</c> both call <c>.Matches()</c> on it to find
        /// every emoji occurrence in a string, not just the first (verified against
        /// the emoji.wpf source, there is no public <c>MatchMultiple</c> API — only a
        /// stale comment mentions that name).
        ///
        /// <c>Foreground</c> is pinned to <see cref="Brushes.Black"/> on every
        /// EmojiInline: <c>EmojiInline.Rebuild()</c> only skips its (monochrome)
        /// TintEffect when Foreground resolves to pure black — this app's ambient
        /// text color is a dark navy (#131c2b, not pure black), which every inline
        /// would otherwise inherit and get tinted with, silently defeating the point
        /// of this change (real color emoji, not navy-tinted ones).
        ///
        /// <c>FontSize</c> must also be set explicitly (never left to inherit):
        /// setting <c>Text</c> triggers <c>EmojiInline.Rebuild()</c> synchronously,
        /// BEFORE the inline is attached to the tree — sizing the glyph image with
        /// the WPF default FontSize and leaving correction to inherited-property
        /// invalidation timing after <c>inlines.Add()</c>. Emoji.Wpf's own call
        /// sites always pass FontSize explicitly; so do we (initializer order
        /// matters: FontSize before Text, so the first Rebuild already sees it).
        ///
        /// EmojiInline construction is wrapped in try/catch: a decorative color-emoji
        /// nicety must never crash the copilot pane, so any failure (e.g. a missing
        /// emoji font resource) falls back to a plain styled Run of the same text.
        /// Strings with no emoji take the original single-Run path (they still pay
        /// one MatchOne.Matches scan, but allocate nothing new).</summary>
        private static void AppendTextWithEmoji(InlineCollection inlines, string text, double fontSize, Action<Run> style = null)
        {
            if (string.IsNullOrEmpty(text)) return;

            MatchCollection matches = null;
            try { matches = Emoji.Wpf.EmojiData.MatchOne.Matches(text); }
            catch { /* detection failed — fall through to the plain-Run fast path */ }

            if (matches == null || matches.Count == 0)
            {
                var run = new Run(text);
                style?.Invoke(run);
                inlines.Add(run);
                return;
            }

            int last = 0;
            foreach (Match m in matches)
            {
                if (m.Index > last)
                {
                    var plain = new Run(text.Substring(last, m.Index - last));
                    style?.Invoke(plain);
                    inlines.Add(plain);
                }
                try
                {
                    inlines.Add(new Emoji.Wpf.EmojiInline
                    {
                        FontSize = fontSize,        // BEFORE Text — see remarks above
                        Foreground = Brushes.Black, // true color — see remarks above
                        Text = m.Value,
                    });
                }
                catch
                {
                    var fallback = new Run(m.Value);
                    style?.Invoke(fallback);
                    inlines.Add(fallback);
                }
                last = m.Index + m.Length;
            }
            if (last < text.Length)
            {
                var tail = new Run(text.Substring(last));
                style?.Invoke(tail);
                inlines.Add(tail);
            }
        }

        /// <summary>`[text](url)` → a real clickable Hyperlink ONLY for
        /// `bina://select/&lt;id&gt;` (raises the same ElementIdClicked event as
        /// the table id-cells); any other url stays the inert styled Run today's
        /// prose links have always rendered as (no navigation, nothing to click).</summary>
        private static Inline LinkInline(string linkText, string url)
        {
            var m = SelectUriPattern.Match(url ?? "");
            if (m.Success && long.TryParse(m.Groups[1].Value, out long elementId))
            {
                var hl = new Hyperlink(new Run(linkText))
                {
                    Foreground = Accent,
                    TextDecorations = TextDecorations.Underline,
                    Cursor = Cursors.Hand,
                };
                hl.Click += (_, __) => { try { ElementIdClicked?.Invoke(elementId); } catch { /* listener's problem, not ours */ } };
                return hl;
            }
            return new Run(linkText) { Foreground = Accent, TextDecorations = TextDecorations.Underline };
        }
    }
}
