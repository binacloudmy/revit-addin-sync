using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cad2Bim;
using Cad2Bim.ViewModels;
using Cad2Bim.Views.Rendering;
using RevitWebAppSync.Services.CadToBim;
using CadWall = Cad2Bim.Wall;
using CadPoint = Cad2Bim.Point;
using CadSegment = Cad2Bim.Segment;
using CadArc = Cad2Bim.Arc;
using Point = System.Windows.Point;

namespace RevitWebAppSync.UI.CadToBim
{
    public enum ViewportMode { Pan, Erase, Brush }

    /// <summary>
    /// The upstream CadViewport (sealed, kept verbatim) plus an overlay drawn ABOVE its layer
    /// visuals: wall bands, erased walls, openings, rooms, brush boxes and the live drag box.
    ///
    /// Composition, not inheritance. The viewport is the first child of this Grid, the overlay
    /// host the second, and the overlay's DrawingVisuals share the viewport's own MatrixTransform
    /// instance (the one it assigns to every layer visual), so pan and zoom move the overlay for
    /// free; only pen widths are re-rendered once a zoom settles, exactly as the viewport does.
    ///
    /// Mouse: in Pan mode nothing here touches the mouse. In Erase and Brush modes the LEFT
    /// button is taken in the Preview (tunnelling) phase and marked handled, so the viewport
    /// never starts a pan; middle-button pan and wheel zoom still reach the viewport untouched.
    /// </summary>
    public sealed class CadOverlayViewport : Grid
    {
        public const double EraseToleranceMm = 150.0;

        private const double MinDragPx = 4.0;
        private const double WallCenterPx = 2.0;
        private const double WallHoverPx = 4.0;
        private const double WallBandMinPx = 3.0;
        private const byte WallBandAlpha = 0x2E;
        private const double OpeningPx = 2.2;
        private const double RoomPx = 1.0;
        private const double ErasePx = 1.8;
        private const double BoxPx = 1.2;
        private const byte BoxFillAlpha = 0x24;
        private const double LabelPx = 10.5;
        private const int ArcSteps = 12;

        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(ViewportMode), typeof(CadOverlayViewport),
            new FrameworkPropertyMetadata(ViewportMode.Pan, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((CadOverlayViewport)d).OnModeChanged()));

        public static readonly DependencyProperty LayersSourceProperty = DependencyProperty.Register(
            nameof(LayersSource), typeof(IEnumerable<LayerViewModel>), typeof(CadOverlayViewport),
            new PropertyMetadata(null, (d, e) => ((CadOverlayViewport)d).viewport.LayersSource = (IEnumerable<LayerViewModel>)e.NewValue));

        public static readonly DependencyProperty ContentBoundsProperty = DependencyProperty.Register(
            nameof(ContentBounds), typeof(Rect), typeof(CadOverlayViewport),
            new PropertyMetadata(Rect.Empty, (d, e) => ((CadOverlayViewport)d).viewport.ContentBounds = (Rect)e.NewValue));

        public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(
            nameof(Backdrop), typeof(Brush), typeof(CadOverlayViewport),
            new PropertyMetadata(null, (d, e) => ((CadOverlayViewport)d).viewport.Backdrop = (Brush)e.NewValue ?? Brushes.White));

        public event Action<CadWall> WallClicked;
        public event Action<BoxMm> BoxDragged;
        /// <summary>Cursor position in drawing millimetres, for the coords readout.</summary>
        public event Action<double, double> CursorMoved;

        private static readonly Typeface LabelFace =
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private readonly CadViewport viewport;
        private readonly OverlayHost overlay;
        private readonly DispatcherTimer settleTimer;
        private MatrixTransform shared;
        private double lastScale;

        private IReadOnlyList<CadWall> active = new CadWall[0];
        private IReadOnlyList<CadWall> erased = new CadWall[0];
        private IReadOnlyList<Opening> openings = new Opening[0];
        private IReadOnlyList<Space> spaces = new Space[0];
        private IReadOnlyList<BoxMm> boxes = new BoxMm[0];
        private SegmentIndex activeIndex;
        private CadWall hover;

        private bool pressed;
        private bool dragging;
        private Point dragStart;

        public CadOverlayViewport()
        {
            viewport = new CadViewport { Backdrop = Brushes.White };
            overlay = new OverlayHost();
            Children.Add(viewport);
            Children.Add(overlay);

            settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            settleTimer.Tick += (_, __) => { settleTimer.Stop(); RedrawAll(); };
        }

        public ViewportMode Mode
        {
            get => (ViewportMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public IEnumerable<LayerViewModel> LayersSource
        {
            get => (IEnumerable<LayerViewModel>)GetValue(LayersSourceProperty);
            set => SetValue(LayersSourceProperty, value);
        }

        public Rect ContentBounds
        {
            get => (Rect)GetValue(ContentBoundsProperty);
            set => SetValue(ContentBoundsProperty, value);
        }

        public Brush Backdrop
        {
            get => (Brush)GetValue(BackdropProperty);
            set => SetValue(BackdropProperty, value);
        }

        /// <summary>The wrapped upstream viewport (pan/zoom/layers live there).</summary>
        public CadViewport Viewport => viewport;

        // ───────── public surface ─────────

        public void SetOverlay(IReadOnlyList<CadWall> activeWalls, IReadOnlyList<CadWall> erasedWalls,
                               IReadOnlyList<Opening> openingList, IReadOnlyList<Space> spaceList,
                               IReadOnlyList<BoxMm> boxList)
        {
            active = activeWalls ?? new CadWall[0];
            erased = erasedWalls ?? new CadWall[0];
            openings = openingList ?? new Opening[0];
            spaces = spaceList ?? new Space[0];
            boxes = boxList ?? new BoxMm[0];
            activeIndex = active.Count > 0
                ? new SegmentIndex(active.Select(w => w.Centerline).ToList(), 1000)
                : null;
            hover = null;
            RedrawAll();
        }

        public CadWall HitTestWall(Point screenPt, double toleranceMm)
        {
            Point? mm = ToMm(screenPt);
            if (!mm.HasValue) return null;
            return NearestWall(active, activeIndex, mm.Value.X, mm.Value.Y, toleranceMm);
        }

        /// <summary>Re-render every overlay visual (call after a theme swap).</summary>
        public void Redraw() => RedrawAll();

        /// <summary>
        /// The active wall whose centreline passes within <paramref name="tolMm"/> of the point,
        /// nearest first. <paramref name="index"/> must have been built over the SAME list in the
        /// same order (it yields list indices); null = plain scan.
        /// </summary>
        internal static CadWall NearestWall(IReadOnlyList<CadWall> walls, SegmentIndex index,
                                            double xMm, double yMm, double tolMm)
        {
            if (walls == null || walls.Count == 0 || tolMm < 0) return null;

            IEnumerable<int> candidates = index != null
                ? index.Near(new CadSegment(new CadPoint(xMm, yMm), new CadPoint(xMm, yMm)), tolMm)
                : Enumerable.Range(0, walls.Count);

            CadWall best = null;
            double bestDistance = tolMm;
            bool found = false;

            foreach (int i in candidates)
            {
                if (i < 0 || i >= walls.Count) continue;
                double d = DistanceToSegment(xMm, yMm, walls[i].Centerline);
                if (d > tolMm) continue;
                if (found && d >= bestDistance) continue;
                best = walls[i];
                bestDistance = d;
                found = true;
            }

            return best;
        }

        /// <summary>Perpendicular distance clamped to the segment's own extent.</summary>
        internal static double DistanceToSegment(double x, double y, CadSegment s)
        {
            double ax = s.P1.x, ay = s.P1.y, bx = s.P2.x, by = s.P2.y;
            double dx = bx - ax, dy = by - ay;
            double len2 = (dx * dx) + (dy * dy);
            double t = len2 <= 0 ? 0 : (((x - ax) * dx) + ((y - ay) * dy)) / len2;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            double px = ax + (t * dx) - x;
            double py = ay + (t * dy) - y;
            return Math.Sqrt((px * px) + (py * py));
        }

        // ───────── shared transform ─────────

        private MatrixTransform Shared()
        {
            if (shared != null) return shared;

            MatrixTransform found = null;
            if (VisualTreeHelper.GetChildrenCount(viewport) > 0 &&
                VisualTreeHelper.GetChild(viewport, 0) is DrawingVisual layerVisual)
            {
                found = layerVisual.Transform as MatrixTransform;
            }

            if (found == null)
            {
                // No layer visual yet (nothing bound, or an empty collection): the viewport keeps
                // the one MatrixTransform it hands every layer in a private readonly field.
                FieldInfo field = typeof(CadViewport).GetField("transform", BindingFlags.Instance | BindingFlags.NonPublic);
                found = field?.GetValue(viewport) as MatrixTransform;
            }

            if (found == null) return null;

            shared = found;
            lastScale = shared.Matrix.M11;
            shared.Changed += OnTransformChanged;
            overlay.Attach(shared);
            return shared;
        }

        private void OnTransformChanged(object sender, EventArgs e)
        {
            double scale = shared.Matrix.M11;
            if (Math.Abs(scale - lastScale) > 1e-12)
            {
                // Zoom: pen widths are in CAD units, re-render once it settles (viewport does the same).
                lastScale = scale;
                settleTimer.Stop();
                settleTimer.Start();
            }

            // Room names are screen-space text; they follow every pan immediately.
            RedrawLabels();
        }

        private Point? ToMm(Point screen)
        {
            MatrixTransform t = Shared();
            if (t == null) return null;
            Matrix m = t.Matrix;
            if (!m.HasInverse) return null;
            m.Invert();
            return m.Transform(screen);
        }

        private BoxMm? ToBoxMm(Rect screenRect)
        {
            Point? a = ToMm(screenRect.TopLeft);
            Point? b = ToMm(screenRect.BottomRight);
            if (!a.HasValue || !b.HasValue) return null;
            return new BoxMm(
                Math.Min(a.Value.X, b.Value.X), Math.Min(a.Value.Y, b.Value.Y),
                Math.Max(a.Value.X, b.Value.X), Math.Max(a.Value.Y, b.Value.Y));
        }

        private static Point P(CadPoint p) => new Point(p.x, p.y);

        // ───────── mouse ─────────

        private void OnModeChanged()
        {
            pressed = false;
            dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            hover = null;
            overlay.SetDrag(null, Colors.Transparent);
            Cursor = Mode == ViewportMode.Pan ? Cursors.Hand : Cursors.Cross;
            RedrawWalls();
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (Mode == ViewportMode.Pan)
            {
                base.OnPreviewMouseLeftButtonDown(e);
                return;
            }

            pressed = true;
            dragging = false;
            dragStart = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;    // the viewport never sees it, so no pan starts
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            Point position = e.GetPosition(this);
            Point? mm = ToMm(position);
            if (mm.HasValue) CursorMoved?.Invoke(mm.Value.X, mm.Value.Y);

            if (Mode == ViewportMode.Pan)
            {
                base.OnPreviewMouseMove(e);
                return;
            }

            if (Mode == ViewportMode.Brush && pressed)
            {
                if (!dragging &&
                    (Math.Abs(position.X - dragStart.X) >= MinDragPx || Math.Abs(position.Y - dragStart.Y) >= MinDragPx))
                {
                    dragging = true;
                }

                if (dragging)
                {
                    overlay.SetDrag(RectOf(dragStart, position), Colour("CadToBim.Brush", 0x12, 0xA3, 0xB4));
                }

                e.Handled = true;
                return;
            }

            if (Mode == ViewportMode.Erase && !pressed)
            {
                CadWall under = HitTestWall(position, EraseToleranceMm);
                if (!ReferenceEquals(under, hover))
                {
                    hover = under;
                    RedrawWalls();
                }
                Cursor = under != null ? Cursors.Hand : Cursors.Cross;
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!pressed)
            {
                base.OnPreviewMouseLeftButtonUp(e);
                return;
            }

            pressed = false;
            ReleaseMouseCapture();
            e.Handled = true;
            Point position = e.GetPosition(this);

            if (Mode == ViewportMode.Erase)
            {
                CadWall wall = HitTestWall(position, EraseToleranceMm);
                if (wall != null) WallClicked?.Invoke(wall);
                return;
            }

            if (Mode == ViewportMode.Brush)
            {
                overlay.SetDrag(null, Colors.Transparent);
                if (!dragging) return;
                dragging = false;

                Rect box = RectOf(dragStart, position);
                if (box.Width < MinDragPx || box.Height < MinDragPx) return;

                BoxMm? mmBox = ToBoxMm(box);
                if (mmBox.HasValue) BoxDragged?.Invoke(mmBox.Value);
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            pressed = false;
            dragging = false;
            overlay.SetDrag(null, Colors.Transparent);
            base.OnLostMouseCapture(e);
        }

        private static Rect RectOf(Point a, Point b) =>
            new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)),
                     new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));

        // ───────── rendering ─────────

        private double CurrentScale()
        {
            MatrixTransform t = Shared();
            return t == null ? 0 : Math.Max(Math.Abs(t.Matrix.M11), 1e-6);
        }

        private void RedrawAll()
        {
            double scale = CurrentScale();
            if (scale <= 0) return;
            RedrawWalls(scale);
            RedrawErased(scale);
            RedrawOpenings(scale);
            RedrawRooms(scale);
            RedrawBoxes(scale);
            RedrawLabels();
        }

        private void RedrawWalls()
        {
            double scale = CurrentScale();
            if (scale > 0) RedrawWalls(scale);
        }

        private void RedrawWalls(double scale)
        {
            Color wall = Colour("CadToBim.Wall", 0x1E, 0x9E, 0x4F);
            SolidColorBrush bandBrush = Frozen(Color.FromArgb(WallBandAlpha, wall.R, wall.G, wall.B));
            SolidColorBrush lineBrush = Frozen(wall);
            Pen centre = FrozenPen(lineBrush, WallCenterPx / scale);
            Pen hot = FrozenPen(lineBrush, WallHoverPx / scale);
            var bands = new Dictionary<int, Pen>();

            using (DrawingContext dc = overlay.Walls.RenderOpen())
            {
                foreach (CadWall w in active)
                {
                    Point a = P(w.Centerline.P1), b = P(w.Centerline.P2);

                    // Band = real thickness, but never thinner than 3 px on screen.
                    int key = (int)Math.Round(Math.Max(w.Thickness, WallBandMinPx / scale));
                    Pen band;
                    if (!bands.TryGetValue(key, out band))
                    {
                        band = FrozenPen(bandBrush, key);
                        bands[key] = band;
                    }

                    dc.DrawLine(band, a, b);
                    dc.DrawLine(ReferenceEquals(w, hover) ? hot : centre, a, b);
                }
            }
        }

        private void RedrawErased(double scale)
        {
            Pen pen = Dashed(Frozen(Colour("CadToBim.Erase", 0xD1, 0x3B, 0x3B)), ErasePx / scale, 6, 4);
            using (DrawingContext dc = overlay.Erased.RenderOpen())
            {
                foreach (CadWall w in erased)
                {
                    dc.DrawLine(pen, P(w.Centerline.P1), P(w.Centerline.P2));
                }
            }
        }

        private void RedrawOpenings(double scale)
        {
            Pen pen = FrozenPen(Frozen(Colour("CadToBim.Opening", 0x2B, 0x7D, 0xE9)), OpeningPx / scale);
            using (DrawingContext dc = overlay.Openings.RenderOpen())
            {
                foreach (Opening o in openings)
                {
                    foreach (GeometryElement g in o.Geometry)
                    {
                        if (g is CadSegment s) dc.DrawLine(pen, P(s.P1), P(s.P2));
                        else if (g is CadArc arc) DrawArc(dc, pen, arc);
                    }
                }
            }
        }

        // Tessellated rather than an ArcSegment: the shared transform flips Y, and a polyline
        // needs no reasoning about sweep direction under a mirror.
        private static void DrawArc(DrawingContext dc, Pen pen, CadArc arc)
        {
            double sweep = arc.SweepDegrees * Math.PI / 180.0;
            if (sweep <= 0) sweep = 2 * Math.PI;

            Point previous = P(arc.PointAt(arc.StartAngle));
            for (int i = 1; i <= ArcSteps; i++)
            {
                Point next = P(arc.PointAt(arc.StartAngle + (sweep * i / ArcSteps)));
                dc.DrawLine(pen, previous, next);
                previous = next;
            }

            // The leaf: hinge to the swing's start.
            dc.DrawLine(pen, P(arc.Center), P(arc.StartPoint));
        }

        private void RedrawRooms(double scale)
        {
            Pen pen = Dashed(Frozen(Colour("CadToBim.Room", 0xC7, 0x7D, 0x0A)), RoomPx / scale, 3, 4);
            using (DrawingContext dc = overlay.Rooms.RenderOpen())
            {
                foreach (Space s in spaces)
                {
                    List<CadPoint> boundary = s.Boundary;
                    if (boundary == null || boundary.Count < 3) continue;
                    for (int i = 0; i < boundary.Count; i++)
                    {
                        dc.DrawLine(pen, P(boundary[i]), P(boundary[(i + 1) % boundary.Count]));
                    }
                }
            }
        }

        private void RedrawBoxes(double scale)
        {
            Color c = Colour("CadToBim.Brush", 0x12, 0xA3, 0xB4);
            Brush fill = Frozen(Color.FromArgb(BoxFillAlpha, c.R, c.G, c.B));
            Pen pen = Dashed(Frozen(c), BoxPx / scale, 5, 3);
            using (DrawingContext dc = overlay.Boxes.RenderOpen())
            {
                foreach (BoxMm b in boxes)
                {
                    dc.DrawRectangle(fill, pen, new Rect(new Point(b.MinX, b.MinY), new Point(b.MaxX, b.MaxY)));
                }
            }
        }

        // Screen-space: FormattedText under the Y-flipped shared transform would render mirrored.
        private void RedrawLabels()
        {
            using (DrawingContext dc = overlay.Labels.RenderOpen())
            {
                if (shared == null) return;
                Matrix m = shared.Matrix;
                Brush brush = Frozen(Colour("CadToBim.Room", 0xC7, 0x7D, 0x0A));
                double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

                foreach (Space s in spaces)
                {
                    if (string.IsNullOrWhiteSpace(s.Name) || s.Boundary == null || s.Boundary.Count < 3) continue;

                    Point centre = m.Transform(new Point(s.Boundary.Average(p => p.x), s.Boundary.Average(p => p.y)));
                    if (centre.X < -200 || centre.Y < -200 || centre.X > ActualWidth + 200 || centre.Y > ActualHeight + 200) continue;

                    var text = new FormattedText("◆ " + s.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                                 LabelFace, LabelPx, brush, pixelsPerDip);
                    dc.DrawText(text, new Point(centre.X - (text.Width / 2), centre.Y - (text.Height / 2)));
                }
            }
        }

        // ───────── colours ─────────

        // Reads the CadToBim.* brush mounted on the panel (local theme dictionary, see
        // CadToBimTheme); falls back to the light mockup value when hosted elsewhere.
        private Color Colour(string key, byte r, byte g, byte b)
        {
            if (TryFindResource(key) is SolidColorBrush brush) return brush.Color;
            return Color.FromRgb(r, g, b);
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Brush brush, double width)
        {
            var pen = new Pen(brush, width);
            pen.Freeze();
            return pen;
        }

        // Dash lengths are multiples of the pen width, so they stay screen-constant like the width.
        private static Pen Dashed(Brush brush, double width, double on, double off)
        {
            var pen = new Pen(brush, width)
            {
                DashStyle = new DashStyle(new double[] { on, off }, 0),
                DashCap = PenLineCap.Flat,
            };
            pen.Freeze();
            return pen;
        }

        // ───────── overlay host ─────────

        /// <summary>
        /// Holds the overlay DrawingVisuals (bottom → top: boxes, rooms, openings, erased, walls,
        /// labels) and paints the live drag box in screen space. Never hit-test visible: every
        /// mouse decision is made by the Grid above in the Preview phase.
        /// </summary>
        private sealed class OverlayHost : FrameworkElement
        {
            private readonly VisualCollection visuals;
            private Rect? drag;
            private Pen dragPen;
            private Brush dragFill;

            public DrawingVisual Boxes { get; } = new DrawingVisual();
            public DrawingVisual Rooms { get; } = new DrawingVisual();
            public DrawingVisual Openings { get; } = new DrawingVisual();
            public DrawingVisual Erased { get; } = new DrawingVisual();
            public DrawingVisual Walls { get; } = new DrawingVisual();
            public DrawingVisual Labels { get; } = new DrawingVisual();

            public OverlayHost()
            {
                // Order matters: setting IsHitTestVisible makes WPF walk the visual children
                // synchronously (InvalidateForceInheritPropertyOnChildren → VisualChildrenCount),
                // so the collection must exist BEFORE any dependency property is touched.
                // Staging 0.0.70 crashed here with a NullReferenceException.
                visuals = new VisualCollection(this) { Boxes, Rooms, Openings, Erased, Walls, Labels };
                IsHitTestVisible = false;
            }

            public void Attach(MatrixTransform transform)
            {
                Boxes.Transform = transform;
                Rooms.Transform = transform;
                Openings.Transform = transform;
                Erased.Transform = transform;
                Walls.Transform = transform;
                // Labels stay in screen space on purpose.
            }

            public void SetDrag(Rect? rect, Color color)
            {
                drag = rect;
                if (rect.HasValue)
                {
                    var stroke = new SolidColorBrush(color);
                    stroke.Freeze();
                    dragPen = Dashed(stroke, BoxPx, 5, 3);
                    dragFill = Frozen(Color.FromArgb(BoxFillAlpha, color.R, color.G, color.B));
                }
                InvalidateVisual();
            }

            // Null-safe: base-class constructors can query these before our own ctor body runs.
            protected override int VisualChildrenCount => visuals?.Count ?? 0;

            protected override Visual GetVisualChild(int index) => visuals[index];

            protected override void OnRender(DrawingContext dc)
            {
                if (drag.HasValue) dc.DrawRectangle(dragFill, dragPen, drag.Value);
            }
        }
    }
}
