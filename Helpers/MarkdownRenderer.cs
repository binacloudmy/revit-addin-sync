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
    /// *italic*, __bold__, ~~strike~~, `code`, ```code blocks```, bullet
    /// (- * + dot) + numbered (1. and 1)) lists, > blockquotes, --- rules,
    /// [links](url), and GitHub-style | tables |.
    ///
    /// Renders into a FlowDocument hosted in a read-only borderless
    /// RichTextBox so the user can HIGHLIGHT AND COPY any part of an AI
    /// reply (a plain TextBlock cannot be selected). Tables keep their
    /// auto-sizing Grid look via BlockUIContainer (not text-selectable —
    /// the bubble's ⧉ copy button covers them).
    /// </summary>
    public static class MarkdownRenderer
    {
        // Palette from the copilot colour tokens (ThemeTokens, AI-311), resolved for the current theme AT
        // RENDER TIME (ChatView re-renders replies on ThemeChanged). Each token holds the colour the old hex
        // literal drew through CopilotColors' light -> v6 / light -> dark maps, so neither theme changed.
        private static Brush Ink     => UI.Copilot.Controls.CopilotColors.Token("Cp.Ink");       // headers
        private static Brush Text    => UI.Copilot.Controls.CopilotColors.Token("Cp.Text");      // body (design --text)
        private static Brush Muted   => UI.Copilot.Controls.CopilotColors.Token("Cp.Muted");     // quotes/citations
        private static Brush Accent  => UI.Copilot.Controls.CopilotColors.Token("Cp.Accent");    // bullets/links
        private static Brush Line    => UI.Copilot.Controls.CopilotColors.Token("Cp.Hair2");     // table borders
        private static Brush CodeBg  => UI.Copilot.Controls.CopilotColors.Token("Cp.CodeBg");
        private static Brush CodeFg  => UI.Copilot.Controls.CopilotColors.Token("Cp.CodeFg");
        private static Brush BlockBg => UI.Copilot.Controls.CopilotColors.Token("Cp.CodeBg");
        private static Brush CellBg  => UI.Copilot.Controls.CopilotColors.Token("Cp.Menu");      // table data-cell surface
        private static Brush IdLinkHoverBg => UI.Copilot.Controls.CopilotColors.Token("Cp.Tier2Bg"); // clickable-id hover chip
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

        /// <param name="tableDownloads">False when the reply already offers its
        /// tables for export (the turn's "Export to Excel"), so a table does not
        /// carry a second, competing "Download CSV" button.</param>
        public static FrameworkElement Render(string markdown, double maxWidth = 350, bool tableDownloads = true)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = BodyFontSize,
                Foreground = Text,
            };

            if (!string.IsNullOrWhiteSpace(markdown))
                BuildBlocks(doc.Blocks, markdown, maxWidth, tableDownloads);

            var box = new RichTextBox
            {
                Document = doc,
                IsReadOnly = true,
                // Without this, WPF renders every UIElement embedded in the
                // document (table grids, the Download-CSV button) and every
                // Hyperlink (bina://select element-id links) as DISABLED —
                // visible but dead to clicks. Read-only text selection is
                // unaffected.
                IsDocumentEnabled = true,
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

        private static void BuildBlocks(BlockCollection blocks, string markdown, double maxWidth, bool tableDownloads)
        {
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCode = false;
            var codeLines = new List<string>();
            // Nearest heading ABOVE a table names its Download-CSV file
            // (TableCsv.SuggestFileName slugifies it; null → "bina-schedule").
            string lastHeading = null;
            // A blank markdown line becomes extra top-margin on the NEXT block
            // (an empty FlowDocument paragraph would render a full line tall).
            double pendingSpace = 0;
            // The list item the previous line produced (null after anything else):
            // an indented line that follows it continues its text.
            Paragraph lastItem = null;

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
                var prevItem = lastItem;
                lastItem = null;

                // ``` fenced code block toggle
                if (MarkdownBlocks.IsFence(trimmed.Trim()))
                {
                    if (inCode) { Add(CodeBlock(string.Join("\n", codeLines))); codeLines.Clear(); inCode = false; }
                    else inCode = true;
                    continue;
                }
                if (inCode) { codeLines.Add(line); continue; }

                // | table | — gather the contiguous block of pipe rows
                if (MarkdownBlocks.IsPipeRow(trimmed))
                {
                    var rows = new List<string>();
                    while (i < lines.Length && MarkdownBlocks.IsPipeRow(lines[i]))
                        rows.Add(lines[i++].Trim());
                    i--; // step back; for-loop will advance
                    var table = TableBlock(rows, maxWidth, lastHeading, tableDownloads);
                    if (table != null) Add(new BlockUIContainer(table) { Margin = new Thickness(0, 4, 0, 4) });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed)) { pendingSpace = 6; continue; }

                // # … ###### headings (#### and deeper used to show their hashes).
                int level = MarkdownBlocks.HeadingLevel(trimmed.Trim(), out var headingText);
                if (level > 0)
                {
                    lastHeading = headingText;
                    Add(Header(headingText, level == 1 ? 15 : level == 2 ? 14 : level == 3 ? 13 : 12.5));
                    continue;
                }

                if (MarkdownBlocks.IsHorizontalRule(trimmed)) { Add(Rule()); continue; }

                if (trimmed.StartsWith("> ")) { Add(Blockquote(trimmed.Substring(2))); continue; }

                if (MarkdownBlocks.TryBullet(line, out var bulletLevel, out var bulletText))
                {
                    var bulletItem = ListItem(bulletLevel == 0 ? "•" : "◦", bulletText, bulletLevel);
                    Add(bulletItem);
                    lastItem = bulletItem;
                    continue;
                }

                if (MarkdownBlocks.TryNumbered(line, out var numLevel, out var number, out var numText))
                {
                    var numItem = ListItem(number + ".", numText, numLevel);
                    Add(numItem);
                    lastItem = numItem;
                    continue;
                }

                // Indented line right after a list item: more text of that item.
                if (prevItem != null && MarkdownBlocks.TryListContinuation(line, out var continuation))
                {
                    prevItem.Inlines.Add(new Run(" "));
                    AddInlines(prevItem.Inlines, continuation, BodyFontSize);
                    lastItem = prevItem;
                    continue;
                }

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

        private static Paragraph ListItem(string marker, string text, int level = 0)
        {
            // Nested items step in 14px per level.
            var p = new Paragraph { Margin = new Thickness(8 + level * 14, 1, 0, 1) };
            p.Inlines.Add(new Run(marker + "  ") { Foreground = Accent });
            AddInlines(p.Inlines, text, BodyFontSize);
            return p;
        }

        /// <summary>--- / *** / ___ : a thin full-width line in the table-border colour.</summary>
        private static Block Rule()
        {
            return new BlockUIContainer(new Border
            {
                Height = 1,
                Background = Line,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            })
            { Margin = new Thickness(0, 6, 0, 6) };
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

        private static bool IsSeparatorRow(string t) => MarkdownBlocks.IsSeparatorRow(t);

        private static string[] SplitCells(string row) => MarkdownBlocks.SplitCells(row);

        // Detection rule (deliberately strict, see ElementIdClicked doc-comment):
        // the trimmed cell text must parse whole as a positive long ≥ 100000 —
        // real Revit element ids are always in that range, so this never fires
        // on a small "Count"/"Level" style numeric column.
        private static bool TryParseElementId(string cellText, out long id) =>
            long.TryParse((cellText ?? "").Trim(), out id) && id >= 100000;

        /// <summary>Every element id this renderer would make clickable in the
        /// given markdown — table cells passing the strict rule above plus
        /// bina://select/&lt;id&gt; links. Distinct, first-appearance order.
        /// Pure (no WPF) — feeds ChatView's "Highlight in model" action row,
        /// which selects the whole set in one select_elements call.</summary>
        public static List<long> ExtractElementIds(string markdown)
        {
            var ids = new List<long>();
            var seen = new HashSet<long>();
            if (string.IsNullOrEmpty(markdown)) return ids;
            foreach (var line in markdown.Split('\n'))
            {
                if (line.IndexOf('|') < 0) continue;
                foreach (var cell in SplitCells(line))
                    if (TryParseElementId(cell, out var id) && seen.Add(id))
                        ids.Add(id);
            }
            foreach (Match m in Regex.Matches(markdown, @"bina://select/(\d+)"))
                if (long.TryParse(m.Groups[1].Value, out var id) && id >= 100000 && seen.Add(id))
                    ids.Add(id);
            return ids;
        }

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
        private static FrameworkElement TableBlock(List<string> rows, double maxWidth, string nearestHeading, bool tableDownloads)
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

            // Header-only tables get no export affordance — nothing to save.
            if (dataRows.Count < 2 || !tableDownloads) return grid;

            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = maxWidth };
            panel.Children.Add(grid);
            panel.Children.Add(DownloadCsvButton(dataRows, nearestHeading));
            return panel;
        }

        /// <summary>Small right-aligned "Download CSV" ghost button under a
        /// rendered table (ClickUp 86eybfttd). Writes the PARSED cells (the same
        /// dataRows the grid rendered) via SaveFileDialog. UTF-8 WITH BOM so
        /// Excel opens Malay/unicode text correctly. The whole click handler is
        /// try/caught — the pane lives inside Revit and must never throw across
        /// the WPF dispatcher; a write failure just relabels the button.</summary>
        private static FrameworkElement DownloadCsvButton(List<string[]> dataRows, string nearestHeading)
        {
            var label = new TextBlock { Text = "Download CSV", FontSize = 11, Foreground = Accent };
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(6, 2, 6, 2),
                IsTabStop = false,
                ToolTip = "Save this table as a .csv file",
            };
            UI.Copilot.Controls.FlatButton.Apply(btn, radius: 5);
            btn.Click += (_, __) =>
            {
                try
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        FileName = TableCsv.SuggestFileName(nearestHeading) + ".csv",
                        Filter = "CSV (*.csv)|*.csv",
                        DefaultExt = ".csv",
                        AddExtension = true,
                    };
                    if (dlg.ShowDialog() != true) return; // cancel = no-op
                    System.IO.File.WriteAllText(
                        dlg.FileName,
                        TableCsv.Serialize(dataRows),
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                }
                catch (Exception)
                {
                    label.Text = "Save failed — try another location";
                }
            };
            return btn;
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
            AddInlinesLine(inlines, text ?? "", fontSize);
            if (inlines.Count == 0) inlines.Add(new Run(text ?? ""));
        }

        // The span grammar (image, code, bold, __bold__, ~~strike~~, italic, link) lives in
        // MarkdownBlocks.TokenizeInline so it can be unit-tested without WPF.
        private static void AddInlinesLine(InlineCollection inlines, string text, double fontSize)
        {
            foreach (var t in MarkdownBlocks.TokenizeInline(text))
            {
                switch (t.Kind)
                {
                    case InlineKind.Image: inlines.Add(ImageInline(t.Text, t.Url)); break;
                    // Code spans stay literal/monochrome — never emoji-substituted.
                    case InlineKind.Code: inlines.Add(new Run(t.Text) { FontFamily = CodeFont, Foreground = CodeFg, FontSize = 11 }); break;
                    // Emphasis content is parsed again, so `code` or a link inside
                    // **bold** renders as code/link instead of literal backticks.
                    case InlineKind.Bold: inlines.Add(Emphasis(t.Text, fontSize, InlineKind.Bold)); break;
                    case InlineKind.Italic: inlines.Add(Emphasis(t.Text, fontSize, InlineKind.Italic)); break;
                    case InlineKind.Strike: inlines.Add(Emphasis(t.Text, fontSize, InlineKind.Strike)); break;
                    // Link text/click-handling is untouched — element ids never contain emoji.
                    case InlineKind.Link: inlines.Add(LinkInline(t.Text, t.Url)); break;
                    default: AppendTextWithEmoji(inlines, t.Text, fontSize); break;
                }
            }
        }

        private static Span Emphasis(string inner, double fontSize, InlineKind kind)
        {
            var span = new Span();
            if (kind == InlineKind.Bold) span.FontWeight = FontWeights.Bold;
            else if (kind == InlineKind.Italic) span.FontStyle = FontStyles.Italic;
            else if (kind == InlineKind.Strike) span.TextDecorations = TextDecorations.Strikethrough;
            AddInlinesLine(span.Inlines, inner, fontSize);
            return span;
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
        /// text color is a dark navy (Cp.Ink, not pure black), which every inline
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
        /// the table id-cells); any other url stays inert (no navigation, nothing
        /// to click) and shows its target next to the text (spec §7.11), so a
        /// link can never hide where it points.</summary>
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
            var span = new Span();
            span.Inlines.Add(new Run(linkText) { Foreground = Accent, TextDecorations = TextDecorations.Underline });
            var target = (url ?? "").Trim();
            if (target.Length > 0 && !string.Equals(target, (linkText ?? "").Trim(), StringComparison.Ordinal))
                span.Inlines.Add(new Run(" (" + target + ")") { Foreground = Brushes.Gray, FontSize = 11 });
            return span;
        }

        /// <summary>`![alt](url)` → never loaded: names the picture and where it
        /// points (spec §7.11, no remote images in the pane).</summary>
        private static Inline ImageInline(string alt, string url)
        {
            var label = string.IsNullOrWhiteSpace(alt) ? "image" : "image: " + alt.Trim();
            return new Run("[" + label + "] (" + (url ?? "").Trim() + ")") { Foreground = Brushes.Gray, FontStyle = FontStyles.Italic };
        }
    }
}
