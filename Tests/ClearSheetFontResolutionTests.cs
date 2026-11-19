using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// WPF keys font lookup on the legacy family name (nameID 1) + weight. The Medium and
    /// SemiBold static instances must report the real family, or FontWeight 500/600 under
    /// "#Public Sans" silently falls back to Regular/Bold. Resolves through WPF's own
    /// font lookup and asserts the glyph typeface comes from the matching file.
    /// </summary>
    public class ClearSheetFontResolutionTests
    {
        private static string FontsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "repo root not found");
            return Path.Combine(dir.FullName, "Fonts") + Path.DirectorySeparatorChar;
        }

        [Theory]
        [InlineData("Public Sans", 400, "PublicSans-Regular.ttf")]
        [InlineData("Public Sans", 500, "PublicSans-Medium.ttf")]
        [InlineData("Public Sans", 600, "PublicSans-SemiBold.ttf")]
        [InlineData("Public Sans", 700, "PublicSans-Bold.ttf")]
        [InlineData("IBM Plex Mono", 400, "IBMPlexMono-Regular.ttf")]
        [InlineData("IBM Plex Mono", 500, "IBMPlexMono-Medium.ttf")]
        public void Weight_resolves_to_its_own_font_file(string family, int weight, string file)
        {
            var ff = new FontFamily(new Uri(FontsDir()), "./#" + family);
            var tf = new Typeface(ff, FontStyles.Normal, FontWeight.FromOpenTypeWeight(weight), FontStretches.Normal);
            Assert.True(tf.TryGetGlyphTypeface(out var gt), "no glyph typeface for " + family + " " + weight);
            Assert.Equal(file, Path.GetFileName(gt.FontUri.LocalPath), ignoreCase: true);
            Assert.Equal(weight, gt.Weight.ToOpenTypeWeight());
        }

        // WPF's lookup for embedded (pack URI) fonts keys on nameID 1 (family) + nameID 2
        // (style) of each file. This reads the name table directly, so it fails on the old
        // "Public Sans Medium" / "Regular" naming.
        [Theory]
        [InlineData("PublicSans-Regular.ttf", "Public Sans", "Regular", 400)]
        [InlineData("PublicSans-Medium.ttf", "Public Sans", "Medium", 500)]
        [InlineData("PublicSans-SemiBold.ttf", "Public Sans", "SemiBold", 600)]
        [InlineData("PublicSans-Bold.ttf", "Public Sans", "Bold", 700)]
        [InlineData("IBMPlexMono-Regular.ttf", "IBM Plex Mono", "Regular", 400)]
        [InlineData("IBMPlexMono-Medium.ttf", "IBM Plex Mono", "Medium", 500)]
        public void Name_table_reports_the_real_family_and_style(string file, string family, string style, int weight)
        {
            var b = File.ReadAllBytes(Path.Combine(FontsDir(), file));
            int U16(int o) => (b[o] << 8) | b[o + 1];
            int tables = U16(4), nameOff = -1;
            for (int i = 0; i < tables; i++)
            {
                int r = 12 + i * 16;
                var tag = System.Text.Encoding.ASCII.GetString(b, r, 4);
                int off = (b[r + 8] << 24) | (b[r + 9] << 16) | (b[r + 10] << 8) | b[r + 11];
                if (tag == "name") nameOff = off;
                if (tag == "OS/2") Assert.Equal(weight, U16(off + 4));
            }
            Assert.True(nameOff >= 0);
            int count = U16(nameOff + 2), strOff = nameOff + U16(nameOff + 4);
            string Get(int id)
            {
                for (int i = 0; i < count; i++)
                {
                    int r = nameOff + 6 + i * 12;
                    if (U16(r) == 3 && U16(r + 6) == id)
                        return System.Text.Encoding.BigEndianUnicode.GetString(b, strOff + U16(r + 10), U16(r + 8));
                }
                return null;
            }
            Assert.Equal(family, Get(1));
            Assert.Equal(style, Get(2));
        }
    }
}
