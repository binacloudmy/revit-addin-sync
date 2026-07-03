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
        // Light palette (matches CopilotTokens / the .dc.html light theme).
        private static readonly SolidColorBrush Ink     = Brush("#131c2b"); // headers
        private static readonly SolidColorBrush Text    = Brush("#131c2b"); // body (design --text)
        private static readonly SolidColorBrush Muted   = Brush("#586273"); // quotes/citations
        private static readonly SolidColorBrush Accent  = Brush("#1d4ed8"); // bullets/links
        private static readonly SolidColorBrush Line    = Brush("#290F1B2D"); // table borders
        private static readonly SolidColorBrush CodeBg  = Brush("#f3f6f9");
        private static readonly SolidColorBrush CodeFg  = Brush("#1e40af");
        private static readonly SolidColorBrush BlockBg = Brush("#f6f8fa");
        private static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono, Consolas, monospace");

        private static SolidColorBrush Brush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        public static FrameworkElement Render(string markdown, double maxWidth = 350)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 12.5,
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
                    var grid = TableGrid(rows, maxWidth);
                    if (grid != null) Add(new BlockUIContainer(grid) { Margin = new Thickness(0, 4, 0, 4) });
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
                AddInlines(p.Inlines, trimmed);
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
            AddInlines(p.Inlines, text);
            return p;
        }

        private static Paragraph ListItem(string marker, string text)
        {
            var p = new Paragraph { Margin = new Thickness(8, 1, 0, 1) };
            p.Inlines.Add(new Run(marker + "  ") { Foreground = Accent });
            AddInlines(p.Inlines, text);
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
            AddInlines(p.Inlines, text);
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

        /// <summary>The auto-sizing Grid table the chat has always used — hosted
        /// in a BlockUIContainer (FlowDocument Tables can't auto-fit columns).</summary>
        private static Grid TableGrid(List<string> rows, double maxWidth)
        {
            var dataRows = rows.Where(r => !IsSeparatorRow(r)).Select(SplitCells).ToList();
            if (dataRows.Count == 0) return null;
            int cols = dataRows.Max(r => r.Length);

            var grid = new Grid { MaxWidth = maxWidth };
            for (int c = 0; c < cols; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
                        Background = header ? CodeBg : Brushes.White,
                        Padding = new Thickness(7, 4, 7, 4),
                    };
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = header ? Ink : Text,
                        FontSize = 11.5,
                        FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
                    };
                    AddInlines(tb.Inlines, c < dataRows[r].Length ? dataRows[r][c] : "");
                    cell.Child = tb;
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }
            return grid;
        }

        private static void AddInlines(InlineCollection inlines, string text)
        {
            var pattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))";
            int last = 0;
            foreach (Match m in Regex.Matches(text, pattern))
            {
                if (m.Index > last) inlines.Add(new Run(text.Substring(last, m.Index - last)));
                if (m.Groups[2].Success) inlines.Add(new Run(m.Groups[2].Value) { FontWeight = FontWeights.Bold });
                else if (m.Groups[4].Success) inlines.Add(new Run(m.Groups[4].Value) { FontStyle = FontStyles.Italic });
                else if (m.Groups[6].Success) inlines.Add(new Run(m.Groups[6].Value) { FontFamily = CodeFont, Foreground = CodeFg, FontSize = 11 });
                else if (m.Groups[8].Success) inlines.Add(new Run(m.Groups[8].Value) { Foreground = Accent, TextDecorations = TextDecorations.Underline });
                last = m.Index + m.Length;
            }
            if (last < text.Length) inlines.Add(new Run(text.Substring(last)));
            if (inlines.Count == 0) inlines.Add(new Run(text));
        }
    }
}
