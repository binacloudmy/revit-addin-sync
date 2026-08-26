using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Cad2Bim.Views.Rendering {
    using Cad2Bim.ViewModels;
    using Cad2Bim.ViewModels.Shapes;
    using Point = System.Windows.Point; // Cad2Bim.Point (model record) would shadow it here

    /// <summary>
    /// Hosts each LayerViewModel as one DrawingVisual, composited in collection order and
    /// sharing a single CAD-to-screen MatrixTransform (Y-flip + pan + zoom). Pan/zoom only
    /// mutate the matrix; layers re-render when their items change or after a zoom settles
    /// (to keep pen widths screen-constant). Ported from the cad2bim Viewer.
    /// </summary>
    public sealed class CadViewport : FrameworkElement {
        private const double MinScale = 1e-6;
        private const double MaxScale = 1e6;
        private const double BaseWidthPx = 1.0;
        private const double HighlightWidthPx = 3.0;
        private const byte HighlightAlpha = 0xB0;

        private static readonly SolidColorBrush BaseStroke = Frozen(Color.FromRgb(0x88, 0x88, 0x88));

        // Same highlight palette as the cad2bim Viewer's ClassificationHighlightLayer.
        private static readonly Color[] HighlightPalette = [
            Color.FromRgb(0xFF, 0x8C, 0x00),   // orange
            Color.FromRgb(0x00, 0xCE, 0xD1),   // teal
            Color.FromRgb(0xDA, 0x70, 0xD6),   // orchid
            Color.FromRgb(0x7C, 0xFC, 0x00),   // lawn green
        ];

        public static readonly DependencyProperty LayersSourceProperty = DependencyProperty.Register(
            nameof(LayersSource), typeof(IEnumerable<LayerViewModel>), typeof(CadViewport),
            new PropertyMetadata(null, (d, e) => ((CadViewport)d).OnLayersSourceChanged(e)));

        public static readonly DependencyProperty ContentBoundsProperty = DependencyProperty.Register(
            nameof(ContentBounds), typeof(Rect), typeof(CadViewport),
            new PropertyMetadata(Rect.Empty, (d, _) => ((CadViewport)d).OnContentBoundsChanged()));

        private readonly VisualCollection visuals;
        private readonly List<(LayerViewModel Layer, DrawingVisual Visual)> layers = [];
        private readonly Dictionary<LayerViewModel, (IReadOnlyList<object> Items, Geometry? Geometry)> polylineCache = [];
        private readonly MatrixTransform transform = new(Matrix.Identity);
        private readonly DispatcherTimer zoomSettleTimer;

        private bool fitted;
        private bool panning;
        private Point lastMousePosition;

        public CadViewport() {
            visuals = new VisualCollection(this);
            zoomSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            zoomSettleTimer.Tick += (_, _) => {
                zoomSettleTimer.Stop();
                RenderAllLayers();
            };

            ClipToBounds = true;
            SizeChanged += OnSizeChanged;
        }

        public IEnumerable<LayerViewModel>? LayersSource {
            get => (IEnumerable<LayerViewModel>?)GetValue(LayersSourceProperty);
            set => SetValue(LayersSourceProperty, value);
        }

        public Rect ContentBounds {
            get => (Rect)GetValue(ContentBoundsProperty);
            set => SetValue(ContentBoundsProperty, value);
        }

        private double Scale => transform.Matrix.M11;

        protected override int VisualChildrenCount => visuals.Count;

        protected override Visual GetVisualChild(int index) => visuals[index];

        protected override void OnRender(DrawingContext dc) {
            // Opaque backdrop doubles as the mouse hit-test surface.
            dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        }

        private void OnLayersSourceChanged(DependencyPropertyChangedEventArgs e) {
            if (e.OldValue is INotifyCollectionChanged oldIncc) {
                oldIncc.CollectionChanged -= OnLayersCollectionChanged;
            }
            if (e.NewValue is INotifyCollectionChanged newIncc) {
                newIncc.CollectionChanged += OnLayersCollectionChanged;
            }

            RebuildLayers();
        }

        private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildLayers();

        private void RebuildLayers() {
            foreach ((LayerViewModel layer, _) in layers) {
                layer.PropertyChanged -= OnLayerPropertyChanged;
            }

            layers.Clear();
            polylineCache.Clear();
            visuals.Clear();

            foreach (LayerViewModel layer in LayersSource ?? []) {
                var visual = new DrawingVisual { Transform = transform, Opacity = layer.IsVisible ? 1.0 : 0.0 };
                layer.PropertyChanged += OnLayerPropertyChanged;
                layers.Add((layer, visual));
                visuals.Add(visual);
            }

            RenderAllLayers();
        }

        private void OnLayerPropertyChanged(object? sender, PropertyChangedEventArgs e) {
            foreach ((LayerViewModel layer, DrawingVisual visual) in layers) {
                if (!ReferenceEquals(layer, sender)) {
                    continue;
                }

                if (e.PropertyName == nameof(LayerViewModel.IsVisible)) {
                    visual.Opacity = layer.IsVisible ? 1.0 : 0.0;
                } else if (e.PropertyName == nameof(LayerViewModel.Items)) {
                    RenderLayer(layer, visual);
                }
            }
        }

        private void OnContentBoundsChanged() {
            // New document: refit next chance we have a size.
            fitted = false;
            if (ActualWidth > 0 && ActualHeight > 0) {
                FitToExtents();
                RenderAllLayers();
                fitted = true;
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e) {
            if (!fitted && layers.Count > 0) {
                FitToExtents();
                RenderAllLayers();
                fitted = true;
            }
        }

        private void FitToExtents() {
            Rect bounds = ContentBounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0) {
                return;
            }

            double scale = 0.95 * Math.Min(ActualWidth / bounds.Width, ActualHeight / bounds.Height);
            scale = Math.Clamp(scale, MinScale, MaxScale);

            double centerX = bounds.X + (bounds.Width / 2.0);
            double centerY = bounds.Y + (bounds.Height / 2.0);

            // CAD (x, y) -> screen (s*x + tx, -s*y + ty), content center at viewport center.
            transform.Matrix = new Matrix(
                scale, 0, 0, -scale,
                (ActualWidth / 2.0) - (scale * centerX),
                (ActualHeight / 2.0) + (scale * centerY));
        }

        protected override void OnMouseDown(MouseButtonEventArgs e) {
            if (e.ChangedButton is MouseButton.Left or MouseButton.Middle) {
                panning = true;
                lastMousePosition = e.GetPosition(this);
                CaptureMouse();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            if (panning) {
                Point position = e.GetPosition(this);
                Matrix matrix = transform.Matrix;
                matrix.Translate(position.X - lastMousePosition.X, position.Y - lastMousePosition.Y);
                transform.Matrix = matrix;
                lastMousePosition = position;
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e) {
            if (panning && e.ChangedButton is MouseButton.Left or MouseButton.Middle) {
                panning = false;
                ReleaseMouseCapture();
            }

            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e) {
            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            double newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
            factor = newScale / Scale;

            Point position = e.GetPosition(this);
            Matrix matrix = transform.Matrix;
            matrix.ScaleAt(factor, factor, position.X, position.Y);
            transform.Matrix = matrix;

            // Re-render once zooming settles so pen widths stay screen-constant.
            zoomSettleTimer.Stop();
            zoomSettleTimer.Start();

            base.OnMouseWheel(e);
        }

        private void RenderAllLayers() {
            foreach ((LayerViewModel layer, DrawingVisual visual) in layers) {
                RenderLayer(layer, visual);
            }
        }

        private void RenderLayer(LayerViewModel layer, DrawingVisual visual) {
            double scale = Math.Max(Scale, MinScale);
            Pen pen = layer.HighlightIndex is int index
                ? new Pen(Frozen(WithAlpha(HighlightPalette[index % HighlightPalette.Length])), HighlightWidthPx / scale)
                : new Pen(BaseStroke, BaseWidthPx / scale);
            pen.Freeze();

            using DrawingContext dc = visual.RenderOpen();

            // A raw CAD file is tens of thousands of polylines; one batched geometry strokes far
            // faster than a call per item, and it only depends on Items, not on the current zoom.
            if (BatchedPolylines(layer) is Geometry batch) {
                dc.DrawGeometry(null, pen, batch);
            }

            foreach (object item in layer.Items) {
                if (item is not PolylineShape) {
                    Draw(dc, pen, item);
                }
            }
        }

        private Geometry? BatchedPolylines(LayerViewModel layer) {
            if (polylineCache.TryGetValue(layer, out var cached) && ReferenceEquals(cached.Items, layer.Items)) {
                return cached.Geometry;
            }

            StreamGeometry? geometry = null;
            StreamGeometryContext? ctx = null;

            foreach (object item in layer.Items) {
                if (item is not PolylineShape polyline || polyline.Points.Count < 2) {
                    continue;
                }

                geometry ??= new StreamGeometry();
                ctx ??= geometry.Open();

                (double x, double y) = polyline.Points[0];
                ctx.BeginFigure(new Point(x, y), false, polyline.IsClosed);
                ctx.PolyLineTo(
                    polyline.Points.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(),
                    true, false);
            }

            ctx?.Close();
            geometry?.Freeze();

            polylineCache[layer] = (layer.Items, geometry);
            return geometry;
        }

        private static void Draw(DrawingContext dc, Pen pen, object item) {
            switch (item) {
                case SegmentShape s:
                    dc.DrawLine(pen, new Point(s.X1, s.Y1), new Point(s.X2, s.Y2));
                    break;
                case ArcShape a:
                    dc.DrawEllipse(null, pen, new Point(a.Cx, a.Cy), a.Radius, a.Radius);
                    break;
                case WallShape w:
                    dc.DrawLine(pen, new Point(w.A.X1, w.A.Y1), new Point(w.A.X2, w.A.Y2));
                    dc.DrawLine(pen, new Point(w.B.X1, w.B.Y1), new Point(w.B.X2, w.B.Y2));
                    break;
            }
        }

        private static Color WithAlpha(Color color) => Color.FromArgb(HighlightAlpha, color.R, color.G, color.B);

        private static SolidColorBrush Frozen(Color color) {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
