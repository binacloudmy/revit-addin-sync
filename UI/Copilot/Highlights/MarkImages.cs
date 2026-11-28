#nullable enable
// Mark bitmaps for Revit's TemporaryGraphicsManager (an in-canvas control draws an image
// file centred on a model point, at the image's pixel size; only a .bmp file is accepted,
// with RGB(0,128,128) as its clear colour: MarkBitmap). Three shapes (the "Gabung" view):
//   dot     a filled circle 10 DIP across in the outcome's colour over a 1 DIP halo, so it
//           reads on a dark and on a white canvas. One size, whatever the zoom.
//   badge   a pill in the outcome's colour with a number in it: the count of dots that would
//           overlap ("4", "99+"), or, amber, the place of something that needs a look ("1", "3+").
//   frame   a hollow rounded square round a tag, a dimension or another annotation (a dot
//           would hide its text), in the size steps of MarkKey.Sizes.
// The marks of a group that is not in focus are dim: the colour mixed towards the canvas and
// no halo. One file per shape, outcome, size or text, canvas, DPI scale and tone, written once
// under %LOCALAPPDATA%\Bina\RevitSync\marks.

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    public static class MarkImages
    {
        /// <summary>The halo round a dot or a badge, in DIP.</summary>
        public const double Halo = 1;
        /// <summary>A badge's height in DIP, halo included.</summary>
        public const double BadgeHeight = MarkKey.BadgeDip;
        public const double FrameStroke = 2;
        public const double FrameHalo = 4.5;

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "marks");

        /// <summary>A dot's image (written on first use).</summary>
        public static string DotPath(MarkOutcome outcome, bool darkCanvas, double dpiScale, MarkTone tone = MarkTone.Normal) =>
            // "marker-": a new file name, so a PC that cached 0.0.144's small dots draws the new marker (files are written once).
            Cached("marker-" + Key(outcome), darkCanvas, dpiScale, tone, s => RenderDot(outcome, s, darkCanvas, tone));

        /// <summary>A frame's image at a size step (DIP, <see cref="MarkKey.Sizes"/>).</summary>
        public static string FramePath(MarkOutcome outcome, int size, bool darkCanvas, double dpiScale, MarkTone tone = MarkTone.Normal) =>
            Cached("frame-" + Key(outcome) + "-" + size.ToString(CultureInfo.InvariantCulture), darkCanvas, dpiScale, tone,
                s => RenderFrame(outcome, (int)Math.Round(size * s), s, darkCanvas, tone));

        /// <summary>A badge's image with its text ("4", "99+", "2", "3+").</summary>
        public static string BadgePath(MarkOutcome outcome, string text, bool darkCanvas, double dpiScale, MarkTone tone = MarkTone.Normal) =>
            Cached("badge-" + Key(outcome) + "-" + (text ?? "").Replace("+", "p"), darkCanvas, dpiScale, tone, s => RenderBadge(outcome, text ?? "", s, darkCanvas, tone));

        private static string Key(MarkOutcome o) => o.ToString().ToLowerInvariant();

        private static string Cached(string name, bool darkCanvas, double dpiScale, MarkTone tone, Func<double, byte[]> render)
        {
            var scale = dpiScale <= 0 ? 1.0 : Math.Round(dpiScale, 2);
            var file = name + (darkCanvas ? "-dk" : "-lt") + "-" + scale.ToString(CultureInfo.InvariantCulture) + MarkFocus.ToneSuffix(tone) + MarkBitmap.Extension;
            var path = Path.Combine(Folder, file);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(path, render(scale));
            }
            return path;
        }

        /// <summary>A marker as a .bmp file's bytes (r12): a ring in the outcome's colour, a white halo inside it, then a solid dot
        /// in the outcome's colour, <see cref="MarkKey.MarkerDip"/> across. The white halo stands out on a dark canvas, the
        /// coloured ring on a white one. A dim marker has no halo (the gap is clear) and the duller colour.</summary>
        public static byte[] RenderDot(MarkOutcome outcome, double scale, bool darkCanvas, MarkTone tone = MarkTone.Normal)
        {
            scale = scale <= 0 ? 1 : scale;
            var (color, _) = Colours(outcome, darkCanvas, tone);
            var px = Math.Max(12, (int)Math.Round(MarkKey.MarkerDip * scale));
            return Draw(px, px, dc =>
            {
                var c = new Point(px / 2.0, px / 2.0);
                var outer = px / 2.0;
                var ring = MarkKey.MarkerRing * scale;
                var halo = MarkKey.MarkerHalo * scale;
                // The white halo inside the ring (none on a dim marker: the gap stays clear).
                if (tone != MarkTone.Dim) dc.DrawEllipse(Brushes.White, null, c, outer - ring / 2, outer - ring / 2);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(color), ring), c, outer - ring / 2, outer - ring / 2);
                var r = outer - ring - halo;
                dc.DrawEllipse(new SolidColorBrush(color), null, c, r, r);
            });
        }

        /// <summary>A badge as a .bmp file's bytes: a pill in the outcome's colour over the halo, its text in the badge ink.
        /// As wide as its text needs, never narrower than it is tall.</summary>
        public static byte[] RenderBadge(MarkOutcome outcome, string text, double scale, bool darkCanvas, MarkTone tone = MarkTone.Normal)
        {
            scale = scale <= 0 ? 1 : scale;
            var (color, halo) = Colours(outcome, darkCanvas, tone);
            var ink = Hex("Cp.Mark.BadgeInk", darkCanvas);
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            // Drawn in pixels into a 96 DPI bitmap: one pixel per DIP here, the scale is in the sizes.
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11 * scale, new SolidColorBrush(ink), 1.0);
            var h = Math.Max(8, (int)Math.Round(BadgeHeight * scale));
            var w = Math.Max(h, (int)Math.Ceiling(ft.Width + 10 * scale + 2 * Halo * scale));
            return Draw(w, h, dc =>
            {
                var outer = new Rect(0, 0, w, h);
                dc.DrawRoundedRectangle(new SolidColorBrush(halo), null, outer, h / 2.0, h / 2.0);
                var inset = Halo * scale;
                var inner = new Rect(inset, inset, w - 2 * inset, h - 2 * inset);
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, inner, inner.Height / 2, inner.Height / 2);
                dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            });
        }

        /// <summary>A hollow rounded frame as a .bmp file's bytes: the halo first, the outcome's colour over it; dashed for
        /// "needs a look".</summary>
        public static byte[] RenderFrame(MarkOutcome outcome, int px, double scale, bool darkCanvas, MarkTone tone = MarkTone.Normal)
        {
            px = Math.Max(10, px);
            scale = scale <= 0 ? 1 : scale;
            var (color, halo) = Colours(outcome, darkCanvas, tone);
            var stroke = tone == MarkTone.Dim ? 1.5 : FrameStroke;
            return Draw(px, px, dc =>
            {
                var haloPen = new Pen(new SolidColorBrush(halo), FrameHalo * scale) { LineJoin = PenLineJoin.Round };
                var pen = new Pen(new SolidColorBrush(color), stroke * scale)
                {
                    LineJoin = PenLineJoin.Round,
                    DashStyle = HighlightSet.Dashed(outcome) ? new DashStyle(new double[] { 2.2, 1.6 }, 0) : DashStyles.Solid,
                };
                var inset = haloPen.Thickness / 2 + 0.5;
                var rect = new Rect(inset, inset, px - 2 * inset, px - 2 * inset);
                var r = 5 * scale;
                dc.DrawRoundedRectangle(null, haloPen, rect, r, r);
                dc.DrawRoundedRectangle(null, pen, rect, r, r);
            });
        }

        /// <summary>The outcome's colour and the halo's; dim: the colour mixed towards the canvas and no halo (a bitmap has no
        /// alpha, so a dim mark is a duller colour, not a fainter one).</summary>
        private static (Color Color, Color Halo) Colours(MarkOutcome outcome, bool darkCanvas, MarkTone tone)
        {
            var color = Hex(HighlightSet.TokenOf(outcome), darkCanvas);
            var halo = Hex("Cp.Mark.Halo", darkCanvas);
            if (tone == MarkTone.Dim)
            {
                var canvas = darkCanvas ? Colors.Black : Colors.White;   // towards the canvas: black on a dark one, white on a light one
                color = Color.FromRgb(Mix(color.R, canvas.R), Mix(color.G, canvas.G), Mix(color.B, canvas.B));
                halo.A = 0;
            }
            return (color, halo);
        }

        private static Color Hex(string token, bool darkCanvas) =>
            (Color)ColorConverter.ConvertFromString(RevitWebAppSync.UI.Copilot.Model.ThemeTokens.Hex(token, darkCanvas)!);

        private static byte[] Draw(int w, int h, Action<DrawingContext> paint)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) paint(dc);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var pixels = new byte[w * h * 4];
            rtb.CopyPixels(pixels, w * 4, 0);
            return MarkBitmap.Encode(pixels, w, h);
        }

        // A dim mark: two fifths of the colour, three fifths of the canvas.
        private static byte Mix(byte colour, byte canvas) => (byte)Math.Round(colour * 0.4 + canvas * 0.6);
    }
}
