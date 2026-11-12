#nullable enable
// "Revit warned about N things": what Revit flagged while a committed write ran (overlapping or identical elements placed on top of
// each other, ...), drawn inside the result card under the body as an amber block: a header with the count, one row per distinct
// message (a count chip when it repeated, one Show button over the union of its ids), "+ N more" past five.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class WriteWarningsBlock
    {
        public static void Add(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            if (card.WriteWarnings.Count == 0) return;
            var total = WriteWarningText.Count(card.WriteWarnings);

            var box = new StackPanel();
            var head = new StackPanel { Margin = new Thickness(0) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse("M8,2.5 L14,13 H2 Z M8,6.5 V9.5 M8,11.3 V11.5"), Width = 16, Height = 16, StrokeThickness = 1.5,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.AmberFg");
            title.Children.Add(icon);
            var t = CardKit.Text(WriteWarningText.Title(total, lang), 13, "Cp.AmberFg", FontWeights.SemiBold, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(t);
            head.Children.Add(title);
            head.Children.Add(CardKit.Text(WriteWarningText.Subtitle(lang), 11.5, "Cp.Muted", margin: new Thickness(24, 2, 0, 0)));
            var header = CardKit.Header("Cp.AmberBg", head, 0);
            header.Padding = new Thickness(14, 9, 14, 9);
            header.BorderThickness = new Thickness(0, 1, 0, 0);
            header.SetResourceReference(Border.BorderBrushProperty, "Cp.AmberLn");
            box.Children.Add(header);

            var drawn = Math.Min(card.WriteWarnings.Count, WriteWarningText.MaxRows);
            for (var i = 0; i < drawn; i++) box.Children.Add(FindingsBody.Row(card.WriteWarnings[i], lang, actions, top: true));
            if (card.WriteWarnings.Count > drawn)
                box.Children.Add(ItemsCard.MoreRow(ItemsBody.MoreLine(card.WriteWarnings.Count - drawn, lang), true));
            root.Children.Add(box);
        }
    }
}
