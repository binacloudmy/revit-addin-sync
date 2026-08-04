using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.SpacePlanning.Model;

namespace RevitWebAppSync.UI.SpacePlanning.Windows
{
    /// <summary>
    /// The floating "Scheme Preview" window from the design: a schematic plan of one
    /// massing scheme, overlaid on the Revit viewport.
    ///
    /// DRAW-ONLY. This window never opens a Transaction and never touches the
    /// document — Build (in the pane) remains the single write. The title bar says
    /// so, because a plan floating over the Revit canvas reads as model geometry.
    ///
    /// Non-modal on purpose: the pane must stay live while it is open (previewing
    /// another scheme just re-points this window), and blocking Revit for a preview
    /// would be worse than having none.
    /// </summary>
    public partial class SchemePreviewWindow : Window
    {
        private MassingScheme _scheme;
        private int _level = 1;
        private bool _collapsed;
        private bool _docked = true;
        private double _restoreHeight;

        /// <summary>Raised when the user picks a different storey here, so the pane
        /// can keep its own level indicator in step.</summary>
        public event Action<int> LevelPicked;

        public SchemePreviewWindow()
        {
            InitializeComponent();

            TitleBar.MouseLeftButtonDown += OnTitleBarDown;
            Nodrag(DockChip, ToggleDock);
            Nodrag(CollapseGlyph, ToggleCollapse);
            Nodrag(CloseGlyph, () => Close());

            // Code-built rows capture their brushes, so a theme flip has to rebuild
            // them — same contract as the pane's screens.
            Loaded += (_, __) =>
            {
                CopilotTheme.ThemeChanged -= OnTheme;
                CopilotTheme.ThemeChanged += OnTheme;
                // Sync the chip to the real starting state. Deferred to Loaded
                // because Owner is assigned by the caller after construction, and
                // docking needs it.
                SetDocked(_docked);
            };
            Closed += (_, __) => { CopilotTheme.ThemeChanged -= OnTheme; DetachOwner(); };
        }

        private void OnTheme()
        {
            BuildToolbar();
            Plan.InvalidateVisual();
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Point this window at a scheme/level. Safe to call repeatedly —
        /// previewing a second scheme reuses the open window rather than stacking
        /// another one over the viewport.</summary>
        public void SetScheme(MassingScheme scheme, int level)
        {
            _scheme = scheme;

            // A scheme may not have the requested storey (scheme C can be shorter
            // than scheme A): fall back to its first, never to an empty canvas.
            var levels = Levels();
            _level = levels.Contains(level) ? level : (levels.Count > 0 ? levels[0] : 1);

            SubTitle.Text = "BINA AI Copilot · "
                + (string.IsNullOrWhiteSpace(scheme?.Title) ? (scheme?.Id ?? "Scheme") : scheme.Title)
                + " · Schematic — not a Revit view";

            Plan.Scheme = scheme;
            Plan.Level = _level;
            BuildToolbar();
        }

        private List<int> Levels() => _scheme?.Levels() ?? new List<int>();

        // ── Title-bar behaviour ──────────────────────────────────────────────

        // DragMove() swallows the second click of a double-click, so the
        // double-click-to-dock gesture from the design has to be detected here,
        // before the drag starts.
        private void OnTitleBarDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { ToggleDock(); return; }
            try { DragMove(); } catch { /* released mid-gesture */ }
            if (_docked) SetDocked(false);   // dragging it away means "float"
        }

        /// <summary>Wire a title-bar chip so clicking it acts but never starts a
        /// window drag (the design's data-nodrag).</summary>
        private static void Nodrag(UIElement el, Action onClick)
        {
            el.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        }

        private void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            if (_collapsed)
            {
                _restoreHeight = ActualHeight;
                ToolbarWrap.Visibility = Visibility.Collapsed;
                PlanWrap.Visibility = Visibility.Collapsed;
                ResizeMode = ResizeMode.NoResize;
                // Set the height explicitly rather than via SizeToContent: an
                // explicit Window.Height (which the caller sets) wins over
                // SizeToContent, so collapsing left a tall empty frame. MinHeight
                // has to come down too or it silently floors the collapse.
                MinHeight = 0;
                Height = TitleBar.Height + 2;   // +2 = the root border
            }
            else
            {
                ToolbarWrap.Visibility = Visibility.Visible;
                PlanWrap.Visibility = Visibility.Visible;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                MinHeight = 120;
                Height = _restoreHeight > 0 ? _restoreHeight : 400;
            }
            CollapseGlyph.Text = _collapsed ? "□" : "–";
            CollapseGlyph.ToolTip = _collapsed ? "Expand" : "Collapse";
        }

        // ── Dock / float ─────────────────────────────────────────────────────
        // A WPF window cannot truly dock inside Revit's viewport, so "docked" here
        // means pinned to the top-right of the Revit window and following it as it
        // moves or resizes; "floating" means positioned freely by the user.

        private void ToggleDock() => SetDocked(!_docked);

        private void SetDocked(bool docked)
        {
            _docked = docked;
            DockLabel.Text = docked ? "Float" : "Dock";
            DockChip.ToolTip = docked ? "Detach and position freely" : "Pin to the top-right of Revit";
            if (docked) { AttachOwner(); SnapToOwner(); }
            else DetachOwner();
        }

        private void AttachOwner()
        {
            if (Owner == null) return;
            Owner.LocationChanged -= OnOwnerMoved;
            Owner.SizeChanged -= OnOwnerResized;
            Owner.LocationChanged += OnOwnerMoved;
            Owner.SizeChanged += OnOwnerResized;
        }

        private void DetachOwner()
        {
            if (Owner == null) return;
            Owner.LocationChanged -= OnOwnerMoved;
            Owner.SizeChanged -= OnOwnerResized;
        }

        private void OnOwnerMoved(object s, EventArgs e) => SnapToOwner();
        private void OnOwnerResized(object s, SizeChangedEventArgs e) => SnapToOwner();

        /// <summary>Pin to the owner's top-right with a margin, clamped so the title
        /// bar always stays grabbable even if Revit is partly offscreen.</summary>
        public void SnapToOwner()
        {
            var o = Owner;
            if (o == null || !_docked) return;
            try
            {
                const double margin = 18;
                // Revit maximized reports Left/Top as the restore bounds, which would
                // fling this offscreen — use the working area in that case.
                bool max = o.WindowState == WindowState.Maximized;
                double ol = max ? SystemParameters.WorkArea.Left : o.Left;
                double ot = max ? SystemParameters.WorkArea.Top : o.Top;
                double ow = max ? SystemParameters.WorkArea.Width : o.ActualWidth;

                Left = Math.Max(SystemParameters.VirtualScreenLeft,
                                ol + ow - Width - margin);
                Top = Math.Max(SystemParameters.VirtualScreenTop, ot + 90);
            }
            catch { /* owner mid-teardown */ }
        }

        // ── Toolbar: level tabs + legend ─────────────────────────────────────

        private void BuildToolbar()
        {
            Toolbar.Children.Clear();
            if (_scheme == null) return;

            var levels = Levels();
            if (levels.Count > 1)
            {
                var seg = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = Res("Cp.Menu"),
                    BorderBrush = Res("Cp.Line"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(2),
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var n in levels) row.Children.Add(LevelTab(n));
                seg.Child = row;
                Toolbar.Children.Add(seg);
            }

            // Only the types actually drawn on this storey — the same rule the
            // canvas uses, so the legend can never list a swatch that isn't there.
            var rooms = (_scheme.Rooms ?? new List<MassingRoom>()).Where(r => r != null && r.Level == _level);
            bool dark = CopilotTheme.IsDark;
            foreach (var sw in MassingPalette.All.Where(s => rooms.Any(r => MassingPalette.For(r.Type) == s)))
                Toolbar.Children.Add(LegendChip(sw, dark));

            // No area figure here on purpose — the canvas already captions the
            // storey with "Tingkat N · N m² floor area", and printing it twice in
            // one small window read as two different numbers at a glance.
        }

        private Button LevelTab(int n)
        {
            bool on = n == _level;
            var btn = new Button
            {
                Content = new TextBlock
                {
                    // "T" for Tingkat, matching the pane's own level toggle and the
                    // canvas caption. The mock says L1/L2, but shipping two labels
                    // for the same storey across two surfaces is worse than
                    // deviating from the mock on one letter.
                    Text = "T" + n,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    // Same active/inactive treatment the pane's level toggle uses,
                    // so the two controls can't drift apart visually.
                    Foreground = on ? Res("Cp.AccentContrast") : Res("Cp.Muted"),
                },
                Background = on ? Res("Cp.Accent") : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(11, 3, 11, 3),
                Cursor = Cursors.Hand,
                Focusable = true,
            };
            btn.Template = SegmentTemplate();
            System.Windows.Automation.AutomationProperties.SetName(btn, $"Show Tingkat {n}");
            btn.Click += (_, __) =>
            {
                _level = n;
                Plan.Level = n;
                BuildToolbar();
                try { LevelPicked?.Invoke(n); } catch { /* listener's problem */ }
            };
            return btn;
        }

        private static ControlTemplate _segTemplate;

        /// <summary>Chromeless button template — the default Button chrome would
        /// paint a grey box over the segmented control.</summary>
        private static ControlTemplate SegmentTemplate()
        {
            if (_segTemplate != null) return _segTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty,
                new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(presenter);
            _segTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            _segTemplate.Seal();
            return _segTemplate;
        }

        private static UIElement LegendChip(MassingPalette.Swatch sw, bool dark)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = Hex(dark ? sw.FillDark : sw.Fill),
                BorderBrush = Hex(dark ? sw.StrokeDark : sw.Stroke),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = sw.Label,
                FontSize = 11.5,
                Foreground = Res("Cp.Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return row;
        }

        // ── Small helpers ────────────────────────────────────────────────────

        private static Brush Res(string key) =>
            (Application.Current?.TryFindResource(key) as Brush) ?? Brushes.Gray;

        private static Brush Hex(string hex)
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
            catch { return Brushes.Transparent; }
        }
    }
}
