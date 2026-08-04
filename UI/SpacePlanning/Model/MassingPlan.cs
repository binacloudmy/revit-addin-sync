using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.SpacePlanning.Model
{
    /// <summary>
    /// Pure (no WPF, no Revit) helpers shared by the Planning screen, the preview
    /// canvas and the Build path:
    ///   · <see cref="MassingPalette"/> — room-type colours as hex, light + dark
    ///   · <see cref="PlanFit"/>        — the metric→screen auto-fit transform
    ///   · <see cref="MassingArgs"/>    — the metres→millimetres args builder
    /// Kept dependency-free so Tests/ can link the file and pin all three.
    /// </summary>
    public static class MassingPalette
    {
        /// <summary>Room-type swatch. Fill/Stroke are hex; the *Dark pair is the
        /// Slate-dark equivalent (translucent hue over the dark canvas, brightened
        /// stroke) so the plan reads in both CopilotTheme modes.</summary>
        public sealed class Swatch
        {
            public string Type;
            public string Label;        // legend text
            public string Fill;
            public string Stroke;
            public string FillDark;
            public string StrokeDark;
        }

        // Order is the legend order. Light values are the spec's palette (§4.4).
        private static readonly Swatch[] _all =
        {
            new Swatch { Type = "kelas",       Label = "Bilik Darjah",  Fill = "#DFE4FD", Stroke = "#5B61E8", FillDark = "#3A4750D8", StrokeDark = "#949BF7" },
            new Swatch { Type = "sokongan",    Label = "Sokongan",      Fill = "#E2F5C9", Stroke = "#6F9C33", FillDark = "#3A6F9C33", StrokeDark = "#A6CD6B" },
            new Swatch { Type = "tandas",      Label = "Tandas",        Fill = "#D5EDFB", Stroke = "#3B9FD4", FillDark = "#3A3B9FD4", StrokeDark = "#7CC7EC" },
            new Swatch { Type = "perhimpunan", Label = "Perhimpunan",   Fill = "#FDEEC2", Stroke = "#C9922A", FillDark = "#3AC9922A", StrokeDark = "#E8BC63" },
            new Swatch { Type = "kantin",      Label = "Kantin",        Fill = "#FBE0D3", Stroke = "#CC7043", FillDark = "#3ACC7043", StrokeDark = "#EFA07A" },
            new Swatch { Type = "padang",      Label = "Padang (site)", Fill = "#E8F2DD", Stroke = "#94B877", FillDark = "#2A94B877", StrokeDark = "#9FC98A" },
            new Swatch { Type = "selasar",     Label = "Selasar",       Fill = "#F1F2F4", Stroke = "#C3C8CF", FillDark = "#24FFFFFF", StrokeDark = "#6B768A" },
        };

        /// <summary>Label ink drawn inside a room rect. One value per theme: every
        /// fill above is pale in light mode and dark-translucent in dark mode, so a
        /// single ink clears 7:1 contrast on all of them.</summary>
        public const string InkLight = "#0b1220";
        public const string InkDark = "#e8eef6";

        public static IReadOnlyList<Swatch> All => _all;

        /// <summary>Swatch for a room type. Unknown types get a stable (hash-picked)
        /// swatch rather than a blank rect — the backend may add types the addin
        /// hasn't shipped yet.</summary>
        public static Swatch For(string type)
        {
            if (!string.IsNullOrEmpty(type))
            {
                foreach (var s in _all)
                    if (string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase)) return s;
            }
            // Stable hash → palette. Deliberately NOT string.GetHashCode(), which is
            // randomized per process on .NET Core: the same scheme would recolour on
            // every Revit restart.
            int h = 0;
            foreach (var ch in type ?? "") h = unchecked(h * 31 + ch);
            return _all[Math.Abs(h) % _all.Length];
        }
    }

    /// <summary>
    /// The metric→screen transform for one drawn level: uniform scale, centred,
    /// with Y flipped (metric Y-up → screen Y-down). Immutable, no WPF types, so
    /// the fit math is unit-testable without a rendering surface.
    /// </summary>
    public struct PlanFit
    {
        public bool IsEmpty;
        public double Scale;
        public double MinX, MinY, MaxX, MaxY;
        public double MarginX, MarginY;     // px gutter left / bottom after centring
        public double Width, Height;        // the surface we fitted into

        public double SpanX => MaxX - MinX;
        public double SpanY => MaxY - MinY;

        public double ToScreenX(double worldX) => (worldX - MinX) * Scale + MarginX;

        /// <summary>+y north in metres becomes downward-decreasing pixels.</summary>
        public double ToScreenY(double worldY) => Height - MarginY - (worldY - MinY) * Scale;

        /// <summary>Screen rect (left, top, width, height) for a room. Uses the
        /// room's TOP edge (y + h) because screen Y grows downward.</summary>
        public void RectOf(MassingRoom r, out double left, out double top, out double w, out double h)
        {
            left = ToScreenX(r.X);
            top = ToScreenY(r.Y + r.H);
            w = r.W * Scale;
            h = r.H * Scale;
        }

        /// <summary>Auto-fit the rooms into a width×height surface.
        /// <paramref name="fill"/> leaves a breathing gutter (0.92 = 4% each side).
        /// Returns IsEmpty when there is nothing drawable.</summary>
        public static PlanFit Fit(IEnumerable<MassingRoom> rooms, double width, double height, double fill = 0.92)
        {
            var fit = new PlanFit { IsEmpty = true, Scale = 1, Width = width, Height = height };
            if (rooms == null || width <= 0 || height <= 0) return fit;

            bool any = false;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var r in rooms)
            {
                if (r == null) continue;
                any = true;
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.W);
                maxY = Math.Max(maxY, r.Y + r.H);
            }
            if (!any) return fit;

            double spanX = maxX - minX, spanY = maxY - minY;
            // A single zero-width/height room (or all rooms collinear) makes one
            // span 0 — fit on the other axis alone instead of dividing by zero.
            double sx = spanX > 1e-9 ? width / spanX : double.PositiveInfinity;
            double sy = spanY > 1e-9 ? height / spanY : double.PositiveInfinity;
            double scale = Math.Min(sx, sy);
            if (double.IsInfinity(scale) || scale <= 0) scale = 1;
            scale *= fill;

            fit.IsEmpty = false;
            fit.Scale = scale;
            fit.MinX = minX; fit.MinY = minY; fit.MaxX = maxX; fit.MaxY = maxY;
            fit.MarginX = (width - spanX * scale) / 2.0;
            fit.MarginY = (height - spanY * scale) / 2.0;
            return fit;
        }
    }

    /// <summary>
    /// Builds the <c>place_massing_scheme</c> tool args from a scheme.
    ///
    /// ⚠ THE metres→millimetres CONVERSION LIVES HERE AND NOWHERE ELSE. The backend
    /// emits metres; the mutators consume millimetres (ArgsHelp.GetPointListMm /
    /// GetLengthMm divide by 304.8 internally). A second ×1000 anywhere downstream
    /// is a 1000× scale bug — a 7.2 m classroom placed 7,200 m wide. Pinned by
    /// MassingArgsTests.
    /// </summary>
    public static class MassingArgs
    {
        public const double MmPerMetre = 1000.0;

        /// <summary>Floor-to-floor height used when the backend does not publish one.
        /// Level 1 sits at 0, level n at (n-1)×this.
        ///
        /// PREFER the response's <c>floor_height_m</c>: it is the figure the SOA's
        /// volume was computed from, so building to anything else makes the reported
        /// isipadu describe a different building. This constant only covers a backend
        /// that predates the field.</summary>
        public const double DefaultStoreyHeightMm = 4000.0;

        /// <summary>Back-compat alias — the old name for the default.</summary>
        public const double StoreyHeightMm = DefaultStoreyHeightMm;

        /// <summary>Storey height in mm for a result: the backend's figure when it
        /// sent one, else the default. Non-positive values are ignored rather than
        /// producing a zero-height extrusion the mutator would reject.</summary>
        public static double StoreyHeightMmFor(SuggestResult result) =>
            result?.FloorHeightM is double m && m > 0 ? m * MmPerMetre : DefaultStoreyHeightMm;

        /// <summary>Wall height used when make_walls is on.</summary>
        public const double RoomHeightMm = 3000.0;

        /// <summary>Level of Development of what Build places. LOD 100 = a generic
        /// conceptual representation of the form; areas are approximate/derived (the
        /// SOA carries the authoritative figures) and the masses are deliberately NOT
        /// building elements. Stamped into each element's Comments so the deliverable
        /// is self-describing downstream.</summary>
        public const string Lod = "LOD 100";

        /// <summary>Revit level name for a scheme level index (1 → "Tingkat 1").</summary>
        public static string LevelName(int level) => "Tingkat " + level;

        /// <summary>Group name for the built scheme. Kept Design-Option-shaped
        /// ("option_name") per §6 even though the container is a Model Group.</summary>
        public static string OptionName(MassingScheme scheme) =>
            "Massing — " + (string.IsNullOrWhiteSpace(scheme?.Title) ? (scheme?.Id ?? "Scheme") : scheme.Title);

        /// <summary>
        /// Tool args for place_massing_scheme, all lengths in mm.
        /// Rooms with counts_as_gfa=false (the padang) are dropped: they are site
        /// area, not floor area, and building a slab for one would both corrupt the
        /// GFA and drop a 900 m² plate on the field. Preview-only in v1 (§9).
        /// </summary>
        public static Dictionary<string, object> Build(
            MassingScheme scheme, bool makeWalls = false, string optionName = null,
            bool autoOffset = true, double? storeyHeightMm = null)
        {
            if (scheme == null) throw new ArgumentNullException(nameof(scheme));

            // Backend figure when the caller passes one (see StoreyHeightMmFor);
            // otherwise the default. Guarded so a bad value cannot produce a
            // zero-height extrusion, which the mutator rejects outright.
            double storeyMm = storeyHeightMm is double h && h > 0 ? h : DefaultStoreyHeightMm;

            var buildable = (scheme.Rooms ?? new List<MassingRoom>())
                .Where(r => r != null && r.CountsAsGfa)
                .ToList();

            var levels = buildable
                .Select(r => r.Level)
                .Distinct()
                .OrderBy(n => n)
                .Select(n => (object)new Dictionary<string, object>
                {
                    ["name"] = LevelName(n),
                    ["elevation_mm"] = (n - 1) * storeyMm,
                    // The scheme's own 1|2 index, so the mutator can map a room's
                    // "level" to a level spec by VALUE rather than by array position
                    // (a level-2-only scheme has one entry, at index 0).
                    ["level"] = n,
                })
                .ToList();

            var rooms = buildable
                .Select(r => (object)new Dictionary<string, object>
                {
                    ["label"] = r.Label ?? "",
                    ["type"] = r.Type ?? "",
                    ["boundary_mm"] = Boundary(r),
                    ["level"] = r.Level,
                    ["height_mm"] = RoomHeightMm,
                })
                .ToList();

            return new Dictionary<string, object>
            {
                ["option_name"] = optionName ?? OptionName(scheme),
                ["levels"] = levels,
                ["rooms"] = rooms,
                ["make_walls"] = makeWalls,
                // LOD 100 deliverable: the scheme is placed as generic conceptual
                // masses (DirectShape extrusions), not as floors/walls. Floor-to-floor
                // height so the masses stack flush into a readable building form.
                ["storey_height_mm"] = storeyMm,
                ["lod"] = Lod,
                // The backend emits every scheme from the same origin, so two Builds
                // would land on top of each other. Step each one clear of the last.
                // Matters most for a programme too large for the generator's ~3,530 m²
                // ceiling, which can only be modelled as several briefs side by side.
                ["auto_offset"] = autoOffset,
            };
        }

        /// <summary>The room rectangle as a closed 4-point loop in mm, CCW from the
        /// bottom-left corner. The one and only ×1000.</summary>
        public static List<object> Boundary(MassingRoom r)
        {
            double x0 = r.X * MmPerMetre, y0 = r.Y * MmPerMetre;
            double x1 = (r.X + r.W) * MmPerMetre, y1 = (r.Y + r.H) * MmPerMetre;
            return new List<object>
            {
                new List<object> { x0, y0 },
                new List<object> { x1, y0 },
                new List<object> { x1, y1 },
                new List<object> { x0, y1 },
            };
        }
    }
}
