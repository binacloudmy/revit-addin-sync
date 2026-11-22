#nullable enable
// The file format of a mark. Revit's in-canvas control (TemporaryGraphicsManager) takes only a .bmp file, by an
// absolute path; any other format throws ArgumentException. Live round 13: the marks had been PNG files since
// 13 November, every AddControl threw, the catch swallowed it, no mark was ever drawn and the key still said
// "Showing all 16". A bitmap has no alpha: Revit clears the pixels of colour RGB(0,128,128) when it draws. So a
// rendered mark (premultiplied BGRA) becomes a 24-bit bitmap in which a pixel that is at least half opaque keeps its
// colour at full strength and any other pixel is the clear colour: no pixel is a blend with it, so no teal fringe.
// WPF-free and Revit-free.

using System;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    public static class MarkBitmap
    {
        public const string Extension = ".bmp";
        /// <summary>The colour Revit treats as transparent in an in-canvas image.</summary>
        public const byte ClearR = 0, ClearG = 128, ClearB = 128;
        private const int Header = 54;

        /// <summary>A 24-bit bottom-up BMP from premultiplied BGRA pixels (rows top-down, 4 bytes per pixel).</summary>
        public static byte[] Encode(byte[] pbgra, int width, int height)
        {
            if (pbgra == null) throw new ArgumentNullException(nameof(pbgra));
            if (width <= 0 || height <= 0 || pbgra.Length < width * height * 4) throw new ArgumentException("the pixels do not fill the size");
            var row = (width * 3 + 3) & ~3;                       // rows are padded to four bytes
            var file = new byte[Header + row * height];
            file[0] = (byte)'B'; file[1] = (byte)'M';
            Put(file, 2, file.Length); Put(file, 10, Header);
            Put(file, 14, 40); Put(file, 18, width); Put(file, 22, height);
            file[26] = 1; file[28] = 24;                           // one plane, 24 bits per pixel, no compression
            Put(file, 34, row * height); Put(file, 38, 2835); Put(file, 42, 2835);
            for (var y = 0; y < height; y++)
            {
                var at = Header + (height - 1 - y) * row;          // the last row comes first
                for (var x = 0; x < width; x++)
                {
                    var p = (y * width + x) * 4;
                    int b = pbgra[p], g = pbgra[p + 1], r = pbgra[p + 2], a = pbgra[p + 3];
                    if (a < 128) { b = ClearB; g = ClearG; r = ClearR; }
                    else
                    {
                        b = Math.Min(255, b * 255 / a); g = Math.Min(255, g * 255 / a); r = Math.Min(255, r * 255 / a);
                        if (r == ClearR && g == ClearG && b == ClearB) b = ClearB + 1;   // a real pixel is never the clear colour
                    }
                    file[at + x * 3] = (byte)b; file[at + x * 3 + 1] = (byte)g; file[at + x * 3 + 2] = (byte)r;
                }
            }
            return file;
        }

        private static void Put(byte[] file, int at, int value)
        {
            file[at] = (byte)value; file[at + 1] = (byte)(value >> 8); file[at + 2] = (byte)(value >> 16); file[at + 3] = (byte)(value >> 24);
        }
    }
}
