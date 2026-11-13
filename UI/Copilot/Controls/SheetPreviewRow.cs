#nullable enable
// Boards SheetGen / SheetsExcel: the small sheet-outline preview row under the columns band of the items list.
//   outline   64 wide, aspect 841:594 (about 1.41:1), radius 4, Cp.Sunken fill, 1px Cp.Hair2 border; the plan's name inside
//             (mono 9.5, Cp.Muted, ellipsis).
//   text      to the right, 12 apart: the sheet number (mono 12, Cp.Ink) and "1:100 · fills 14% of the drawing area" (12, Cp.Muted;
//             the "fills" part only when the item carries a percentage).
// What is drawn, and when, is decided by SheetPreview; nothing is drawn for an item that lacks what the preview needs.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class SheetPreviewRow
    {
        internal static FrameworkElement Build(SheetPreview p, ReceiptLanguage lang, bool top)
        {
            var name = CardKit.Text(p.Label, 9.5, "Cp.Muted", wrap: false, mono: true);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.TextAlignment = TextAlignment.Center;
            name.Margin = new Thickness(3, 0, 3, 0);
            name.ToolTip = p.Label;

            var outline = new Border
            {
                Width = SheetPreview.OutlineWidth, Height = SheetPreview.OutlineHeight, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = name,
            };
            outline.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            outline.SetResourceReference(Border.BorderBrushProperty, "Cp.Hair2");
            AutomationProperties.SetName(outline, (lang == ReceiptLanguage.Ms ? "Garis sheet: " : "Sheet outline: ") + p.Label);

            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            if (p.Number.Length > 0) text.Children.Add(CardKit.Text(p.Number, 12, "Cp.Ink", wrap: false, mono: true));
            text.Children.Add(CardKit.Text(p.DetailLine(lang), 12, "Cp.Muted", margin: new Thickness(0, p.Number.Length > 0 ? 2 : 0, 0, 0)));
            Grid.SetColumn(text, 1);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.Children.Add(outline);
            grid.Children.Add(text);
            return ItemsCard.Line(grid, top, new Thickness(14, 10, 14, 10));
        }
    }
}
