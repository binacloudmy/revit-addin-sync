using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Right-rail companion surface to the chat thread (design file
    /// docs/design/bina-copilot-v5.dc.html lines 345-405). Three tabs:
    ///   - Viewport: rendered floor plan with rooms + door markers + live count badge
    ///   - Elements: row list bound to CopilotViewModel.Highlights
    ///   - Logs:     in-pane event log (run events, tool calls, status changes)
    ///
    /// Renders plain XAML primitives only — no Storyboard (Revit pane constraint).
    /// Re-uses Cp.* tokens so the rail picks up the active theme automatically.
    /// </summary>
    public partial class RightRailView : UserControl
    {
        public enum RailTab { Viewport, Elements, Logs }

        private RailTab _active = RailTab.Viewport;
        private CopilotViewModel _vm;
        private INotifyCollectionChanged _highlights;
        private INotifyCollectionChanged _logs;
        private readonly DispatcherTimer _pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };

        // Plan canvas demo geometry — replaced when Highlights arrive (door markers).
        private bool _demoPlanDrawn;

        public RightRailView()
        {
            InitializeComponent();
            _pulse.Tick += (_, __) => PulsePlan();
            DataContextChanged += (_, __) => Bind();
            Loaded += (_, __) => DrawDemoPlanIfEmpty();
            Unloaded += (_, __) => { _pulse.Stop(); Unbind(); };
            // Default tab styling
            Activate(RailTab.Viewport);
        }

        // ────────────── Public hooks ──────────────
        public void SetActive(RailTab t) => Activate(t);
        public void SetCount(int found, int total, string label)
        {
            CountBadgeText.Text = $"{found} / {total} · {label}";
            CountBadge.Visibility = Visibility.Visible;
        }
        public void ClearCount() => CountBadge.Visibility = Visibility.Collapsed;
        public void AppendLog(string line) => AddLogRow(line);

        // ────────────── VM wiring ──────────────
        private void Bind()
        {
            Unbind();
            _vm = DataContext as CopilotViewModel;
            if (_vm == null) return;

            _highlights = _vm.Highlights as INotifyCollectionChanged;
            if (_highlights != null) _highlights.CollectionChanged += OnHighlightsChanged;
            RenderElementsFromVm();

            // Subscribe to the VM's INotifyPropertyChanged directly for
            // RunStatus / ToolActivity transitions, so the Logs tab picks them up.
            if (_vm is INotifyPropertyChanged inpc)
                inpc.PropertyChanged += OnVmPropertyChanged;
            else
                StartStatusPoll();
        }

        private void Unbind()
        {
            if (_highlights != null) _highlights.CollectionChanged -= OnHighlightsChanged;
            _highlights = null;
            if (_vm is INotifyPropertyChanged inpc) inpc.PropertyChanged -= OnVmPropertyChanged;
            _statusPoll?.Stop();
            _statusPoll = null;
            _vm = null;
        }

        private void OnHighlightsChanged(object sender, NotifyCollectionChangedEventArgs e)
            => RenderElementsFromVm();

        private void RenderElementsFromVm()
        {
            ElementsList.Children.Clear();
            if (_vm == null) return;
            int i = 0;
            foreach (var h in _vm.Highlights)
            {
                i++;
                ElementsList.Children.Add(ElementRow(i, h));
            }
            ElementsBadge.Text = i.ToString();
            EmptyElements.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Mirror markers on the plan
            RepaintMarkers();
        }

        private FrameworkElement ElementRow(int idx, HighlightMarker m)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 1),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            row.MouseEnter += (_, __) => row.Background = (Brush)FindResource("Cp.Hover");
            row.MouseLeave += (_, __) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, __) => HighlightInModel(m);

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            // door icon (Phosphor door-open path approximation)
            var icon = new Path
            {
                Width = 14, Height = 14, Stretch = Stretch.Uniform,
                Stroke = (Brush)FindResource("Cp.Accent"),
                StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                Data = Geometry.Parse("M4,4 h12 v16 h-4 v-6 a4,4 0 0 0 -4,-4 h-4 z M4,4 v16")
            };
            sp.Children.Add(icon);

            var id = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Cp.Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 56,
                Text = "ELM-" + idx.ToString("D3"),
            };
            sp.Children.Add(id);

            var name = new TextBlock
            {
                Text = string.IsNullOrEmpty(m.NewLabel) ? m.OldLabel : m.NewLabel,
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("Cp.Text"),
            };
            sp.Children.Add(name);

            // level chip
            var lvl = new TextBlock
            {
                Text = "L01", FontSize = 11,
                Foreground = (Brush)FindResource("Cp.Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            sp.Children.Add(lvl);

            row.Child = sp;
            return row;
        }

        private void HighlightInModel(HighlightMarker m)
        {
            // Existing selection pipeline is local to the addin; this hook is
            // intentionally non-blocking — a future iteration can route to
            // select_elements via McpJob, matching the chat's "Highlight in model".
        }

        // ────────────── Viewport tab ──────────────
        private void DrawDemoPlanIfEmpty()
        {
            if (_demoPlanDrawn) return;
            _demoPlanDrawn = true;
            if (PlanCanvas.ActualWidth < 10 || PlanCanvas.ActualHeight < 10)
            {
                SizeChanged += (_, __) => DrawDemoPlanIfEmpty();
                return;
            }
            DrawDemoRooms();
            _pulse.Start();
        }

        private void DrawDemoRooms()
        {
            PlanCanvas.Children.Clear();
            double w = PlanCanvas.ActualWidth;
            double h = PlanCanvas.ActualHeight;
            if (w < 50 || h < 50) return;
            // 9 rectangles in a 3x3 grid — design spec "sc-for list='{{ rooms }}'"
            var rooms = new (double X, double Y, double Wd, double Ht, string Label)[]
            {
                (0.04, 0.06, 0.26, 0.22, "R1"), (0.32, 0.06, 0.32, 0.22, "R2"), (0.66, 0.06, 0.30, 0.22, "R3"),
                (0.04, 0.32, 0.26, 0.30, "R4"), (0.32, 0.32, 0.32, 0.30, "R5"), (0.66, 0.32, 0.30, 0.30, "R6"),
                (0.04, 0.66, 0.26, 0.26, "R7"), (0.32, 0.66, 0.32, 0.26, "R8"), (0.66, 0.66, 0.30, 0.26, "R9"),
            };
            foreach (var r in rooms)
            {
                var rect = new Rectangle
                {
                    Width = r.Wd * w,
                    Height = r.Ht * h,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x2A)),
                    StrokeThickness = 1.2,
                    Fill = new SolidColorBrush(Color.FromArgb(0x0C, 0xE6, 0xE9, 0xEE)),
                    RadiusX = 2, RadiusY = 2,
                };
                Canvas.SetLeft(rect, r.X * w);
                Canvas.SetTop(rect, r.Y * h);
                PlanCanvas.Children.Add(rect);
            }
            RepaintMarkers();
        }

        private readonly List<Ellipse> _markers = new List<Ellipse>();
        private void RepaintMarkers()
        {
            // remove old door markers
            foreach (var m in _markers) PlanCanvas.Children.Remove(m);
            _markers.Clear();
            if (_vm == null) return;
            double w = PlanCanvas.ActualWidth;
            double h = PlanCanvas.ActualHeight;
            if (w < 50 || h < 50) return;
            int i = 0;
            foreach (var mk in _vm.Highlights)
            {
                i++;
                var dot = new Ellipse
                {
                    Width = 10, Height = 10,
                    Fill = (Brush)FindResource(mk.Warn ? "Cp.Amber" : "Cp.Accent"),
                };
                dot.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = mk.Warn ? 0 : 4 };
                Canvas.SetLeft(dot, mk.XPct * w - 5);
                Canvas.SetTop(dot, mk.YPct * h - 5);
                Panel.SetZIndex(dot, 5);
                PlanCanvas.Children.Add(dot);
                _markers.Add(dot);
            }
        }

        private void PulsePlan()
        {
            // Subtle "breathing" on accent dots so the viewport reads as live.
            // No Storyboard — pure per-tick opacity wobble inside the dispatcher.
            if (_markers.Count == 0) return;
            double t = (DateTime.Now.Millisecond % 1000) / 1000.0;
            double op = 0.55 + 0.45 * Math.Sin(t * Math.PI * 2);
            foreach (var dot in _markers)
            {
                if (dot.Fill is SolidColorBrush b)
                {
                    var c = b.Color; c.A = (byte)(255 * op);
                    dot.Fill = new SolidColorBrush(c);
                }
            }
        }

        // ────────────── Logs tab ──────────────
        private readonly List<(string Time, string Text)> _log = new List<(string, string)>();

        private void AddLogRow(string text)
        {
            var t = DateTime.Now.ToString("HH:mm:ss");
            _log.Add((t, text));
            if (_log.Count > 200) _log.RemoveAt(0);
            RebuildLogs();
        }

        private void RebuildLogs()
        {
            LogsList.Children.Clear();
            if (_log.Count == 0)
            {
                EmptyLogs.Visibility = Visibility.Visible;
                return;
            }
            EmptyLogs.Visibility = Visibility.Collapsed;
            foreach (var (t, text) in _log)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 1) };
                row.Children.Add(new TextBlock
                {
                    Text = t,
                    Foreground = (Brush)FindResource("Cp.Faint"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontSize = 11,
                    Width = 72,
                });
                row.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = (Brush)FindResource("Cp.Muted"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                });
                LogsList.Children.Add(row);
            }
            LogsScroller.ScrollToEnd();
        }

        // ────────────── Tab switching ──────────────
        private void OnTabPick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag &&
                Enum.TryParse<RailTab>(tag, out var t))
            {
                Activate(t);
            }
        }

        private void Activate(RailTab t)
        {
            _active = t;
            ViewportHost.Visibility = t == RailTab.Viewport ? Visibility.Visible : Visibility.Collapsed;
            ElementsHost.Visibility = t == RailTab.Elements ? Visibility.Visible : Visibility.Collapsed;
            LogsHost.Visibility = t == RailTab.Logs ? Visibility.Visible : Visibility.Collapsed;
            // visual hint via header text weight — keep it simple
            TabViewport.FontWeight = t == RailTab.Viewport ? FontWeights.SemiBold : FontWeights.Medium;
            TabElements.FontWeight = t == RailTab.Elements ? FontWeights.SemiBold : FontWeights.Medium;
            TabLogs.FontWeight = t == RailTab.Logs ? FontWeights.SemiBold : FontWeights.Medium;
        }

        // ────────────── Status poller (lightweight) ──────────────
        private string _lastStatus = "", _lastTool = "";
        private DispatcherTimer _statusPoll;

        private void StartStatusPoll()
        {
            _statusPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _statusPoll.Tick += (_, __) =>
            {
                if (_vm == null) return;
                var s = _vm.RunStatus;
                if (!string.IsNullOrEmpty(s) && s != _lastStatus)
                {
                    _lastStatus = s;
                    AddLogRow($"status → {s}");
                }
                var t = _vm.ToolActivity;
                if (!string.IsNullOrEmpty(t) && t != _lastTool)
                {
                    _lastTool = t;
                    AddLogRow($"tool → {t}");
                }
            };
            _statusPoll.Start();
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_vm == null) return;
            if (e.PropertyName == nameof(CopilotViewModel.RunStatus))
                AddLogRow($"status → {_vm.RunStatus}");
            else if (e.PropertyName == nameof(CopilotViewModel.ToolActivity) &&
                     !string.IsNullOrEmpty(_vm.ToolActivity))
                AddLogRow($"tool → {_vm.ToolActivity}");
        }
    }
}
