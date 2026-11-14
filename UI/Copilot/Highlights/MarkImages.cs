#nullable enable
// Mark bitmaps for Revit's TemporaryGraphicsManager (an in-canvas control draws an image
// file centred on a model point, at the image's pixel size; only a .bmp file is accepted,
// with RGB(0,128,128) as its clear colour: MarkBitmap). Hollow, so the thing under
// the mark stays readable (result marks design, rule 2): a rounded frame for a tag or
// another annotation, a ring for an element. Outcome colour over a thin halo, so the
// mark reads on a dark and on a white canvas; dashed for "needs a look". One file per
// shape, outcome, size step, canvas and DPI scale, written once under
// %LOCALAPPDATA%\Bina\RevitSync\marks. A focused group's marks are bright (a thicker line on a
// wider halo) and the others dim (the same mark mixed towards the canvas, no halo): one more key in
// the file name (step 2). The dimension track is step 3.

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
        public const double Stroke = 2;
        public const double HaloStroke = 4.5;

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "marks");

        /// <summary>The mark image for an outcome at a size step (DIP, <see cref="MarkKey.Sizes"/>) and DPI scale. Written on first use.</summary>
        public static string PathFor(MarkOutcome outcome, bool frame, int size, bool darkCanvas, double dpiScale, MarkTone tone = MarkTone.Normal)
        {
            var scale = dpiScale <= 0 ? 1.0 : Math.Round(dpiScale, 2);
            var name = (frame ? "frame" : "ring") + "-" + outcome.ToString().ToLowerInvariant() + "-" + size.ToString(CultureInfo.InvariantCulture)
                       + (darkCanvas ? "-dk" : "-lt") + "-" + scale.ToString(CultureInfo.InvariantCulture) + MarkFocus.ToneSuffix(tone) + MarkBitmap.Extension;
            var path = Path.Combine(Folder, name);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(path, Render(outcome, frame, (int)Math.Round(size * scale), scale, darkCanvas, tone));
            }
            return path;
        }

        /// <summary>A hollow frame or ring as a .bmp file's bytes: the halo first, the outcome's colour over it. Nothing is
        /// filled. Bright: a thicker line on a wider halo; dim: the colour mixed towards the canvas and no halo (a bitmap
        /// has no alpha).</summary>
        public static byte[] Render(MarkOutcome outcome, bool frame, int px, double scale, bool darkCanvas, MarkTone tone = MarkTone.Normal)
        {
            px = Math.Max(10, px);
            var color = (Color)ColorConverter.ConvertFromString(HighlightSet.ColorOf(outcome, darkCanvas));
            var halo = (Color)ColorConverter.ConvertFromString(RevitWebAppSync.UI.Copilot.Model.ThemeTokens.Hex("Cp.Mark.Halo", darkCanvas)!);
            if (tone == MarkTone.Dim)
            {
                var canvas = darkCanvas ? Colors.Black : Colors.White;   // towards the canvas: black on a dark one, white on a light one
                color = Color.FromRgb(Mix(color.R, canvas.R), Mix(color.G, canvas.G), Mix(color.B, canvas.B));
                halo.A = 0;
            }
            var stroke = tone == MarkTone.Bright ? 3.0 : tone == MarkTone.Dim ? 1.5 : Stroke;
            var haloStroke = tone == MarkTone.Bright ? 6.0 : HaloStroke;
            var dashed = HighlightSet.Dashed(outcome);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var haloPen = new Pen(new SolidColorBrush(halo), haloStroke * scale) { LineJoin = PenLineJoin.Round };
                var pen = new Pen(new SolidColorBrush(color), stroke * scale)
                {
                    LineJoin = PenLineJoin.Round,
                    DashStyle = dashed ? new DashStyle(new double[] { 2.2, 1.6 }, 0) : DashStyles.Solid,
                };
                var inset = haloPen.Thickness / 2 + 0.5;
                var rect = new Rect(inset, inset, px - 2 * inset, px - 2 * inset);
                if (frame)
                {
                    var r = 5 * scale;
                    dc.DrawRoundedRectangle(null, haloPen, rect, r, r);
                    dc.DrawRoundedRectangle(null, pen, rect, r, r);
                }
                else
                {
                    var c = new Point(px / 2.0, px / 2.0);
                    dc.DrawEllipse(null, haloPen, c, rect.Width / 2, rect.Height / 2);
                    dc.DrawEllipse(null, pen, c, rect.Width / 2, rect.Height / 2);
                }
            }
            var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var pixels = new byte[px * px * 4];
            rtb.CopyPixels(pixels, px * 4, 0);
            return MarkBitmap.Encode(pixels, px, px);
        }

        // A dim mark: two fifths of the colour, three fifths of the canvas.
        private static byte Mix(byte colour, byte canvas) => (byte)Math.Round(colour * 0.4 + canvas * 0.6);
    }
}
