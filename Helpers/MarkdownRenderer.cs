using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace RevitWebAppSync.Helpers
{
    /// <summary>
    /// Lightweight markdown → WPF converter for chat bubbles.
    /// Supports: **bold**, *italic*, `code`, ```code blocks```, 
    /// # headers, - bullet lists, 📄 citations, and [links](url).
    /// </summary>
    public static class MarkdownRenderer
    {
        private static readonly SolidColorBrush CodeBg = new SolidColorBrush(Color.FromRgb(30, 30, 30));
        private static readonly SolidColorBrush CodeFg = new SolidColorBrush(Color.FromRgb(156, 220, 254));
        private static readonly SolidColorBrush AccentBlue = new SolidColorBrush(Color.FromRgb(86, 156, 214));
        private static readonly SolidColorBrush CitationColor = new SolidColorBrush(Color.FromRgb(78, 201, 176));
        private static readonly FontFamily CodeFont = new FontFamily("Consolas");

        /// <summary>
        /// Render markdown string into a StackPanel of styled WPF elements.
        /// </summary>
        public static StackPanel Render(string markdown, double maxWidth = 350)
        {
            var panel = new StackPanel { MaxWidth = maxWidth };

            if (string.IsNullOrWhiteSpace(markdown))
                return panel;

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCodeBlock = false;
            var codeLines = new List<string>();

            foreach (var line in lines)
            {
                // Code block toggle
                if (line.TrimStart().StartsWith("```"))
                {
                    if (inCodeBlock)
                    {
                        // End code block
                        AddCodeBlock(panel, string.Join("\n", codeLines));
                        codeLines.Clear();
                        inCodeBlock = false;
                    }
                    else
                    {
                        inCodeBlock = true;
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    codeLines.Add(line);
                    continue;
                }

                var trimmed = line.TrimStart();

                // Empty line → small spacer
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    panel.Children.Add(new Border { Height = 6 });
                    continue;
                }

                // Headers
                if (trimmed.StartsWith("### "))
                {
                    AddHeader(panel, trimmed.Substring(4), 13, FontWeights.Bold);
                    continue;
                }
                if (trimmed.StartsWith("## "))
                {
                    AddHeader(panel, trimmed.Substring(3), 14, FontWeights.Bold);
                    continue;
                }
                if (trimmed.StartsWith("# "))
                {
                    AddHeader(panel, trimmed.Substring(2), 15, FontWeights.Bold);
                    continue;
                }

                // Bullet points
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    var bulletText = trimmed.Substring(2);
                    var bulletPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(8, 1, 0, 1)
                    };
                    bulletPanel.Children.Add(new TextBlock
                    {
                        Text = "•  ",
                        Foreground = AccentBlue,
                        FontSize = 12
                    });
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        MaxWidth = maxWidth - 30
                    };
                    AddInlines(tb.Inlines, bulletText);
                    bulletPanel.Children.Add(tb);
                    panel.Children.Add(bulletPanel);
                    continue;
                }

                // Citation lines (📄 Source: ...)
                if (trimmed.StartsWith("📄"))
                {
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = CitationColor,
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(4, 2, 0, 2)
                    };
                    tb.Text = trimmed;
                    panel.Children.Add(tb);
                    continue;
                }

                // Regular paragraph
                {
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    AddInlines(tb.Inlines, trimmed);
                    panel.Children.Add(tb);
                }
            }

            // Unclosed code block
            if (inCodeBlock && codeLines.Count > 0)
            {
                AddCodeBlock(panel, string.Join("\n", codeLines));
            }

            return panel;
        }

        /// <summary>
        /// Parse inline markdown (**bold**, *italic*, `code`, [link](url)) into Runs.
        /// </summary>
        private static void AddInlines(InlineCollection inlines, string text)
        {
            // Pattern: **bold** | *italic* | `code` | [text](url)
            var pattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))";
            int lastIndex = 0;

            foreach (Match match in Regex.Matches(text, pattern))
            {
                // Add text before match
                if (match.Index > lastIndex)
                {
                    inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
                }

                if (match.Groups[2].Success) // **bold**
                {
                    inlines.Add(new Run(match.Groups[2].Value) { FontWeight = FontWeights.Bold });
                }
                else if (match.Groups[4].Success) // *italic*
                {
                    inlines.Add(new Run(match.Groups[4].Value) { FontStyle = FontStyles.Italic });
                }
                else if (match.Groups[6].Success) // `code`
                {
                    inlines.Add(new Run(match.Groups[6].Value)
                    {
                        FontFamily = CodeFont,
                        Foreground = CodeFg,
                        FontSize = 11
                    });
                }
                else if (match.Groups[8].Success) // [text](url)
                {
                    inlines.Add(new Run(match.Groups[8].Value)
                    {
                        Foreground = AccentBlue,
                        TextDecorations = TextDecorations.Underline
                    });
                }

                lastIndex = match.Index + match.Length;
            }

            // Add remaining text
            if (lastIndex < text.Length)
            {
                inlines.Add(new Run(text.Substring(lastIndex)));
            }

            // If nothing was added (no matches), add the full text
            if (inlines.Count == 0)
            {
                inlines.Add(new Run(text));
            }
        }

        private static void AddHeader(StackPanel panel, string text, double fontSize, FontWeight weight)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = fontSize,
                FontWeight = weight,
                Margin = new Thickness(0, 4, 0, 2)
            };
            AddInlines(tb.Inlines, text);
            panel.Children.Add(tb);
        }

        private static void AddCodeBlock(StackPanel panel, string code)
        {
            var border = new Border
            {
                Background = CodeBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var textBox = new TextBox
            {
                Text = code,
                Foreground = CodeFg,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = CodeFont,
                FontSize = 11
            };

            border.Child = textBox;
            panel.Children.Add(border);
        }
    }
}
