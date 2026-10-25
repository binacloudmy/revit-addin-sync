#nullable enable
// W2 Task 1: Revit-free snapshot builders (spec §7.9, plan §2.4), the
// document identity a turn is bound to, and display-unit symbols.

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SnapshotModelTests
    {
        [Fact]
        public void PathHash_IsLowercaseSha256OfUtf8Path()
        {
            // sha256("C:\\Projek\\Pejabat.rvt"), UTF-8.
            var h = SnapshotBuilder.PathHash("C:\\Projek\\Pejabat.rvt");
            Assert.Matches(new Regex("^[0-9a-f]{64}$"), h!);
            Assert.Equal(SnapshotBuilder.PathHash("C:\\Projek\\Pejabat.rvt"), h);
            Assert.NotEqual(SnapshotBuilder.PathHash("C:\\Projek\\Pejabat2.rvt"), h);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855".Length, h!.Length);
            Assert.Null(SnapshotBuilder.PathHash(""));
            Assert.Null(SnapshotBuilder.PathHash(null));
        }

        [Fact]
        public void PathHash_KnownVector()
        {
            // printf 'abc' | shasum -a 256
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", SnapshotBuilder.PathHash("abc"));
        }

        [Fact]
        public void Selection_CapsIdsAt50_AndCountsByCategory()
        {
            var picked = Enumerable.Range(1, 120)
                .Select(i => (Id: (long)i, Category: i % 3 == 0 ? "Doors" : i % 3 == 1 ? "Walls" : (string?)null))
                .ToList();
            var sel = SnapshotBuilder.Selection(picked);

            Assert.Equal(120, sel.Count);
            Assert.Equal(SnapshotLimits.MaxSelectionIds, sel.Ids.Count);
            Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), sel.Ids);
            Assert.Equal(40, sel.ByCategory["Doors"]);
            Assert.Equal(40, sel.ByCategory["Walls"]);
            Assert.Equal(40, sel.ByCategory["(no category)"]);
        }

        [Fact]
        public void Selection_ByCategory_IsCappedWithAnOtherBucket()
        {
            var picked = Enumerable.Range(1, 30).Select(i => (Id: (long)i, Category: (string?)("Cat" + i))).ToList();
            var sel = SnapshotBuilder.Selection(picked);
            Assert.Equal(SnapshotBuilder.MaxSelectionCategories + 1, sel.ByCategory.Count);
            Assert.Equal(30 - SnapshotBuilder.MaxSelectionCategories, sel.ByCategory["(other)"]);
            Assert.Equal(30, sel.ByCategory.Values.Sum());
        }

        [Fact]
        public void Rooms_ListedUpTo150_OmittedAbove()
        {
            List<SnapshotRoom> Make(int n) => Enumerable.Range(1, n)
                .Select(i => new SnapshotRoom { Name = "Bilik " + i, Number = i.ToString(), Level = "Aras 1" }).ToList();

            Assert.Equal(150, SnapshotBuilder.Rooms(Make(150))!.Count);
            Assert.Null(SnapshotBuilder.Rooms(Make(151)));
            Assert.Empty(SnapshotBuilder.Rooms(Make(0))!);
        }

        [Theory]
        [InlineData(false, false, false, false, "standalone")]
        [InlineData(true, true, false, false, "central")]
        [InlineData(true, false, false, false, "local")]
        [InlineData(true, false, true, false, "detached")]
        [InlineData(false, false, true, false, "detached")]
        [InlineData(true, false, false, true, "read_only")]
        [InlineData(false, false, false, true, "read_only")]
        public void Mode_Resolution(bool workshared, bool central, bool detached, bool readOnly, string expected)
        {
            var mode = SnapshotBuilder.Mode(workshared, central, detached, readOnly);
            Assert.Equal(expected, mode);
            Assert.Contains(mode, SnapshotDocModes.All);
        }

        [Fact]
        public void SessionNonce_HasContractShapeAndIsUnique()
        {
            var a = SnapshotBuilder.NewSessionNonce();
            var b = SnapshotBuilder.NewSessionNonce();
            Assert.Matches(new Regex("^n_[0-9a-f]{12}$"), a);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Fingerprint_BindsToSessionNonce()
        {
            var snap = new SnapshotModel { Doc = new SnapshotDoc { SessionNonce = "n_5b2e9c41d7a8", Title = "Pejabat" } };
            var live = DocIdentity.Fingerprint("n_5b2e9c41d7a8");

            Assert.True(DocIdentity.Matches(live, snap));
            Assert.False(DocIdentity.Matches(DocIdentity.Fingerprint("n_000000000000"), snap));
            // A turn that started with no document never matches a live one.
            Assert.False(DocIdentity.Matches(live, new SnapshotModel()));
            Assert.False(DocIdentity.Matches(live, null));
            Assert.Equal("", DocIdentity.FromSnapshot(new SnapshotModel()));
        }

        [Fact]
        public void Stale_KeepsLastGoodFieldsAndFlags()
        {
            var good = new SnapshotModel
            {
                Doc = new SnapshotDoc { Title = "Pejabat", SessionNonce = "n_5b2e9c41d7a8" },
                ChangeSeq = 18,
                WarningsCount = 47,
            };
            var stale = SnapshotBuilder.Stale(good);
            Assert.True(stale.Stale);
            Assert.Equal("Pejabat", stale.Doc!.Title);
            Assert.Equal(18, stale.ChangeSeq);
            Assert.False(good.Stale);                   // the original is not mutated

            Assert.True(SnapshotBuilder.Stale(null).Stale);
        }

        [Fact]
        public void Clone_IsDeep()
        {
            var a = new SnapshotModel { Doc = new SnapshotDoc { Title = "A" } };
            a.Selection.Ids.Add(5);
            var b = SnapshotBuilder.Clone(a);
            b.Doc!.Title = "B";
            b.Selection.Ids.Add(6);
            Assert.Equal("A", a.Doc!.Title);
            Assert.Single(a.Selection.Ids);
        }

        [Theory]
        [InlineData("autodesk.unit.unit:millimeters-1.0.1", "mm")]
        [InlineData("autodesk.unit.unit:meters-1.0.1", "m")]
        [InlineData("autodesk.unit.unit:centimeters-1.0.1", "cm")]
        [InlineData("autodesk.unit.unit:metersCentimeters-1.0.1", "m")]
        [InlineData("autodesk.unit.unit:feetFractionalInches-1.0.1", "ft")]
        [InlineData("autodesk.unit.unit:fractionalInches-1.0.0", "in")]
        [InlineData("autodesk.unit.unit:squareMeters-1.0.1", "m²")]
        [InlineData("autodesk.unit.unit:cubicMeters-1.0.1", "m³")]
        [InlineData("autodesk.unit.unit:degrees-1.0.1", "°")]
        [InlineData("autodesk.unit.unit:general-1.0.1", "")]
        [InlineData(null, "")]
        public void UnitSymbols_FromForgeTypeId(string? typeId, string expected)
        {
            Assert.Equal(expected, DisplayUnits.SymbolFor(typeId));
        }

        [Theory]
        [InlineData("2400", "mm", "2400 mm")]
        [InlineData("2400 mm", "mm", "2400 mm")]
        [InlineData("2.400 m", "m", "2.400 m")]
        [InlineData("12.5 m²", "m²", "12.5 m²")]
        [InlineData("", "mm", "")]
        [InlineData("Level 2", "", "Level 2")]
        public void WithUnit_AppendsSymbolOnlyWhenMissing(string formatted, string symbol, string expected)
        {
            Assert.Equal(expected, DisplayUnits.WithUnit(formatted, symbol));
        }

        [Theory]
        [InlineData("STR_v3.rvt", "STR_v3")]
        [InlineData("STR_v3.RVT", "STR_v3")]
        [InlineData("STR_v3.rvt : 2 : Shared Site", "STR_v3")]
        [InlineData("MEP Services", "MEP Services")]
        [InlineData("", "(unnamed link)")]
        public void LinkNames_DisplayStripsExtensionAndInstanceSuffix(string raw, string expected)
        {
            Assert.Equal(expected, LinkNames.Display(raw));
        }

        [Fact]
        public void LinkNames_MatchExactFirstThenContains()
        {
            var links = new[] { "STR_v3", "STR_v3_old", "MEP_Services" };
            Assert.Equal(new[] { "STR_v3" }, LinkNames.Match("str_v3.rvt", links));
            Assert.Equal(new[] { "MEP_Services" }, LinkNames.Match("mep", links));
            Assert.Equal(new[] { "STR_v3", "STR_v3_old" }, LinkNames.Match("STR", links));
            Assert.Empty(LinkNames.Match("ARC", links));
        }

        [Fact]
        public void OverviewCacheKey_ChangesWithAnyPart()
        {
            var k = OverviewCacheKey.Of("guid-1", "n_1", 3, 18);
            Assert.Equal(k, OverviewCacheKey.Of("guid-1", "n_1", 3, 18));
            Assert.NotEqual(k, OverviewCacheKey.Of("guid-2", "n_1", 3, 18));
            Assert.NotEqual(k, OverviewCacheKey.Of("guid-1", "n_2", 3, 18));
            Assert.NotEqual(k, OverviewCacheKey.Of("guid-1", "n_1", 4, 18));
            Assert.NotEqual(k, OverviewCacheKey.Of("guid-1", "n_1", 3, 19));
        }
    }
}
