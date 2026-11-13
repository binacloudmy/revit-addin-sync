#nullable enable
// Mark PNGs for Revit's TemporaryGraphicsManager (an in-canvas control draws an image
// file centred on a model point, at the image's pixel size). Hollow, so the thing under
// the mark stays readable (result marks design, rule 2): a rounded frame for a tag or
// another annotation, a ring for an element. Outcome colour over a thin halo, so the
// mark reads on a dark and on a white canvas; dashed for "needs a look". One file per
// shape, outcome, size step, canvas and DPI scale, written once under
// %LOCALAPPDATA%\Bina\RevitSync\marks. (Bright and dim variants for a focused group,
// and the dimension track, are steps 2 and 3.)

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
        public static string PathFor(MarkOutcome outcome, bool frame, int size, bool darkCanvas, double dpiScale)
        {
            var scale = dpiScale <= 0 ? 1.0 : Math.Round(dpiScale, 2);
            var name = (frame ? "frame" : "ring") + "-" + outcome.ToString().ToLowerInvariant() + "-" + size.ToString(CultureInfo.InvariantCulture)
                       + (darkCanvas ? "-dk" : "-lt") + "-" + scale.ToString(CultureInfo.InvariantCulture) + ".png";
            var path = Path.Combine(Folder, name);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(path, Render(outcome, frame, (int)Math.Round(size * scale), scale, darkCanvas));
            }
            return path;
        }

        /// <summary>A hollow frame or ring: the halo first, the outcome's colour over it. Nothing is filled.</summary>
        public static byte[] Render(MarkOutcome outcome, bool frame, int px, double scale, bool darkCanvas)
        {
            px = Math.Max(10, px);
            var color = (Color)ColorConverter.ConvertFromString(HighlightSet.ColorOf(outcome, darkCanvas));
            var halo = (Color)ColorConverter.ConvertFromString(RevitWebAppSync.UI.Copilot.Model.ThemeTokens.Hex("Cp.Mark.Halo", darkCanvas)!);
            var dashed = HighlightSet.Dashed(outcome);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var haloPen = new Pen(new SolidColorBrush(halo), HaloStroke * scale) { LineJoin = PenLineJoin.Round };
                var pen = new Pen(new SolidColorBrush(color), Stroke * scale)
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
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
    }
}
