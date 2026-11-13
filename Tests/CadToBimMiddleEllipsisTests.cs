// Long layer names that share a prefix ("1_Arch_Plan_Walls", "1_Arch_Plan_Doors") must stay
// tellable apart in the 280 px column: MiddleEllipsis keeps the start AND the end.

using System;
using System.Threading;
using System.Windows;
using RevitWebAppSync.UI.CadToBim;
using Xunit;

namespace Tests
{
    public class CadToBimMiddleEllipsisTests
    {
        [Theory]
        [InlineData("1_Arch_Plan_Walls", 12, "1_Arch…_Walls")]   // last segment kept whole
        [InlineData("1_Arch_Plan_Doors", 12, "1_Arch…_Doors")]
        [InlineData("1_Arch_Plan_Walls", 9, "1_Arc…alls")]     // too tight for "_Walls": halves
        [InlineData("1_Arch_Plan_Walls", 16, "1_Arch_P…an_Walls")]   // roomy: half each
        [InlineData("ABCDEFGHIJ", 4, "AB…IJ")]                // no separator: halves
        [InlineData("ABCDEFGHIJ", 5, "ABC…IJ")]               // head gets the odd one
        [InlineData("A-WALL", 6, "A-WALL")]
        [InlineData("A-WALL", 10, "A-WALL")]
        [InlineData("A-WALL", 0, "…")]
        public void Cut_keeps_the_start_and_the_end(string text, int keep, string expected)
        {
            Assert.Equal(expected, MiddleEllipsis.Cut(text, keep));
        }

        [Fact]
        public void Cut_never_splits_a_surrogate_pair()
        {
            string text = "AB\U0001F600CDEF\U0001F600GH";   // each emoji is two chars
            for (int keep = 0; keep < text.Length; keep++)
            {
                string cut = MiddleEllipsis.Cut(text, keep);
                for (int i = 0; i < cut.Length; i++)
                {
                    if (char.IsHighSurrogate(cut[i]))
                        Assert.True(i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1]), keep + ": " + cut);
                    if (char.IsLowSurrogate(cut[i]))
                        Assert.True(i > 0 && char.IsHighSurrogate(cut[i - 1]), keep + ": " + cut);
                }
            }
        }

        [Fact]
        public void Fit_returns_the_text_when_it_fits()
        {
            Assert.Equal("A-WALL", MiddleEllipsis.Fit("A-WALL", s => s.Length <= 10));
            Assert.Equal("", MiddleEllipsis.Fit("", s => false));
            Assert.Equal("", MiddleEllipsis.Fit(null, s => false));
        }

        [Fact]
        public void Fit_finds_the_longest_cut_that_fits()
        {
            // One "width unit" per char: 13 fits = 12 kept + the ellipsis.
            string fitted = MiddleEllipsis.Fit("1_Arch_Plan_Walls", s => s.Length <= 13);
            Assert.Equal("1_Arch…_Walls", fitted);
        }

        [Fact]
        public void Fit_falls_back_to_the_ellipsis_alone()
        {
            Assert.Equal("…", MiddleEllipsis.Fit("1_Arch_Plan_Walls", s => s.Length <= 1));
        }

        [Fact]
        public void Names_that_differ_at_the_end_stay_different_when_cut()
        {
            string[] names =
            {
                "1_Arch_Plan_Walls", "1_Arch_Plan_Doors", "1_Arch_Plan_Windows",
                "2_Arch_Plan_Walls", "2_Arch_Plan_Furniture",
            };
            var shown = new System.Collections.Generic.HashSet<string>();
            foreach (string name in names) shown.Add(MiddleEllipsis.Fit(name, s => s.Length <= 14));
            Assert.Equal(names.Length, shown.Count);
        }

        // The WPF control: measured in a narrow slot it shows a middle cut that keeps both ends.
        [Fact]
        public void The_control_cuts_in_the_middle_in_a_narrow_slot_and_not_in_a_wide_one()
        {
            Exception failure = null;
            string narrow = null, wide = null;
            bool narrowCut = false, wideCut = true;
            var thread = new Thread(() =>
            {
                try
                {
                    var text = new MiddleEllipsisText { Text = "1_Arch_Plan_Walls_Existing", FontSize = 12 };
                    text.Measure(new Size(110, double.PositiveInfinity));
                    narrow = text.Shown;
                    narrowCut = text.IsCut;
                    Assert.True(text.DesiredSize.Width <= 110.5, "desired " + text.DesiredSize.Width);

                    text.Measure(new Size(1000, double.PositiveInfinity));
                    wide = text.Shown;
                    wideCut = text.IsCut;
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null) throw new Exception("STA test failed", failure);
            Assert.True(narrowCut);
            Assert.StartsWith("1_Arch", narrow);
            Assert.EndsWith("_Existing", narrow);
            Assert.Contains(MiddleEllipsis.Ellipsis, narrow);
            Assert.False(wideCut);
            Assert.Equal("1_Arch_Plan_Walls_Existing", wide);
        }
    }
}
