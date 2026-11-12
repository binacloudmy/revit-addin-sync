using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// The live "Working" card of design board Running.dc.html: a pulsing lime dot, "Working" and a mono "Step N",
    /// a five-segment bar, one outcome row per step (18px status icon, label, mono count, a plain line under the
    /// active one), then, for a write, the "What I will write" card and a Stop row. Everything reads the scoped
    /// Cp.Run.* tokens. It lives only while a turn runs; a finished turn draws its own collapsed card.
    /// Animations are direct BeginAnimation calls (never a Storyboard: those crash inside a Revit pane).
    /// Update() rebuilds only when what it shows changed, so the spinner and the pulse do not restart every tick.
    /// </summary>
    public sealed class RunningCardView : StackPanel
    {
        private string _key;
        private double _pct;

        public Action OnStop { get; set; }
        public Action OnLayoutChanged { get; set; }
        public ReceiptLanguage Language { get; set; } = ReceiptLanguage.En;

        public RunningCardView()
        {
            SetResourceReference(TextElement.FontFamilyProperty, "Cp.Run.Font");
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        public void Update(IReadOnlyList<ProgressStep> steps, int replyChars)
        {
            steps ??= Array.Empty<ProgressStep>();
            var ms = Language == ReceiptLanguage.Ms;
            _pct = Math.Max(_pct, AgentActivityView.TurnPercent(steps, replyChars));
            var segments = RunningCard.Segments(_pct, running: true);
            var rows = RunningCard.Rows(steps);
            var step = RunningText.StepLabel(steps, Language);
            var preview = RunningCard.PreviewRecords(steps);
            var helper = RunningCard.StopHelper(steps, ms);

            var key = string.Join("|", segments) + "#" + step + "#" + helper + "#" + ms + "#"
                + string.Join(";", rows.Select(r => r.State + "," + r.Text + "," + r.Outcome + "," + r.Note))
                + "#" + string.Join(";", preview.Select(p => p.Key + "=" + p.Value + p.Derived));
            if (key == _key) return;
            _key = key;

            Children.Clear();
            Children.Add(Card(step, segments, rows));
            if (preview.Count > 0) Children.Add(PreviewCard(preview, ms));
            Children.Add(StopRow(helper, ms));
            OnLayoutChanged?.Invoke();
        }

        // ── Card ─────────────────────────────────────────────────────────────

        private FrameworkElement Card(string step, IReadOnlyList<SegmentState> segments, IReadOnlyList<RunRow> rows)
        {
            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Run.Surface");
            card.SetResourceReference(Border.BorderBrushProperty, "Cp.Run.Line");
            var col = new StackPanel();
            card.Child = col;

            // header: pulsing dot, "Working", mono step
            var head = new Grid { Margin = new Thickness(14, 12, 14, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(PulseDot());
            var working = Text(Language == ReceiptLanguage.Ms ? "Sedang bekerja" : "Working", 13, FontWeights.SemiBold, "Cp.Run.Text");
            working.Margin = new Thickness(9, 0, 0, 0);
            working.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(working, 1);
            head.Children.Add(working);
            if (step.Length > 0)
            {
                var right = Text(step, 11, FontWeights.Normal, "Cp.Run.Faint");
                right.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Run.FontMono");
                right.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(right, 2);
                head.Children.Add(right);
            }
            col.Children.Add(head);

            col.Children.Add(SegmentBar(segments));

            var list = new StackPanel { Margin = new Thickness(14, 14, 14, 16) };
            for (var i = 0; i < rows.Count; i++)
            {
                var row = Row(rows[i]);
                if (i > 0) row.Margin = new Thickness(0, 12, 0, 0);
                list.Children.Add(row);
            }
            if (rows.Count == 0) list.Margin = new Thickness(14, 4, 14, 8);
            col.Children.Add(list);
            return card;
        }

        private static FrameworkElement PulseDot()
        {
            var host = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            var halo = new Ellipse { Width = 16, Height = 16 };
            halo.SetResourceReference(Shape.FillProperty, "Cp.Run.AccentHalo");
            var dot = new Ellipse { Width = 8, Height = 8, RenderTransformOrigin = new Point(0.5, 0.5) };
            dot.SetResourceReference(Shape.FillProperty, "Cp.Run.Accent");
            var scale = new ScaleTransform(1, 1);
            dot.RenderTransform = scale;
            host.Children.Add(halo);
            host.Children.Add(dot);
            if (!CopilotTheme.ReducedMotion)
            {
                var d = new Duration(TimeSpan.FromMilliseconds(700));
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, d) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.8, d) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.8, d) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            }
            return host;
        }

        private static FrameworkElement SegmentBar(IReadOnlyList<SegmentState> segments)
        {
            var bar = new Grid { Margin = new Thickness(14, 0, 14, 0), Height = 3 };
            for (var i = 0; i < segments.Count; i++)
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var seg = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(i == 0 ? 0 : 1.5, 0, i == segments.Count - 1 ? 0 : 1.5, 0) };
                seg.SetResourceReference(Border.BackgroundProperty, segments[i] == SegmentState.Pending ? "Cp.Run.Raised" : "Cp.Run.Accent");
                if (segments[i] == SegmentState.Current && !CopilotTheme.ReducedMotion)
                    seg.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, new Duration(TimeSpan.FromMilliseconds(800)))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                Grid.SetColumn(seg, i);
                bar.Children.Add(seg);
            }
            return bar;
        }

        // ── Rows ─────────────────────────────────────────────────────────────

        private FrameworkElement Row(RunRow r)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = Icon(r.State);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 0, 0);
            grid.Children.Add(icon);

            var body = new StackPanel();
            var active = r.State == StepState.Running;
            var label = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            label.SetResourceReference(TextBlock.ForegroundProperty,
                r.State == StepState.Error ? "Cp.Run.Warn" : active ? "Cp.Run.Text" : "Cp.Run.Text2");
            foreach (var (text, mono) in RunningCard.LabelParts(r.Text))
            {
                var run = new System.Windows.Documents.Run(text);
                if (mono) { run.FontSize = 12; run.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Cp.Run.FontMono"); }
                label.Inlines.Add(run);
            }
            body.Children.Add(label);
            if (r.Note.Length > 0)
            {
                var note = Text(r.Note, 12, FontWeights.Normal, "Cp.Run.Faint");
                note.Margin = new Thickness(0, 2, 0, 0);
                note.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(note);
            }
            Grid.SetColumn(body, 2);
            grid.Children.Add(body);

            if (r.Outcome.Length > 0)
            {
                var outcome = Text(r.Outcome, 11, FontWeights.Normal, "Cp.Run.Faint");
                outcome.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Run.FontMono");
                outcome.Margin = new Thickness(10, 3, 0, 0);
                outcome.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(outcome, 3);
                grid.Children.Add(outcome);
            }
            return grid;
        }

        /// <summary>18px status icon: done = green-washed circle with a check; active = lime arc spinning on a faint ring;
        /// error = amber ring with a dash.</summary>
        private static FrameworkElement Icon(StepState state)
        {
            var g = new Grid { Width = 18, Height = 18 };
            if (state == StepState.Done)
            {
                var disc = new Ellipse { Width = 16, Height = 16 };
                disc.SetResourceReference(Shape.FillProperty, "Cp.Run.SuccessBg");
                g.Children.Add(disc);
                var check = new Path
                {
                    Data = Geometry.Parse("M5.6,9.2 L7.9,11.4 L12.4,6.4"), StrokeThickness = 1.8, Width = 18, Height = 18,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                };
                check.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Success");
                g.Children.Add(check);
            }
            else if (state == StepState.Error)
            {
                var ring = new Ellipse { Width = 16, Height = 16, StrokeThickness = 1.6 };
                ring.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Warn");
                g.Children.Add(ring);
                var dash = new Path
                {
                    Data = Geometry.Parse("M9,5.5 L9,9.5 M9,12 L9,12.1"), StrokeThickness = 1.8, Width = 18, Height = 18,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                dash.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Warn");
                g.Children.Add(dash);
            }
            else
            {
                var ring = new Ellipse { Width = 16, Height = 16, StrokeThickness = 2 };
                ring.SetResourceReference(Shape.StrokeProperty, "Cp.Run.AccentRing");
                g.Children.Add(ring);
                var arc = new Path
                {
                    Data = Geometry.Parse("M9,1 A8,8 0 0 1 17,9"), StrokeThickness = 2, Width = 18, Height = 18,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                arc.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Accent");
                g.Children.Add(arc);
                var rot = new RotateTransform(0, 9, 9);
                g.RenderTransform = rot;
                rot.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(900))) { RepeatBehavior = RepeatBehavior.Forever });
            }
            return g;
        }

        // ── "What I will write" ──────────────────────────────────────────────

        private FrameworkElement PreviewCard(IReadOnlyList<PreviewRecord> records, bool ms)
        {
            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 16, 0, 0) };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Run.Surface");
            card.SetResourceReference(Border.BorderBrushProperty, "Cp.Run.Line");
            var col = new StackPanel();
            card.Child = col;

            var head = new Border { Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(11, 11, 0, 0) };
            head.SetResourceReference(Border.BackgroundProperty, "Cp.Run.Surface2");
            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Text(WritePreviewText.Title(ms), 13, FontWeights.SemiBold, "Cp.Run.Text");
            hg.Children.Add(title);
            var pill = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var eye = new Path
            {
                Data = Geometry.Parse("M1.5,8 C1.5,8 4,3.5 8,3.5 C12,3.5 14.5,8 14.5,8 C14.5,8 12,12.5 8,12.5 C4,12.5 1.5,8 1.5,8 Z M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8 Z"),
                Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            eye.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Muted");
            pill.Children.Add(eye);
            pill.Children.Add(Text(ms ? "Pratonton" : "Preview", 11.5, FontWeights.Normal, "Cp.Run.Muted"));
            Grid.SetColumn(pill, 1);
            hg.Children.Add(pill);
            head.Child = hg;
            col.Children.Add(head);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < records.Count; i++)
            {
                var rec = records[i];
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var last = i == records.Count - 1;
                var key = new Border { Padding = new Thickness(14, rec.Derived ? 10 : 9, 8, rec.Derived ? 10 : 9), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
                key.SetResourceReference(Border.BorderBrushProperty, "Cp.Run.Line");
                var keyText = Text(rec.Key, 11.5, FontWeights.Normal, rec.Derived ? "Cp.Run.Warn" : "Cp.Run.Muted");
                keyText.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Run.FontMono");
                keyText.TextWrapping = TextWrapping.Wrap;
                key.Child = keyText;

                var val = new Border { Padding = new Thickness(0, rec.Derived ? 10 : 9, 14, rec.Derived ? 10 : 9), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
                val.SetResourceReference(Border.BorderBrushProperty, "Cp.Run.Line");
                if (rec.Derived)
                {
                    key.SetResourceReference(Border.BackgroundProperty, "Cp.Run.WarnBg");
                    val.SetResourceReference(Border.BackgroundProperty, "Cp.Run.WarnBg");
                    if (last) { key.CornerRadius = new CornerRadius(0, 0, 0, 11); val.CornerRadius = new CornerRadius(0, 0, 11, 0); }
                    var wg = new Grid();
                    wg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                    wg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var spark = new Path
                    {
                        Data = Geometry.Parse("M8,2.5 L9.3,6.7 L13.5,8 L9.3,9.3 L8,13.5 L6.7,9.3 L2.5,8 L6.7,6.7 Z"),
                        Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
                    };
                    spark.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Warn");
                    wg.Children.Add(spark);
                    var note = Text(WritePreviewText.DerivedNote(ms), 12.5, FontWeights.Normal, "Cp.Run.WarnText");
                    note.TextWrapping = TextWrapping.Wrap;
                    note.Margin = new Thickness(6, 0, 0, 0);
                    Grid.SetColumn(note, 1);
                    wg.Children.Add(note);
                    val.Child = wg;
                }
                else
                {
                    var v = Text(rec.Value, 13, FontWeights.Normal, "Cp.Run.Text");
                    v.TextWrapping = TextWrapping.Wrap;
                    val.Child = v;
                }
                Grid.SetRow(key, i); Grid.SetRow(val, i); Grid.SetColumn(val, 1);
                body.Children.Add(key);
                body.Children.Add(val);
            }
            col.Children.Add(body);
            return card;
        }

        // ── Stop row ─────────────────────────────────────────────────────────

        private FrameworkElement StopRow(string helper, bool ms)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var square = new Rectangle { Width = 9, Height = 9, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            square.SetResourceReference(Shape.FillProperty, "Cp.Run.Text");
            content.Children.Add(square);
            var stopText = Text(ms ? "Berhenti" : "Stop", 12.5, FontWeights.SemiBold, "Cp.Run.Text");
            stopText.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(stopText);
            var stop = RunKit.Button(content, "Cp.Run.Surface2", "Cp.Run.LineButton", height: 34, padding: new Thickness(12, 0, 14, 0), radius: 8);
            stop.ToolTip = ms ? "Hentikan larian ini" : "Stop this run";
            stop.Click += (_, __) => OnStop?.Invoke();
            row.Children.Add(stop);

            if (helper.Length > 0)
            {
                var shield = new Path
                {
                    Data = Geometry.Parse("M8,2 L13,4 L13,8 C13,11 10.8,13.2 8,14 C5.2,13.2 3,11 3,8 L3,4 Z"),
                    Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
                    Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                shield.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Success");
                row.Children.Add(shield);
                var help = Text(helper, 12, FontWeights.Normal, "Cp.Run.Muted");
                help.VerticalAlignment = VerticalAlignment.Center;
                help.TextWrapping = TextWrapping.Wrap;
                help.MaxWidth = 250;
                row.Children.Add(help);
            }
            return row;
        }

        // ── The "Copilot" label that heads the AI row (design: 16px lime tile + 12/600 muted) ──

        public static FrameworkElement CopilotLabel()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var tile = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
            tile.SetResourceReference(Border.BackgroundProperty, "Cp.Run.Accent");
            var glyph = new Path
            {
                Data = Geometry.Parse("M1.5,10 H10.5 M3,7 H9 M4.5,4 H7.5"), Width = 9, Height = 9, Stretch = Stretch.Uniform,
                StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(Shape.StrokeProperty, "Cp.Run.AccentInk");
            tile.Child = glyph;
            row.Children.Add(tile);
            var name = Text("Copilot", 12, FontWeights.SemiBold, "Cp.Run.Muted");
            name.Margin = new Thickness(7, 0, 0, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Run.Font");
            row.Children.Add(name);
            return row;
        }

        private static TextBlock Text(string text, double size, FontWeight weight, string brush)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }
    }

    /// <summary>A flat button in the running card's look: fill, 1px border, radius, hover to the raised fill, a lime
    /// ring on keyboard focus. Shared by the Stop row and the composer's Queue button.</summary>
    internal static class RunKit
    {
        private const string Template =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}' Padding='{TemplateBinding Padding}'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource Cp.Run.Raised}'/></Trigger>" +
            "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='{DynamicResource Cp.Run.Accent}'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";

        private static ControlTemplate _template;

        public static Button Button(object content, string fillToken, string borderToken, double height, Thickness padding, double radius)
        {
            _template ??= (ControlTemplate)XamlReader.Parse(Template);
            var b = new Button
            {
                Content = content, Height = height, Padding = padding, Template = _template, Cursor = System.Windows.Input.Cursors.Hand,
                Tag = new CornerRadius(radius), FocusVisualStyle = null,
            };
            b.SetResourceReference(Control.BackgroundProperty, fillToken);
            b.SetResourceReference(Control.BorderBrushProperty, borderToken);
            return b;
        }
    }
}
