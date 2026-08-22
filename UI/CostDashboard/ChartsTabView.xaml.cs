using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.CostDashboard
{
    /// <summary>
    /// Charts tab: five custom-drawn WPF charts (donut, line, per-m² bars,
    /// cost drivers, cost by level) + export buttons. Pure WPF shapes — no
    /// chart library. All figures come from <see cref="MockChartsData"/>;
    /// brushes resolve from the token dictionary by key. No Revit dependency.
    /// </summary>
    public partial class ChartsTabView : UserControl
    {
        public MockDashboardModel Model { get; }
        public MockChartsModel Charts { get; }

        public ChartsTabView()
        {
            InitializeComponent();
            Model = MockDashboardData.Create();
            Charts = MockChartsData.Create();
            Apply();
            LineCanvas.SizeChanged += (_, __) => DrawLineChart();
        }

        private void Apply()
        {
            // Card 1 — cost by discipline
            DisciplineHeaderText.Text = Charts.DisciplineHeader;
            DisciplineHintText.Text = Charts.DisciplineHint;
            DonutTotalText.Text = Charts.DonutTotal;
            DonutTotalLabelText.Text = Charts.DonutTotalLabel;
            DisciplineNoteText.Text = Charts.DisciplineNote;
            DrawDonut();
            BuildLegend();

            // Card 2 — cost by revision
            RevisionHeaderText.Text = Charts.RevisionHeader;
            RevisionPillText.Text = Charts.RevisionPill;
            RevisionScaleNoteText.Text = Charts.RevisionScaleNote;
            RevisionNoteText.Inlines.Clear();
            RevisionNoteText.Inlines.Add(Charts.RevisionNotePrefix);
            RevisionNoteText.Inlines.Add(new System.Windows.Documents.Run(Charts.RevisionNoteAmount)
            {
                Foreground = Brush("Cd.Green"),
                FontWeight = FontWeights.SemiBold,
            });
            RevisionNoteText.Inlines.Add(Charts.RevisionNoteSuffix);

            // Card 3 — cost per m²
            PerM2HeaderText.Text = Charts.PerM2Header;
            PerM2GfaText.Text = Charts.PerM2Gfa;
            PerM2CurrencyText.Text = Charts.PerM2Currency;
            PerM2ValueText.Text = Charts.PerM2Value;
            PerM2UnitText.Text = Charts.PerM2Unit;
            PerM2PillText.Text = Charts.PerM2Pill;
            PerM2NoteText.Text = Charts.PerM2Note;
            BuildPerM2Bars();

            // Card 4 — top cost drivers
            DriversHeaderText.Text = Charts.DriversHeader;
            DriversHintText.Text = Charts.DriversHint;
            DriverRows.Children.Clear();
            for (int i = 0; i < Charts.Drivers.Count; i++)
            {
                if (i > 0)
                {
                    DriverRows.Children.Add(new Rectangle
                    {
                        Height = 1,
                        Fill = Brush("Cd.Divider"),
                        SnapsToDevicePixels = true,
                    });
                }
                DriverRows.Children.Add(BuildDriverRow(Charts.Drivers[i]));
            }

            // Card 5 — priced cost by level
            LevelsHeaderText.Text = Charts.LevelsHeader;
            LevelsHintText.Text = Charts.LevelsHint;
            BuildLevelRows();

            // Export buttons
            ExportPdfText.Text = Charts.ExportPdfLabel;
            ExportXlsxText.Text = Charts.ExportXlsxLabel;
        }

        // ───────────────────────── Card 1: donut ─────────────────────────

        private void DrawDonut()
        {
            DonutCanvas.Children.Clear();
            double size = DonutCanvas.Width;
            double thickness = 26;
            double r = (size - thickness) / 2;
            var c = new Point(size / 2, size / 2);

            // Full track behind segments (covers rounding gaps).
            DonutCanvas.Children.Add(new Ellipse
            {
                Width = size - thickness,
                Height = size - thickness,
                Margin = new Thickness(thickness / 2),
                Stroke = Brush("Cd.Track"),
                StrokeThickness = thickness,
            });

            double total = 0;
            foreach (var s in Charts.DonutSegments) total += s.Percent;
            if (total <= 0) return;

            double angle = -90; // 12 o'clock, clockwise
            foreach (var s in Charts.DonutSegments)
            {
                double sweep = 360.0 * s.Percent / total;
                double a0 = angle, a1 = angle + sweep;
                angle = a1;

                // Avoid a degenerate full-circle arc.
                if (sweep >= 359.99) a1 = a0 + 359.99;

                var p0 = Polar(c, r, a0);
                var p1 = Polar(c, r, a1);
                var fig = new PathFigure { StartPoint = p0, IsClosed = false, IsFilled = false };
                fig.Segments.Add(new ArcSegment(p1, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
                var geo = new PathGeometry();
                geo.Figures.Add(fig);

                DonutCanvas.Children.Add(new Path
                {
                    Data = geo,
                    Stroke = Brush(s.BrushKey),
                    StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Flat,
                    StrokeEndLineCap = PenLineCap.Flat,
                });
            }
        }

        private static Point Polar(Point c, double r, double deg)
        {
            double rad = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        private void BuildLegend()
        {
            DonutLegend.Children.Clear();
            for (int i = 0; i < Charts.DonutSegments.Count; i++)
            {
                var s = Charts.DonutSegments[i];
                var row = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

                var dot = new Ellipse
                {
                    Width = 10, Height = 10,
                    Fill = Brush(s.BrushKey),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                row.Children.Add(dot);

                var name = Text(s.Name, "Cd.Text");
                name.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                var pct = Text(s.PercentLabel, "Cd.Text");
                pct.FontWeight = FontWeights.Bold;
                pct.TextAlignment = TextAlignment.Right;
                pct.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(pct, 2);
                row.Children.Add(pct);

                DonutLegend.Children.Add(row);
            }
        }

        // ───────────────────────── Card 2: line chart ─────────────────────────

        private void DrawLineChart()
        {
            var canvas = LineCanvas;
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w <= 0 || h <= 0 || double.IsNaN(w) || double.IsNaN(h)) return;

            const double padLeft = 40, padRight = 24, padTop = 22, padBottom = 36;
            double plotW = w - padLeft - padRight;
            double plotH = h - padTop - padBottom;
            if (plotW <= 0 || plotH <= 0) return;

            double yMin = Charts.RevisionAxisMin, yMax = Charts.RevisionAxisMax, step = Charts.RevisionAxisStep;
            double yRange = yMax - yMin;
            if (yRange <= 0 || step <= 0) return;

            Func<double, double> Y = v => padTop + (yMax - v) / yRange * plotH;
            int n = Charts.Revisions.Count;
            Func<int, double> X = i => padLeft + (n == 1 ? plotW / 2 : plotW * i / (n - 1));

            var gridBrush = Brush("Ch.Gridline");
            var mutedBrush = Brush("Cd.TextMuted");
            var blue = Brush("Cd.Blue");

            // Gridlines + y-axis labels
            for (double v = yMin; v <= yMax + 1e-9; v += step)
            {
                double y = Y(v);
                canvas.Children.Add(new Line
                {
                    X1 = padLeft, X2 = w - padRight, Y1 = y, Y2 = y,
                    Stroke = gridBrush, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    SnapsToDevicePixels = true,
                });
                var lbl = Text(v.ToString("0.0") + "M", "Cd.Caption");
                lbl.TextAlignment = TextAlignment.Right;
                lbl.Width = padLeft - 8;
                Canvas.SetLeft(lbl, 0);
                Canvas.SetTop(lbl, y - 7);
                canvas.Children.Add(lbl);
            }

            // Area fill under the line
            var area = new PathFigure { StartPoint = new Point(X(0), Y(yMin)), IsClosed = true };
            for (int i = 0; i < n; i++) area.Segments.Add(new LineSegment(new Point(X(i), Y(Charts.Revisions[i].Value)), true));
            area.Segments.Add(new LineSegment(new Point(X(n - 1), Y(yMin)), true));
            var areaGeo = new PathGeometry();
            areaGeo.Figures.Add(area);
            canvas.Children.Add(new Path { Data = areaGeo, Fill = Brush("Ch.LineAreaFill") });

            // Line
            var line = new Polyline { Stroke = blue, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < n; i++) line.Points.Add(new Point(X(i), Y(Charts.Revisions[i].Value)));
            canvas.Children.Add(line);

            // Points + value labels + x-axis labels
            for (int i = 0; i < n; i++)
            {
                var p = Charts.Revisions[i];
                double x = X(i), y = Y(p.Value);

                if (p.IsCurrent)
                {
                    // Halo behind the current point
                    var halo = new Ellipse { Width = 22, Height = 22, Fill = Brush("Ch.LineAreaFill") };
                    Canvas.SetLeft(halo, x - 11);
                    Canvas.SetTop(halo, y - 11);
                    canvas.Children.Add(halo);
                }

                double d = p.IsCurrent ? 12 : 8;
                var dot = new Ellipse
                {
                    Width = d, Height = d,
                    Fill = p.IsCurrent ? blue : Brush("Cd.Card"),
                    Stroke = blue, StrokeThickness = 2,
                };
                Canvas.SetLeft(dot, x - d / 2);
                Canvas.SetTop(dot, y - d / 2);
                canvas.Children.Add(dot);

                var val = Text(p.ValueLabel, "Cd.Caption");
                val.Foreground = p.IsCurrent ? blue : Brush("Cd.TextSecondary");
                val.FontWeight = p.IsCurrent ? FontWeights.Bold : FontWeights.SemiBold;
                val.Width = 40;
                val.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(val, x - 20);
                Canvas.SetTop(val, y - 20);
                canvas.Children.Add(val);

                var ax = Text(p.AxisLabel, "Cd.Caption");
                ax.FontWeight = FontWeights.SemiBold;
                ax.Foreground = p.IsCurrent ? blue : mutedBrush;
                ax.Width = 70;
                ax.TextAlignment = p.IsCurrent ? TextAlignment.Right : TextAlignment.Center;
                Canvas.SetLeft(ax, p.IsCurrent ? x - 70 + 8 : x - 35);
                Canvas.SetTop(ax, h - padBottom + 14);
                canvas.Children.Add(ax);
            }
        }

        // ───────────────────────── Card 3: per m² bars ─────────────────────────

        private void BuildPerM2Bars()
        {
            var g = PerM2Bars;
            g.Children.Clear();
            double max = Math.Max(Charts.ThisDesign, Charts.JkrMedian);
            if (max <= 0) max = 1;

            AddHBarRow(g, 0, Charts.ThisDesignLabel, Charts.ThisDesign / max, Brush("Cd.Blue"),
                       Charts.ThisDesign.ToString("N0"), false, 0);
            AddHBarRow(g, 1, Charts.JkrMedianLabel, Charts.JkrMedian / max, Brush("Ch.MedianBar"),
                       Charts.JkrMedian.ToString("N0"), false, 10);
        }

        /// <summary>
        /// [label] [track with fill at fraction] [value]. Row index maps to grid row.
        /// </summary>
        private void AddHBarRow(Grid g, int row, string label, double fraction, Brush fill,
                                string value, bool labelAccent, double topMargin, Brush labelBrush = null)
        {
            while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = Text(label, "Cd.Text");
            lbl.VerticalAlignment = VerticalAlignment.Center;
            lbl.Margin = new Thickness(0, topMargin, 12, 0);
            if (labelBrush != null) lbl.Foreground = labelBrush;
            if (labelAccent) lbl.FontWeight = FontWeights.SemiBold;
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);
            g.Children.Add(lbl);

            var bar = new ProgressBar
            {
                Style = (Style)FindResource("Cd.DisciplineBar"),
                Height = 12,
                Minimum = 0,
                Maximum = 100,
                Value = Math.Max(0, Math.Min(100, fraction * 100)),
                Foreground = fill,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, topMargin, 0, 0),
            };
            Grid.SetRow(bar, row);
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);

            var val = Text(value, "Cd.Text");
            val.FontWeight = FontWeights.Bold;
            val.TextAlignment = TextAlignment.Right;
            val.VerticalAlignment = VerticalAlignment.Center;
            val.Margin = new Thickness(12, topMargin, 0, 0);
            Grid.SetRow(val, row);
            Grid.SetColumn(val, 2);
            g.Children.Add(val);
        }

        // ───────────────────────── Card 4: cost drivers ─────────────────────────

        /// <summary>
        /// [pill] [name bold  discipline · qty] ... [cost bold] [pct grey]
        ///        [────────── bar (Value = pct / max pct) ──────────]
        /// </summary>
        private FrameworkElement BuildDriverRow(CostDriver d)
        {
            var accent = Brush(d.BrushKey);
            var soft = Brush(d.SoftBrushKey);

            var grid = new Grid { Margin = new Thickness(0, 11, 0, 11) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Discipline pill on its soft tint
            var pill = new Border
            {
                Background = soft,
                CornerRadius = (CornerRadius)FindResource("Cd.Radius.Pill"),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var pillText = Text(d.Discipline, "Cd.Caption");
            pillText.Foreground = accent;
            pillText.FontWeight = FontWeights.SemiBold;
            pill.Child = pillText;
            Grid.SetColumn(pill, 0);
            grid.Children.Add(pill);

            // Name + discipline/qty line
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var name = Text(d.Name, "Cd.Text");
            name.FontWeight = FontWeights.SemiBold;
            left.Children.Add(name);
            var sub = Text(d.DisciplineLine, "Cd.Body");
            sub.FontSize = (double)FindResource("Cd.FontSize.Caption");
            sub.Margin = new Thickness(8, 0, 0, 0);
            sub.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(sub);
            Grid.SetColumn(left, 1);
            grid.Children.Add(left);

            // Cost + percent
            var cost = Text(d.Cost, "Cd.Text");
            cost.FontWeight = FontWeights.Bold;
            cost.TextAlignment = TextAlignment.Right;
            cost.VerticalAlignment = VerticalAlignment.Center;
            cost.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(cost, 2);
            grid.Children.Add(cost);

            var pct = Text(d.PercentLabel, "Cd.Body");
            pct.TextAlignment = TextAlignment.Right;
            pct.VerticalAlignment = VerticalAlignment.Center;
            pct.Width = 36;
            Grid.SetColumn(pct, 3);
            grid.Children.Add(pct);

            // Bar: longest driver = full width
            int maxPct = 1;
            foreach (var x in Charts.Drivers) maxPct = Math.Max(maxPct, x.Percent);
            var bar = new ProgressBar
            {
                Style = (Style)FindResource("Cd.DisciplineBar"),
                Value = 100.0 * d.Percent / maxPct,
                Foreground = accent,
                Margin = new Thickness(0, 8, 0, 0),
            };
            Grid.SetRow(bar, 1);
            Grid.SetColumn(bar, 0);
            Grid.SetColumnSpan(bar, 4);
            grid.Children.Add(bar);

            return grid;
        }

        // ───────────────────────── Card 5: cost by level ─────────────────────────

        private void BuildLevelRows()
        {
            var g = LevelRows;
            g.Children.Clear();
            g.RowDefinitions.Clear();

            double max = 0;
            foreach (var l in Charts.LevelCosts) if (l.Cost.HasValue) max = Math.Max(max, l.Cost.Value);
            if (max <= 0) max = 1;

            // Blue tones, darkest for the largest priced level; Unassigned = orange.
            string[] blueKeys = { "Cd.Blue", "Ch.BlueMid", "Ch.BlueLight", "Ch.BlueLighter" };
            int blueIdx = 0;

            for (int i = 0; i < Charts.LevelCosts.Count; i++)
            {
                var l = Charts.LevelCosts[i];
                Brush fill;
                Brush labelBrush = null;
                if (l.IsUnassigned)
                {
                    fill = Brush("Cd.Orange");
                    labelBrush = fill;
                }
                else
                {
                    fill = Brush(blueKeys[Math.Min(blueIdx, blueKeys.Length - 1)]);
                    if (l.Cost.HasValue) blueIdx++;
                }

                double fraction = l.Cost.HasValue ? l.Cost.Value / max : 0;
                // Keep tiny values visible as a sliver, as in the reference.
                if (l.Cost.HasValue && fraction < 0.02) fraction = 0.02;

                AddHBarRow(g, i, l.Name, fraction, fill, l.CostLabel, true, i == 0 ? 0 : 12, labelBrush);
            }
        }

        // ───────────────────────── Export buttons (mock, no-op) ─────────────────────────

        private void OnExportPdf(object sender, RoutedEventArgs e) { }
        private void OnExportXlsx(object sender, RoutedEventArgs e) { }

        // ───────────────────────── helpers ─────────────────────────

        private TextBlock Text(string text, string styleKey) => new TextBlock
        {
            Text = text,
            Style = (Style)FindResource(styleKey),
        };

        private Brush Brush(string key) =>
            TryFindResource(key) as Brush ?? (Brush)FindResource("Cd.TextMuted");
    }
}
