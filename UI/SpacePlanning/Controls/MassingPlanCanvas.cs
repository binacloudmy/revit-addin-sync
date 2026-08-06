using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using RevitWebAppSync.UI.SpacePlanning.Model;

namespace RevitWebAppSync.UI.SpacePlanning.Controls
{
    /// <summary>
    /// Draw-only floor-plan preview for one level of a massing scheme. Pure pixels:
    /// this control NEVER touches the Revit document — the only write is the pane's
    /// Build button (place_massing_scheme).
    ///
    /// No existing Copilot control draws geometry, so this subclasses
    /// FrameworkElement and renders in OnRender: a room rect per room, auto-fit and
    /// Y-flipped (metric Y-up → screen Y-down) by <see cref="PlanFit"/>, coloured by
    /// room type from <see cref="MassingPalette"/>, plus a legend strip and the
    /// level's area caption.
    ///
    /// Theme: the palette carries a light and a Slate-dark pair, picked off
    /// CopilotTheme.IsDark the same way the code-drawn chat surface does. The pane
    /// re-renders its body on ThemeChanged, and the setter below re-invalidates, so
    /// a theme flip repaints.
    /// </summary>
    public class MassingPlanCanvas : FrameworkElement
    {
        // ── Dependency properties ────────────────────────────────────────────
        public static readonly DependencyProperty SchemeProperty =
            DependencyProperty.Register(nameof(Scheme), typeof(MassingScheme), typeof(MassingPlanCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LevelProperty =
            DependencyProperty.Register(nameof(Level), typeof(int), typeof(MassingPlanCanvas),
                new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The scheme being previewed. Null → empty state.</summary>
        public MassingScheme Scheme
        {
            get => (MassingScheme)GetValue(SchemeProperty);
            set => SetValue(SchemeProperty, value);
        }

        /// <summary>Which storey to draw (1 = Tingkat 1).</summary>
        public int Level
        {
            get => (int)GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        /// <summary>Draw the room-type legend strip along the bottom. The floating
        /// Scheme Preview window renders the legend in its own toolbar (per the
        /// design), so it turns this off rather than showing two of them.</summary>
        public static readonly DependencyProperty ShowLegendProperty =
            DependencyProperty.Register(nameof(ShowLegend), typeof(bool), typeof(MassingPlanCanvas),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool ShowLegend
        {
            get => (bool)GetValue(ShowLegendProperty);
            set => SetValue(ShowLegendProperty, value);
        }

        /// <summary>Height requested when the parent gives us infinite space (the
        /// screen stacks us in a ScrollViewer). Width always fills.</summary>
        public double PreferredHeight { get; set; } = 300;

        // ── Layout constants ─────────────────────────────────────────────────
        private const double Pad = 8;             // outer padding
        private const double CaptionHeight = 18;  // "Tingkat 1 · 1,731 m²" line
        private const double LegendRowHeight = 17;
        private const double LegendSwatch = 9;
        private const double LegendGap = 12;
        private const double MinLabelPad = 4;     // room must exceed text + this to label

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? PreferredHeight : availableSize.Height;
            return new Size(w, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            bool dark = CopilotTheme.IsDark;
            var surface = Brush(dark ? "#0c1420" : "#f7f9fb");
            var hair = Brush(dark ? "#24FFFFFF" : "#140F1B2D");
            var muted = Brush(dark ? "#8a94a6" : "#586273");
            var ink = Brush(dark ? MassingPalette.InkDark : MassingPalette.InkLight);

            // Surface + hairline frame. Also the hit-test area — without a filled
            // rect a FrameworkElement is invisible to the mouse.
            var framePen = new Pen(hair, 1);
            dc.DrawRoundedRectangle(surface, framePen, new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)), 8, 8);

            var rooms = (Scheme?.Rooms ?? new List<MassingRoom>())
                .Where(r => r != null && r.Level == Level)
                .ToList();

            if (rooms.Count == 0)
            {
                var empty = Text(Scheme == null ? "No scheme selected" : $"Nothing on Tingkat {Level}", 11.5, muted);
                dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
                return;
            }

            // ── Legend strip (bottom): only the types actually on this level ──
            var present = MassingPalette.All
                .Where(s => rooms.Any(r => MassingPalette.For(r.Type) == s))
                .ToList();
            var legendRows = ShowLegend
                ? LegendLayout(present, w - Pad * 2)
                : new List<List<MassingPalette.Swatch>>();
            double legendHeight = legendRows.Count * LegendRowHeight;

            // ── Caption (top): level + its floor area ────────────────────────
            double area = Scheme?.LevelArea(Level) ?? 0;
            var caption = Text($"Tingkat {Level}", 11, ink, bold: true);
            dc.DrawText(caption, new Point(Pad + 2, Pad));
            if (area > 0)
            {
                var areaText = Text($"{area:N0} m² floor area", 11, muted);
                dc.DrawText(areaText, new Point(w - Pad - 2 - areaText.Width, Pad));
            }

            // ── The plan itself, in what's left ──────────────────────────────
            double planTop = Pad + CaptionHeight;
            double planW = w - Pad * 2;
            double planH = h - planTop - Pad - legendHeight - (legendHeight > 0 ? 4 : 0);
            if (planW <= 4 || planH <= 4) return;

            var fit = PlanFit.Fit(rooms, planW, planH);
            if (fit.IsEmpty) return;

            // Draw the site-only rooms (padang) FIRST so building blocks sit on top
            // when a field overlaps a block's bounding box.
            foreach (var room in rooms.OrderBy(r => r.CountsAsGfa ? 1 : 0))
            {
                var sw = MassingPalette.For(room.Type);
                fit.RectOf(room, out var left, out var top, out var rw, out var rh);
                var rect = new Rect(Pad + left, planTop + top, Math.Max(0, rw), Math.Max(0, rh));

                var fillBrush = Brush(dark ? sw.FillDark : sw.Fill);
                var strokeBrush = Brush(dark ? sw.StrokeDark : sw.Stroke);
                // Site area is NOT a building block — a dashed outline says so at a
                // glance (acceptance criterion 2) without needing the legend.
                var pen = room.CountsAsGfa
                    ? new Pen(strokeBrush, 1.2)
                    : new Pen(strokeBrush, 1.2) { DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0) };
                pen.Freeze();
                dc.DrawRectangle(fillBrush, pen, rect);

                // Label, hidden when the rect can't hold it (auto-hide, no clipping).
                if (string.IsNullOrWhiteSpace(room.Label)) continue;
                var label = Text(room.Label, 10, ink);
                if (label.Width + MinLabelPad * 2 > rect.Width || label.Height + MinLabelPad > rect.Height) continue;
                dc.DrawText(label, new Point(
                    rect.X + (rect.Width - label.Width) / 2,
                    rect.Y + (rect.Height - label.Height) / 2));
            }

            // ── Legend ───────────────────────────────────────────────────────
            double ly = h - Pad - legendHeight;
            foreach (var row in legendRows)
            {
                double lx = Pad + 2;
                foreach (var sw in row)
                {
                    var fillBrush = Brush(dark ? sw.FillDark : sw.Fill);
                    var strokeBrush = Brush(dark ? sw.StrokeDark : sw.Stroke);
                    var swatchPen = new Pen(strokeBrush, 1);
                    swatchPen.Freeze();
                    dc.DrawRoundedRectangle(fillBrush, swatchPen,
                        new Rect(lx, ly + 3, LegendSwatch, LegendSwatch), 2, 2);
                    var t = Text(sw.Label, 10, muted);
                    dc.DrawText(t, new Point(lx + LegendSwatch + 4, ly + 1));
                    lx += LegendSwatch + 4 + t.Width + LegendGap;
                }
                ly += LegendRowHeight;
            }
        }

        /// <summary>Greedy wrap of the legend chips into rows that fit the width.</summary>
        private List<List<MassingPalette.Swatch>> LegendLayout(
            List<MassingPalette.Swatch> swatches, double maxWidth)
        {
            var rows = new List<List<MassingPalette.Swatch>>();
            if (swatches.Count == 0 || maxWidth <= 0) return rows;

            var row = new List<MassingPalette.Swatch>();
            double x = 0;
            foreach (var sw in swatches)
            {
                double chip = LegendSwatch + 4 + Text(sw.Label, 10, Brushes.Black).Width + LegendGap;
                if (row.Count > 0 && x + chip > maxWidth)
                {
                    rows.Add(row);
                    row = new List<MassingPalette.Swatch>();
                    x = 0;
                }
                row.Add(sw);
                x += chip;
            }
            if (row.Count > 0) rows.Add(row);
            return rows;
        }

        // ── Small render helpers ─────────────────────────────────────────────

        private static readonly Dictionary<string, Brush> _brushes =
            new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);

        private static Brush Brush(string hex)
        {
            if (_brushes.TryGetValue(hex, out var cached)) return cached;
            Brush b;
            try
            {
                var scb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                scb.Freeze();
                b = scb;
            }
            catch { b = Brushes.Transparent; }
            _brushes[hex] = b;
            return b;
        }

        private static Typeface _face;
        private static Typeface Face =>
            _face ??= new Typeface(new FontFamily("Geist, Segoe UI, system-ui"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private static Typeface _faceBold;
        private static Typeface FaceBold =>
            _faceBold ??= new Typeface(new FontFamily("Geist, Segoe UI, system-ui"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
            new FormattedText(
                s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                bold ? FaceBold : Face, size, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
