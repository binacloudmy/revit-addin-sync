#nullable enable
// The "Gabung" result view, G2 and G3: the dot and the badge as Revit takes them (a 24-bit .bmp whose clear colour is
// RGB(0,128,128)). The centre of a dot is the outcome's colour, its corner is clear, a dim dot is another colour.
// Rendered with WPF, so these run where WPF does (the Windows test run), on an STA thread.
using System;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MarkImagesTests
    {
        private static T Sta<T>(Func<T> f)
        {
            T result = default!; Exception? failure = null;
            var t = new System.Threading.Thread(() => { try { result = f(); } catch (Exception ex) { failure = ex; } });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start(); t.Join();
            if (failure != null) throw failure;
            return result;
        }

        private static (int W, int H) Size(byte[] bmp) => (BitConverter.ToInt32(bmp, 18), BitConverter.ToInt32(bmp, 22));

        private static (byte R, byte G, byte B) At(byte[] bmp, int x, int y)
        {
            var (w, h) = Size(bmp);
            var row = (w * 3 + 3) & ~3;
            var at = 54 + (h - 1 - y) * row + x * 3;
            return (bmp[at + 2], bmp[at + 1], bmp[at]);
        }

        private static (byte, byte, byte) Rgb(string token, bool dark)
        {
            var hex = ThemeTokens.Hex(token, dark)!.TrimStart('#');
            if (hex.Length == 8) hex = hex.Substring(2);
            return (Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
        }

        private static readonly (byte, byte, byte) Clear = (MarkBitmap.ClearR, MarkBitmap.ClearG, MarkBitmap.ClearB);

        [Theory]
        [InlineData(MarkOutcome.Changed, true, 1.0)]
        [InlineData(MarkOutcome.Made, false, 1.0)]
        [InlineData(MarkOutcome.Changed, true, 2.0)]
        public void ADot_IsABitmap_ItsCentreTheOutcomesColour_ItsCornerClear(MarkOutcome o, bool dark, double scale)
        {
            var bmp = Sta(() => MarkImages.RenderDot(o, scale, dark));
            Assert.Equal((byte)'B', bmp[0]); Assert.Equal((byte)'M', bmp[1]);
            Assert.Equal(24, BitConverter.ToInt16(bmp, 28));
            var (w, h) = Size(bmp);
            Assert.Equal((int)Math.Round(MarkKey.MarkerDip * scale), w);   // r12: a 30 DIP marker (ring, white halo, solid dot)
            Assert.Equal((255, 255, 255), At(bmp, w / 2, (int)Math.Round((MarkKey.MarkerRing + MarkKey.MarkerHalo / 2) * scale)));   // the white halo
            Assert.Equal(w, h);
            Assert.Equal(Rgb(HighlightSet.TokenOf(o), dark), At(bmp, w / 2, h / 2));
            Assert.Equal(Clear, At(bmp, 0, 0));
            Assert.Equal(Clear, At(bmp, w - 1, h - 1));
        }

        [Fact]
        public void ADimDot_IsAnotherColour_AndStillNotTheClearColour()
        {
            var normal = Sta(() => MarkImages.RenderDot(MarkOutcome.Changed, 1, true));
            var dim = Sta(() => MarkImages.RenderDot(MarkOutcome.Changed, 1, true, MarkTone.Dim));
            var (w, h) = Size(dim);
            Assert.NotEqual(At(normal, w / 2, h / 2), At(dim, w / 2, h / 2));
            Assert.NotEqual(Clear, At(dim, w / 2, h / 2));
            Assert.Equal(Clear, At(dim, 0, 0));
        }

        [Fact]
        public void ABadge_IsAPillInTheOutcomesColour_WideEnoughForItsText()
        {
            var two = Sta(() => MarkImages.RenderBadge(MarkOutcome.Changed, "4", 1, true));
            var wide = Sta(() => MarkImages.RenderBadge(MarkOutcome.Changed, "99+", 1, true));
            var (w, h) = Size(two);
            Assert.Equal((int)Math.Round(MarkImages.BadgeHeight), h);
            Assert.True(w >= h);
            Assert.True(Size(wide).W > w);
            Assert.Equal(Clear, At(two, 0, 0));                                                     // the pill's round corner
            Assert.Equal(Rgb(HighlightSet.TokenOf(MarkOutcome.Changed), true), At(two, 3, h / 2));   // inside the pill, left of the text
            var dim = Sta(() => MarkImages.RenderBadge(MarkOutcome.Changed, "4", 1, true, MarkTone.Dim));
            Assert.NotEqual(At(two, 3, h / 2), At(dim, 3, h / 2));
        }

        [Fact]
        public void AFrame_IsStillHollow_ForATagsText()
        {
            var bmp = Sta(() => MarkImages.RenderFrame(MarkOutcome.Made, 30, 1, true));
            Assert.Equal((30, 30), Size(bmp));
            Assert.Equal(Clear, At(bmp, 15, 15));     // nothing over the tag's text
            Assert.NotEqual(Clear, At(bmp, 3, 15));   // the frame's side
        }
    }
}
