using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RevitWebAppSync.Helpers
{
    /// <summary>
    /// Lightweight markdown → WPF converter for the Copilot chat bubbles.
    /// Light-themed (dark text on white). Supports headers, **bold**,
    /// *italic*, `code`, ```code blocks```, bullet + numbered lists,
    /// > blockquotes, [links](url), and GitHub-style | tables |.
    /// </summary>
    public static class MarkdownRenderer
    {
        // Light-theme palette (matches CopilotTokens).
        private static readonly SolidColorBrush Ink     = Brush("#0b0d12"); // headers
        private static readonly SolidColorBrush Text    = Brush("#374151"); // body
        private static readonly SolidColorBrush Muted   = Brush("#6b7280"); // quotes/citations
        private static readonly SolidColorBrush Accent  = Brush("#2563eb"); // bullets/links
        private static readonly SolidColorBrush Line    = Brush("#e5e7eb"); // table borders
        private static readonly SolidColorBrush CodeBg  = Brush("#f3f4f6");
        private static readonly SolidColorBrush CodeFg  = Brush("#9333ea");
        private static readonly SolidColorBrush BlockBg = Brush("#f6f8fa");
        private static readonly FontFamily CodeFont = new FontFamily("Cascadia Mono, Consolas, monospace");

        private static SolidColorBrush Brush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        public static StackPanel Render(string markdown, double maxWidth = 350)
        {
            var panel = new StackPanel { MaxWidth = maxWidth };
            if (string.IsNullOrWhiteSpace(markdown)) return panel;

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCode = false;
            var codeLines = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // ``` fenced code block toggle
                if (trimmed.StartsWith("```"))
                {
                    if (inCode) { AddCodeBlock(panel, string.Join("\n", codeLines), maxWidth); codeLines.Clear(); inCode = false; }
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
                    AddTable(panel, rows, maxWidth);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed)) { panel.Children.Add(new Border { Height = 6 }); continue; }

                if (trimmed.StartsWith("### ")) { AddHeader(panel, trimmed.Substring(4), 13, maxWidth); continue; }
                if (trimmed.StartsWith("## "))  { AddHeader(panel, trimmed.Substring(3), 14, maxWidth); continue; }
                if (trimmed.StartsWith("# "))   { AddHeader(panel, trimmed.Substring(2), 15, maxWidth); continue; }

                if (trimmed.StartsWith("> "))   { AddBlockquote(panel, trimmed.Substring(2), maxWidth); continue; }

                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                { AddListItem(panel, "•", trimmed.Substring(2), maxWidth); continue; }

                var numbered = Regex.Match(trimmed, @"^(\d+)\.\s+(.*)$");
                if (numbered.Success)
                { AddListItem(panel, numbered.Groups[1].Value + ".", numbered.Groups[2].Value, maxWidth); continue; }

                // paragraph
                var p = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Text, FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(p.Inlines, trimmed);
                panel.Children.Add(p);
            }

            if (inCode && codeLines.Count > 0) AddCodeBlock(panel, string.Join("\n", codeLines), maxWidth);
            return panel;
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

        private static void AddTable(StackPanel panel, List<string> rows, double maxWidth)
        {
            var dataRows = rows.Where(r => !IsSeparatorRow(r)).Select(SplitCells).ToList();
            if (dataRows.Count == 0) return;
            int cols = dataRows.Max(r => r.Length);

            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4), MaxWidth = maxWidth };
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
            panel.Children.Add(grid);
        }

        private static void AddBlockquote(StackPanel panel, string text, double maxWidth)
        {
            var border = new Border
            {
                BorderBrush = Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = BlockBg,
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 3, 0, 3),
                CornerRadius = new CornerRadius(0, 4, 4, 0),
            };
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, MaxWidth = maxWidth - 24 };
            AddInlines(tb.Inlines, text);
            border.Child = tb;
            panel.Children.Add(border);
        }

        private static void AddListItem(StackPanel panel, string marker, string text, double maxWidth)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 1, 0, 1) };
            row.Children.Add(new TextBlock { Text = marker + "  ", Foreground = Accent, FontSize = 12.5, MinWidth = 16 });
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Text, FontSize = 12.5, MaxWidth = maxWidth - 30 };
            AddInlines(tb.Inlines, text);
            row.Children.Add(tb);
            panel.Children.Add(row);
        }

        private static void AddHeader(StackPanel panel, string text, double fontSize, double maxWidth)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ink, FontSize = fontSize, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 2), MaxWidth = maxWidth };
            AddInlines(tb.Inlines, text);
            panel.Children.Add(tb);
        }

        private static void AddCodeBlock(StackPanel panel, string code, double maxWidth)
        {
            var border = new Border { Background = BlockBg, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(9), Margin = new Thickness(0, 4, 0, 4), MaxWidth = maxWidth };
            border.Child = new TextBox
            {
                Text = code, Foreground = Ink, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = CodeFont, FontSize = 11,
            };
            panel.Children.Add(border);
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
