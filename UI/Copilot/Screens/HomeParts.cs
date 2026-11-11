using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Pieces shared by the Chat home screen and the Commands tab
    /// (board 1): the "Your commands" list with a Run pill per saved command,
    /// and the "Or ask, for example" prompt rows tagged BM / EN. Built in code
    /// like the other screens (Revit-safe: no storyboards).</summary>
    internal static class HomeParts
    {
        /// <summary>The caller's saved (Mine) commands, in catalog order.</summary>
        public static List<SlashTool> SavedCommands() =>
            ToolCatalog.All.Where(t => t.Category == "Mine").ToList();

        // Uppercase muted section header, same look as the Library headers.
        public static FrameworkElement SectionHeader(string text)
        {
            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 2, 6),
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            return tb;
        }

        /// <summary>"Your commands": one row per saved command with a Run pill.
        /// With none saved, a one-line hint instead of an empty card.</summary>
        public static FrameworkElement SavedCommandsSection(Action<SlashTool> run)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            section.Children.Add(SectionHeader("Your commands"));

            var mine = SavedCommands();
            if (mine.Count == 0)
            {
                var none = new TextBlock
                {
                    Text = "No saved commands yet. Type / in the message box to see the commands you can run.",
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 0),
                };
                none.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                section.Children.Add(none);
                return section;
            }

            var rows = new StackPanel();
            for (int i = 0; i < mine.Count; i++)
            {
                if (i > 0)
                {
                    var hair = new Border { Height = 1 };
                    hair.SetResourceReference(Border.BackgroundProperty, "Cp.LineSoft");
                    rows.Children.Add(hair);
                }
                rows.Children.Add(CommandRow(mine[i], run));
            }
            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = rows, ClipToBounds = true };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            card.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            section.Children.Add(card);
            return section;
        }

        private static FrameworkElement CommandRow(SlashTool tool, Action<SlashTool> run)
        {
            var g = new Grid { Margin = new Thickness(12, 10, 10, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tile = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8),
                Background = CopilotColors.Tint("Cp.Brand", 0x17), VerticalAlignment = VerticalAlignment.Center,
            };
            var icon = new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = ToolCatalog.Icon(tool.IconKey),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
            tile.Child = icon;
            g.Children.Add(tile);

            var texts = new StackPanel { Margin = new Thickness(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = tool.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            texts.Children.Add(name);
            var sub = !string.IsNullOrWhiteSpace(tool.Subtitle) ? tool.Subtitle
                : tool.Inputs != null && tool.Inputs.Count > 0
                    ? "Asks for " + string.Join(", ", tool.Inputs.Select(x => x.Label ?? x.Name))
                    : "";
            if (sub.Length > 0)
            {
                var desc = new TextBlock { Text = sub, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                desc.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                texts.Children.Add(desc);
            }
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            var pill = new Border
            {
                CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Run ▸", FontSize = 11.5, FontWeight = FontWeights.Medium },
            };
            pill.SetResourceReference(Border.BackgroundProperty, "Cp.Hover");
            ((TextBlock)pill.Child).SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            Grid.SetColumn(pill, 2);
            g.Children.Add(pill);

            var row = new Border { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = g, ToolTip = "Run /" + tool.Id };
            row.MouseEnter += (_, __) => row.Background = CopilotColors.Tint("Cp.Brand", 0x0D);
            row.MouseLeave += (_, __) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, __) => run?.Invoke(tool);
            return row;
        }

        /// <summary>"Or ask, for example": (text, "BM"|"EN") rows. A tap puts the
        /// text in the message box for the drafter to edit and send.</summary>
        public static FrameworkElement ExamplesSection(IEnumerable<(string text, string lang)> examples, Action<string> pick)
        {
            var section = new StackPanel();
            section.Children.Add(SectionHeader("Or ask, for example"));
            foreach (var (text, lang) in examples)
            {
                var g = new Grid { Margin = new Thickness(14, 9, 10, 9) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tb = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
                g.Children.Add(tb);
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = lang, FontSize = 10, FontWeight = FontWeights.SemiBold },
                };
                chip.SetResourceReference(Border.BackgroundProperty, "Cp.Hover");
                ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                ((TextBlock)chip.Child).SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
                Grid.SetColumn(chip, 1);
                g.Children.Add(chip);

                var row = new Border
                {
                    CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8),
                    Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = g,
                };
                row.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                row.MouseEnter += (_, __) => row.Background = CopilotColors.Tint("Cp.Brand", 0x0D);
                row.MouseLeave += (_, __) => row.Background = Brushes.Transparent;
                var p = text;
                row.MouseLeftButtonUp += (_, __) => pick?.Invoke(p);
                section.Children.Add(row);
            }
            return section;
        }
    }
}
