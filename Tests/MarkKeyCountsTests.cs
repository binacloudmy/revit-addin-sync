#nullable enable
// The in-model key of a type write (live 13 Nov, F-t04b): one type parameter written on two types of two families whose
// type names are the same (38 and 14 instances, all in the open 3D view). The key read
//   "52 elements changed / in 3 views; 52 in this view / Changed type <family A, cut> 38 / Changed type <family B, cut> 14 /
//    In 2 other views 50 / Showing 52 here · 52 not in this view".
// The type's dependents (its instances AND their tags in two plans, and two more) were all taken as instances, and the
// focus line's second number was "total minus here" over that doubled total. The names are invented.
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Highlights;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MarkKeyCountsTests
    {
        private const string FamA = "LumCo_rcs-dl_rd_LED_2X10W";
        private const string FamB = "LumCo_rcs-dl_rd_LED_2X6W";
        private const long TypeA = 5001, TypeB = 5002;

        private static HighlightSet TwoTypes()
        {
            var set = new HighlightSet { Time = "11:26 PM" };
            set.Kinds.Add(new MarkGroup { Title = FamA + ": Standard", Outcome = MarkOutcome.Changed, Kind = MarkKind.TypeInstances, Ids = { TypeA }, Count = 38 });
            set.Kinds.Add(new MarkGroup { Title = FamB + ": Standard", Outcome = MarkOutcome.Changed, Kind = MarkKind.TypeInstances, Ids = { TypeB }, Count = 14 });
            return set;
        }

        private static List<ElementPlace> Instances() =>
            Enumerable.Range(0, 38).Select(i => new ElementPlace { Id = 100 + i, Group = 0, InActiveView = true })
                .Concat(Enumerable.Range(0, 14).Select(i => new ElementPlace { Id = 200 + i, Group = 1, InActiveView = true })).ToList();

        [Fact]
        public void AllInstancesHere_ShowsAllOfThem_AndNoOtherNumber()
        {
            var key = MarkKey.Build(TwoTypes(), Instances(), "{3D}");
            Assert.Equal("52 elements changed", key.Title);
            Assert.Equal("in {3D}, this view", key.Where);
            Assert.Equal("Showing all 52", key.Focus);
            Assert.Equal((52, 52), (key.Here, key.Total));
        }

        [Fact]
        public void TheFocusLine_NeverSaysTheSameElementsAreHereAndNotHere()
        {
            // What the 0.0.104 build resolved: the 52 instances here, 50 tags owned by two plans, 2 more nowhere.
            var places = Instances();
            places.AddRange(Enumerable.Range(0, 30).Select(i => new ElementPlace { Id = 300 + i, Group = 0, ViewId = 71, ViewName = "Lighting L1", IsPlan = true }));
            places.AddRange(Enumerable.Range(0, 20).Select(i => new ElementPlace { Id = 400 + i, Group = 1, ViewId = 72, ViewName = "Lighting L2", IsPlan = true }));
            places.Add(new ElementPlace { Id = 500, Group = 0 });
            places.Add(new ElementPlace { Id = 501, Group = 1 });

            var key = MarkKey.Build(TwoTypes(), places, "{3D}");

            Assert.DoesNotContain("not in this view", key.Focus);
            Assert.Equal("Showing 52 here · 2 not visible here", key.Focus);
            var other = Assert.Single(key.Lines, l => l.Label.StartsWith("In 2 other"));
            // Each element counted once, and the three numbers add up to the total.
            Assert.Equal(key.Total, key.Here + other.Count + 2);
        }

        [Fact]
        public void AnElementListedTwice_IsOneElement()
        {
            var set = TwoTypes();
            var places = Instances();
            places.AddRange(Instances().Take(5).Select(p => new ElementPlace { Id = p.Id, Group = p.Group, InActiveView = true }));   // listed again
            var key = MarkKey.Build(set, places, "{3D}");
            Assert.Equal(52, key.Here);
            Assert.Equal("Showing all 52", key.Focus);
        }

        [Fact]
        public void ATypesInstances_AreItsDependentsOfThatType_NotTheirTags()
        {
            var deps = new List<(long, long)>
            {
                (100, TypeA), (101, TypeA), (300, 9999),   // a tag: its type is the tag type
                (301, -1),                                  // a legend component with no type of its own here
                (101, TypeA),                               // listed twice
                (200, TypeB),
            };
            Assert.Equal(new long[] { 100, 101, 200 }, HighlightSet.InstancesOnly(deps, new[] { TypeA, TypeB }));
            Assert.Empty(HighlightSet.InstancesOnly(deps, new long[0]));
        }

        [Fact]
        public void ATypesLine_KeepsTheEndOfItsName_WhereTwoFamiliesDiffer()
        {
            var a = MarkKey.LineLabel(TwoTypes().Kinds[0]);
            var b = MarkKey.LineLabel(TwoTypes().Kinds[1]);
            Assert.Equal("Changed type " + FamA + ": Standard", a);          // fits the key's two lines: whole
            Assert.NotEqual(a, b);

            // A name too long for two lines is cut in its middle: the part that tells the two types apart stays.
            var longA = new MarkGroup { Title = "LumCo_lighting_fixture-recessed_downlight_round_LED_2X10W: Standard", Outcome = MarkOutcome.Changed, Kind = MarkKind.TypeInstances };
            var longB = new MarkGroup { Title = "LumCo_lighting_fixture-recessed_downlight_round_LED_2X6W: Standard", Outcome = MarkOutcome.Changed, Kind = MarkKind.TypeInstances };
            var la = MarkKey.LineLabel(longA); var lb = MarkKey.LineLabel(longB);
            Assert.True(la.Length <= MarkKey.MaxLineChars, la);
            Assert.Contains("…", la);
            Assert.EndsWith("2X10W: Standard", la);
            Assert.EndsWith("2X6W: Standard", lb);
            Assert.StartsWith("Changed type LumCo", la);
        }

        [Fact]
        public void TheKeysLabel_MayBreakAfterAnUnderscore_AndReadsTheSame()
        {
            var t = MarkKey.Breakable("Changed type " + FamA + ": Standard");
            Assert.Contains("_​", t);
            Assert.Equal("Changed type " + FamA + ": Standard", t.Replace("​", ""));
        }
    }
}
