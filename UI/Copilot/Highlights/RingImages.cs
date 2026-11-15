#nullable enable
// Ring PNGs for Revit's TemporaryGraphicsManager (in-canvas controls draw an
// image file at a model point, at its pixel size). One file per state and DPI
// scale, written once under %LOCALAPPDATA%\Bina\RevitSync\rings.

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    public static class RingImages
    {
        public const int BasePx = 18;

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "rings");

        /// <summary>The ring image for a state at a DPI scale (1.0 = 100 %). Written on first use.</summary>
        public static string PathFor(MarkState state, double dpiScale)
        {
            var scale = dpiScale <= 0 ? 1.0 : Math.Round(dpiScale, 2);
            var name = state.ToString().ToLowerInvariant() + "-" + scale.ToString(CultureInfo.InvariantCulture) + ".png";
            var path = Path.Combine(Folder, name);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                var bytes = Render(state, (int)Math.Round(BasePx * scale), scale);
                File.WriteAllBytes(path, bytes);
            }
            return path;
        }

        /// <summary>A translucent-filled ring with a 2.5 px stroke (dashed for NeedsYou and Deleted).</summary>
        public static byte[] Render(MarkState state, int px, double scale)
        {
            px = Math.Max(8, px);
            var color = (Color)ColorConverter.ConvertFromString(HighlightSet.ColorOf(state));
            var dashed = state == MarkState.NeedsYou || state == MarkState.Deleted;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var pen = new Pen(new SolidColorBrush(color), 2.5 * scale) { DashStyle = dashed ? new DashStyle(new double[] { 2, 1.5 }, 0) : DashStyles.Solid };
                var fill = dashed ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B));
                var r = px / 2.0 - pen.Thickness / 2 - 0.5;
                dc.DrawEllipse(fill, pen, new Point(px / 2.0, px / 2.0), r, r);
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
