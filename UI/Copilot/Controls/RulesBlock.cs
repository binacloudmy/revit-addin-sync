#nullable enable
// The "Rules checked . N failed" block of a rule-set result, drawn inside the result card (board look): a header with
// the title, one row per rule that needs a look (mono rule id, its name wrapped whole, a count chip in amber or red,
// a ghost Highlight button when its failing elements can be selected), the reason under a rule that could not be
// verified, and the passed rules folded into one line.
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class RulesBlock
    {
        public static FrameworkElement? Build(System.Collections.Generic.IEnumerable<ResultTable> tables, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var rows = RulesBlockText.Rows(tables);
            if (rows.Count == 0) return null;

            var box = new StackPanel();
            var head = new Border { Padding = new Thickness(14, 6, 10, 6), MinHeight = 40 };
            head.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
            var headRow = new DockPanel { LastChildFill = true };
            if (actions.ShowInModel != null)
            {
                var show = CardKit.Ghost(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel, CardKit.Icons.Target, compact: true);
                show.Margin = new Thickness(8, 0, 0, 0); show.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(show, Dock.Right);
                headRow.Children.Add(show);
            }
            var title = CardKit.Text(RulesBlockText.Title(rows, ms), 13, "Cp.Ink", FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            headRow.Children.Add(title);
            head.Child = headRow;
            box.Children.Add(head);

            var open = rows.Where(RulesBlockText.IsOpen).ToList();
            for (var i = 0; i < open.Count; i++) box.Children.Add(Row(open[i], ms, actions, top: true));

            var passed = RulesBlockText.Passed(rows);
            if (passed > 0)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 14, 12) };
                var tick = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M3.5,8.5 L6.5,11.5 L12.5,4.5"), Width = 14, Height = 14, StrokeThickness = 1.6,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                tick.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.Green");
                line.Children.Add(tick);
                var text = CardKit.Text(RulesBlockText.PassedLine(passed, ms), 12, "Cp.Muted");
                text.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(text);
                var wrap = new Border { BorderThickness = new Thickness(0, open.Count > 0 ? 1 : 0, 0, 0), Child = line };
                wrap.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                box.Children.Add(wrap);
            }
            return box;
        }

        private static FrameworkElement Row(RuleRow r, bool ms, TurnEvidenceActions actions, bool top)
        {
            var grid = new Grid { Margin = new Thickness(0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            left.Children.Add(CardKit.Text(r.Id, 11.5, "Cp.Muted", FontWeights.Medium, mono: true));
            if (r.Name.Length > 0)
            {
                // The first clause on one or two lines; the catalog text with its citations is the tooltip.
                var name = CardKit.Text(RulesBlockText.ShortName(r.Name), 13, "Cp.Ink", margin: new Thickness(0, 2, 0, 0));
                if (RulesBlockText.ShortName(r.Name) != r.Name) name.ToolTip = r.Name;
                left.Children.Add(name);
            }
            if (r.NotVerified > 0 && r.Reason.Length > 0)
            {
                var why = ReceiptTemplates.ShortReason(r.Reason, ms ? ReceiptLanguage.Ms : ReceiptLanguage.En);
                if (why.Length > 0) left.Children.Add(CardKit.Text(why, 11.5, "Cp.Faint", margin: new Thickness(0, 2, 0, 0)));
            }
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            if (r.Fail > 0) right.Children.Add(Chip(RulesBlockText.FailChip(r.Fail, ms), RulesBlockText.IsError(r) ? "Cp.IssueBg" : "Cp.AmberBg", RulesBlockText.IsError(r) ? "Cp.IssueFg" : "Cp.AmberFg"));
            else if (r.NotVerified > 0) right.Children.Add(Chip(RulesBlockText.NotVerifiedChip(r.NotVerified, ms), "Cp.Raised", "Cp.Muted"));
            if (r.Handle != null && actions.Highlight != null)
            {
                var h = r.Handle;
                var btn = CardKit.Ghost(ReceiptTemplates.HighlightLabel(ms ? ReceiptLanguage.Ms : ReceiptLanguage.En), () => actions.Highlight(h), compact: true);
                btn.MinHeight = 26; btn.Margin = new Thickness(2, -2, 0, 0);
                right.Children.Add(btn);
            }
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            var row = new Border { Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(0, top ? 1 : 0, 0, 0), Child = grid };
            row.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            return row;
        }

        private static FrameworkElement Chip(string text, string bg, string fg)
        {
            var b = new Border { Height = 22, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            b.SetResourceReference(Border.BackgroundProperty, bg);
            var t = CardKit.Text(text, 11.5, fg, FontWeights.Medium, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }
    }
}
