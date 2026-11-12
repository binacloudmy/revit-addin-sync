#nullable enable
// The "Check N things" card and the inline "Next" row under it (design boards Main, SmartTags, SheetGen,
// SheetsExcel). The checks are their own card: amber border (Cp.AmberLn), a header strip on the amber
// surface (Cp.AmberBg) with a warning triangle and "Check N things" at 13/600, then one row per check
// with the text at 13 and its answers as h32 secondary buttons, the first answer that does something
// drawn as the lime outline. An answered row collapses to a check mark, what it did, and a "Change"
// link that opens the answers again. "Next" is the inline variant: a 12px label, then compact chips.
// The words and the small decisions are in ChecksCardText; a button runs through actions.RunCheckAction.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class ChecksCard
    {
        private const string WarningIcon = "M8,2.5 L14,13 H2 Z M8,6.5 V9.5 M8,11.3 V11.5";
        private const string TickIcon = "M3.5,8.5 L6.5,11.5 L12.5,4.5";

        public static void Add(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            AddChecks(root, card, ms, actions);
            AddNext(root, card, ms, actions);
        }

        // ─── The checks card ──────────────────────────────────────────────────

        private sealed class RowView
        {
            public Border Frame = null!;
            /// <summary>Still to look at: not answered and not left out.</summary>
            public bool Open = true;
            public bool Dismissed;
        }

        private static void AddChecks(StackPanel root, ResultCardModel card, bool ms, TurnEvidenceActions actions)
        {
            // A check that only restates a rule the Rules block lists (its text names the rule id, and it has no button) adds nothing.
            var checks = card.Checks.Where(c => !RulesBlockText.RestatesRule(c.Text, c.Actions.Count > 0, actions.RuleIds)).ToList();
            if (checks.Count == 0) return;

            // Nested in the result card with an inset; if it should sit outside the main card, Build moves this one line.
            var frame = CardKit.Card("Cp.AmberLn", "Cp.Bg", new Thickness(0, 12, 0, 0), 12);
            var stack = new StackPanel();
            frame.Child = stack;

            var title = CardKit.Text("", 13, "Cp.AmberFg", FontWeights.SemiBold, wrap: false);
            title.VerticalAlignment = VerticalAlignment.Center;
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var warn = Icon(WarningIcon, "Cp.AmberFg", 1.5, 16);
            warn.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(warn);
            head.Children.Add(title);
            var header = CardKit.Header("Cp.AmberBg", head, 12);
            header.Padding = new Thickness(14, 8, 14, 8);
            header.BorderThickness = new Thickness(0);
            stack.Children.Add(header);

            var rows = new List<RowView>();
            void Changed()
            {
                var open = 0;
                var anyShown = false;
                foreach (var r in rows) { if (r.Open && !r.Dismissed) open++; if (!r.Dismissed) anyShown = true; }
                // Nothing left to check: the strip goes, the answered rows stay (the board). Everything left out: the card goes.
                header.Visibility = open > 0 ? Visibility.Visible : Visibility.Collapsed;
                title.Text = ChecksCardText.Title(open, ms);
                frame.Visibility = anyShown ? Visibility.Visible : Visibility.Collapsed;
                var first = true;
                foreach (var r in rows)
                {
                    if (r.Dismissed) continue;
                    r.Frame.BorderThickness = new Thickness(0, first && open == 0 ? 0 : 1, 0, 0);
                    first = false;
                }
            }

            foreach (var c in checks)
            {
                var view = BuildRow(c, ms, actions, Changed);
                rows.Add(view);
                stack.Children.Add(view.Frame);
            }
            Changed();
            root.Children.Add(frame);
        }

        private static RowView BuildRow(CheckItem c, bool ms, TurnEvidenceActions actions, Action changed)
        {
            var view = new RowView();
            var frame = new Border { Padding = new Thickness(14, 12, 14, 6), BorderThickness = new Thickness(0, 1, 0, 0) };
            frame.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            view.Frame = frame;
            var stack = new StackPanel();
            frame.Child = stack;

            // The open part: the text, its answers, and why the last answer failed.
            var open = new StackPanel();
            open.Children.Add(CardKit.Text(c.Text, 13, "Cp.Ink", margin: new Thickness(0, 0, 0, c.Actions.Count > 0 ? 8 : 6)));
            var fail = CardKit.Text("", 12, "Cp.Muted", margin: new Thickness(0, 0, 0, 6));
            fail.Visibility = Visibility.Collapsed;

            // The answered part: a check mark, what the answer did, "Change".
            var done = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            done.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            done.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            done.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tick = Icon(TickIcon, "Cp.Green", 2, 14);
            tick.Margin = new Thickness(0, 0, 8, 0);
            tick.VerticalAlignment = VerticalAlignment.Center;
            var result = CardKit.Text("", 12.5, "Cp.Muted");
            result.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tick, 0); Grid.SetColumn(result, 1);
            done.Children.Add(tick); done.Children.Add(result);

            if (c.Actions.Count > 0)
            {
                var bar = new WrapPanel();
                var buttons = new List<Button>();
                var main = ChecksCardText.MainIndex(c.Actions);
                for (var i = 0; i < c.Actions.Count; i++)
                {
                    var action = c.Actions[i];
                    var btn = CardKit.Secondary(action.Label, async () =>
                    {
                        if (action.Kind == ResultActionKinds.Dismiss)
                        {
                            view.Dismissed = true; view.Open = false;
                            frame.Visibility = Visibility.Collapsed;
                            changed();
                            return;
                        }
                        if (actions.RunCheckAction == null) return;
                        foreach (var b in buttons) b.IsEnabled = false;
                        fail.Visibility = Visibility.Collapsed;
                        string? why;
                        try { why = await actions.RunCheckAction(action); } catch (Exception ex) { why = ex.Message; }
                        if (why == null)
                        {
                            result.Text = ChecksCardText.AnsweredText(action.Label, action.Kind, ms);
                            open.Visibility = Visibility.Collapsed;
                            done.Visibility = Visibility.Visible;
                            view.Open = false;
                            changed();
                        }
                        else
                        {
                            fail.Text = ChecksCardText.FailureText(why, ms);
                            fail.ToolTip = why;
                            fail.Visibility = Visibility.Visible;
                            foreach (var b in buttons) b.IsEnabled = true;
                        }
                    });
                    if (i == main) LimeOutline(btn);
                    buttons.Add(btn);
                    bar.Children.Add(btn);
                }
                open.Children.Add(bar);

                // "Change": the answers again, as they were.
                var change = CardKit.Ghost(ChecksCardText.ChangeLabel(ms), () =>
                {
                    done.Visibility = Visibility.Collapsed;
                    fail.Visibility = Visibility.Collapsed;
                    open.Visibility = Visibility.Visible;
                    foreach (var b in buttons) b.IsEnabled = true;
                    view.Open = true;
                    changed();
                }, compact: true);
                change.Content = new TextBlock { Text = ChecksCardText.ChangeLabel(ms), TextDecorations = TextDecorations.Underline };
                change.FontSize = 12;
                change.MinHeight = 28;
                change.Margin = new Thickness(0);
                change.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(change, 2);
                done.Children.Add(change);
            }
            open.Children.Add(fail);
            stack.Children.Add(open);
            stack.Children.Add(done);
            return view;
        }

        /// <summary>The main answer: a lime outline (border Cp.Accent), a tinted lime fill (Cp.BlueSoft) and light lime
        /// text (Cp.BlueText), on top of the secondary button's shape. Local to this card, not a button style.</summary>
        private static void LimeOutline(Button b)
        {
            b.SetResourceReference(Control.BorderBrushProperty, "Cp.Accent");
            b.SetResourceReference(Control.BackgroundProperty, "Cp.BlueSoft");
            b.SetResourceReference(Control.ForegroundProperty, "Cp.BlueText");
        }

        private static FrameworkElement Icon(string data, string stroke, double thickness, double size)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data), Width = 16, Height = 16, Stretch = Stretch.None, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
            return new Viewbox { Width = size, Height = size, Child = path, VerticalAlignment = VerticalAlignment.Center };
        }

        // ─── The "Next" row ───────────────────────────────────────────────────

        private static void AddNext(StackPanel root, ResultCardModel card, bool ms, TurnEvidenceActions actions)
        {
            if (card.Next.Count == 0 || actions.SendPrompt == null) return;
            var wrap = new WrapPanel { Margin = new Thickness(12, 6, 12, 4) };
            var label = CardKit.Text(ChecksCardText.NextLabel(ms), 12, "Cp.Faint", wrap: false, margin: new Thickness(0, 0, 8, 6));
            label.VerticalAlignment = VerticalAlignment.Center;
            wrap.Children.Add(label);
            foreach (var next in card.Next)
            {
                var text = next;
                wrap.Children.Add(CardKit.Secondary(text, () => actions.SendPrompt(text), compact: true));
            }
            root.Children.Add(wrap);
        }
    }
}
