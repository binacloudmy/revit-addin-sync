#nullable enable
// W2 Task 1: Revit-free snapshot builders (spec §7.9, plan §2.4), the
// document identity a turn is bound to, and display-unit symbols.

using System;
using System.IO;
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
        [Fact]
        public void OverviewCache_HitsForSameKey_ClearsOnNewKey()
        {
            var cache = new OverviewCache();
            var k1 = OverviewCacheKey.Of("g", "n_1", 0, 1);
            var levels = new OverviewSection { Name = "levels" };
            cache.Put(k1, "levels", levels);
            Assert.True(cache.TryGet(k1, "levels", out var hit));
            Assert.Same(levels, hit);
            Assert.False(cache.TryGet(k1, "views", out _));

            var k2 = OverviewCacheKey.Of("g", "n_1", 0, 2);   // one DocumentChanged later
            Assert.False(cache.TryGet(k2, "levels", out _));
            cache.Put(k2, "views", new OverviewSection { Name = "views" });
            Assert.False(cache.TryGet(k1, "levels", out _));  // the old key is gone
        }
        // Rulings R24/R33: doc.project_key = SnapshotDoc.ProjectKeyOf(UniqueId, P); P is the
        // central path (or cloud model GUID) when workshared, else PathName; families hash "".
        [Fact]
        public void ProjectKey_ComposesUniqueIdAndCentralPath()
        {
            const string uid = "7d1b3c9e-0001-4c2a-9a7e-000000000001-0000c3f2";
            const string central = "\\\\srv\\Projek\\Pejabat_central.rvt";
            // sha256(uid + "|" + central), computed independently.
            Assert.Equal("a3117faafe8af66c48b365e6f0f04282f0779b8246ae128b4a88fcc80f706eb4",
                SnapshotBuilder.ProjectKey(false, uid, true, false, central, null, "C:\\Users\\me\\Pejabat_local.rvt"));
            // The local copy's path never matters for a workshared model.
            Assert.Equal(SnapshotBuilder.ProjectKey(false, uid, true, false, central, null, "D:\\other_local.rvt"),
                SnapshotBuilder.ProjectKey(false, uid, true, false, central, null, "C:\\Users\\me\\Pejabat_local.rvt"));
            Assert.Equal(SnapshotDoc.ProjectKeyOf(uid, central), SnapshotBuilder.ProjectKey(false, uid, true, false, central, null, ""));
        }

        [Fact]
        public void ProjectKey_FamilyHashesEmptyUniqueId_SameTemplateDifferentFileDiffers()
        {
            Assert.Equal("39674985912ef8498ad99f2df9524c7ad24a9785f1cab66cd50e0c1f82988e25", SnapshotDoc.ProjectKeyOf("abc", null));
            Assert.Equal(SnapshotDoc.ProjectKeyOf("", "C:\\Pintu.rfa"), SnapshotBuilder.ProjectKey(true, "family-uid", false, false, null, null, "C:\\Pintu.rfa"));
            // Projects made from one template share ProjectInformation.UniqueId (spec §7.9).
            Assert.NotEqual(SnapshotBuilder.ProjectKey(false, "same", false, false, null, null, "C:\\A.rvt"),
                SnapshotBuilder.ProjectKey(false, "same", false, false, null, null, "C:\\B.rvt"));
        }

        [Theory]
        [InlineData(true, true, "\\\\srv\\central.rvt", "guid-1", "C:\\local.rvt", "guid-1")]
        [InlineData(true, false, "\\\\srv\\central.rvt", null, "C:\\local.rvt", "\\\\srv\\central.rvt")]
        [InlineData(false, false, null, null, "C:\\solo.rvt", "C:\\solo.rvt")]
        [InlineData(false, false, null, null, "", "")]
        public void ProjectKeyPath_PicksCloudGuidCentralOrPath(bool workshared, bool cloud, string? central, string? cloudGuid, string path, string expected)
        {
            Assert.Equal(expected, SnapshotBuilder.ProjectKeyPath(workshared, cloud, central, cloudGuid, path));
        }
        [Fact]
        public void Selection_UnclassifiedIdsKeepTheTotal()
        {
            // Review M7: only the first SnapshotService.MaxClassifiedSelection ids are classified.
            var picked = Enumerable.Range(1, 10)
                .Select(i => (Id: (long)i, Category: i <= 4 ? "Doors" : (string?)SnapshotBuilder.NotClassified)).ToList();
            var sel = SnapshotBuilder.Selection(picked);
            Assert.Equal(10, sel.Count);
            Assert.Equal(4, sel.ByCategory["Doors"]);
            Assert.Equal(6, sel.ByCategory[SnapshotBuilder.NotClassified]);
        }
        // ── ruling R56: identity always current, only expensive reads wait for the pane ──

        private static readonly System.TimeSpan Long = System.TimeSpan.FromMinutes(1);

        [Fact]
        public void RefreshPlan_PaneClosed_KeepsIdentity_SkipsExpensiveReads()
        {
            var p = SnapshotRefresh.Plan(paneOpen: false, force: false, docChanged: true, docDirty: true, viewDirty: true,
                sinceDocRefresh: Long, warningsDirty: true, sinceWarnings: Long);
            Assert.True(p.Identity);
            Assert.True(p.DocParts);
            Assert.False(p.Expensive);
            Assert.False(p.Warnings);

            var selectionOnly = SnapshotRefresh.Plan(false, false, false, false, viewDirty: true, Long, false, Long);
            Assert.True(selectionOnly.Identity);
            Assert.False(selectionOnly.DocParts);
        }

        [Fact]
        public void RefreshPlan_PaneOpen_WarningsAtMostEvery5Seconds()
        {
            System.TimeSpan S(double s) => System.TimeSpan.FromSeconds(s);
            Assert.False(SnapshotRefresh.Plan(true, false, false, true, false, S(2), true, S(2)).Warnings);
            Assert.True(SnapshotRefresh.Plan(true, false, false, true, false, S(2), true, S(6)).Warnings);
            Assert.True(SnapshotRefresh.Plan(true, false, docChanged: true, true, false, S(0), false, S(0)).Warnings);
            Assert.True(SnapshotRefresh.Plan(true, false, false, true, false, S(2), false, S(9)).Expensive);
            var forced = SnapshotRefresh.Plan(false, force: true, false, false, false, S(0), false, S(0));
            Assert.True(forced.Identity && forced.DocParts && forced.Expensive && forced.Warnings);
            Assert.False(SnapshotRefresh.Plan(true, false, false, false, false, Long, true, Long).Identity);   // nothing dirty
        }

        [Fact]
        public void Compose_FromAnotherDocsHeavyParts_DocIdentityPopulatedAndNotStale()
        {
            var doc = new SnapshotDoc
            {
                Kind = SnapshotDocKinds.Project, Title = "Pejabat", SessionNonce = "n_5b2e9c41d7a8",
                ProjectKey = SnapshotDoc.ProjectKeyOf("uid", "C:\\Pejabat.rvt"), VersionGuid = "3f0c8e5e", Mode = SnapshotDocModes.Standalone,
            };
            var heavyFromOtherDoc = new SnapshotHeavyParts { DocKey = "path:C:\\Other.rvt", WarningsCount = 47, Rooms = new List<SnapshotRoom>() };
            var selection = SnapshotBuilder.Selection(new[] { (1L, (string?)SnapshotBuilder.NotClassified), (2L, (string?)SnapshotBuilder.NotClassified) });
            var snap = SnapshotRefresh.Compose("path:C:\\Pejabat.rvt", doc, new SnapshotView { Name = "Level 2" }, selection, null,
                new[] { new SnapshotLink { Name = "STR_v3", Loaded = false } }, heavyFromOtherDoc, 18);

            Assert.False(snap.Stale);
            Assert.Equal(doc.ProjectKey, snap.Doc!.ProjectKey);
            Assert.Equal("3f0c8e5e", snap.Doc.VersionGuid);
            Assert.True(DocIdentity.Matches(DocIdentity.Fingerprint("n_5b2e9c41d7a8"), snap));
            Assert.Equal(2, snap.Selection.Count);
            Assert.Equal(new long[] { 1, 2 }, snap.Selection.Ids);
            Assert.Equal(0, snap.WarningsCount);              // another document's count is never reused
            Assert.Null(snap.Rooms);
            Assert.Single(snap.Links);
        }

        [Fact]
        public void Compose_SameDoc_KeepsTheLastExpensiveValues()
        {
            var heavy = new SnapshotHeavyParts { DocKey = "k", WarningsCount = 47, Rooms = new List<SnapshotRoom> { new SnapshotRoom { Name = "Surau" } } };
            var snap = SnapshotRefresh.Compose("k", new SnapshotDoc { SessionNonce = "n_1" }, null, new SnapshotSelection(), null,
                new List<SnapshotLink>(), heavy, 3);
            Assert.Equal(47, snap.WarningsCount);           // may be stale while the pane is closed
            Assert.Equal("Surau", Assert.Single(snap.Rooms!).Name);
            Assert.False(snap.Stale);
        }
        // ── fix round 3: a warnings-only refresh never declassifies the selection ──

        private static SnapshotModel WithDoorsSelected() => new SnapshotModel
        {
            Doc = new SnapshotDoc { SessionNonce = "n_1", Title = "Pejabat" },
            Selection = SnapshotBuilder.Selection(new[] { (1L, (string?)"Doors"), (2L, (string?)"Doors"), (3L, (string?)"Doors") }),
        };

        [Fact]
        public void RefreshPlan_WarningsOnly_WithPaneOpen_KeepsTheClassifiedSelection()
        {
            // Re-review 2, Important 1: edits throttled the warnings; 5 s later a warnings-only
            // refresh used to rebuild the selection unclassified.
            var plan = SnapshotRefresh.Plan(paneOpen: true, force: false, docChanged: false, docDirty: false, viewDirty: false,
                sinceDocRefresh: Long, warningsDirty: true, sinceWarnings: Long);
            Assert.True(plan.Warnings);
            Assert.False(plan.Identity);
            Assert.False(plan.RebuildSelection);
            Assert.True(plan.Classify);

            var built = false;
            var sel = SnapshotRefresh.SelectionFor(plan, WithDoorsSelected(), sameDocument: true, classify => { built = true; return new SnapshotSelection(); });
            Assert.False(built);
            Assert.Equal(3, sel.ByCategory["Doors"]);
            Assert.DoesNotContain(SnapshotBuilder.NotClassified, sel.ByCategory.Keys);
        }

        [Fact]
        public void RefreshPlan_PaneOpen_RebuildsClassified_PaneClosed_Unclassified()
        {
            var open = SnapshotRefresh.Plan(true, false, false, false, viewDirty: true, Long, false, Long);
            Assert.True(open.RebuildSelection);
            Assert.True(open.Classify);
            bool? asked = null;
            SnapshotRefresh.SelectionFor(open, WithDoorsSelected(), true, classify => { asked = classify; return new SnapshotSelection(); });
            Assert.True(asked);

            var closed = SnapshotRefresh.Plan(false, false, false, false, viewDirty: true, Long, false, Long);
            Assert.False(closed.Classify);
            SnapshotRefresh.SelectionFor(closed, WithDoorsSelected(), true, classify => { asked = classify; return new SnapshotSelection(); });
            Assert.False(asked);
        }

        [Fact]
        public void SelectionFor_AnotherDocument_AlwaysRebuilds()
        {
            var plan = SnapshotRefresh.Plan(true, false, false, false, false, Long, true, Long);   // warnings-only
            var built = false;
            SnapshotRefresh.SelectionFor(plan, WithDoorsSelected(), sameDocument: false, classify => { built = true; return new SnapshotSelection(); });
            Assert.True(built);
        }

        [Fact]
        public void FailedRefresh_AfterADocumentSwitch_DropsTheOldIdentity()
        {
            // Re-review 2, Minor 1: a throwing refresh must not present the previous document as current.
            var failed = SnapshotRefresh.Failed(WithDoorsSelected(), sameDocument: false);
            Assert.True(failed.Stale);
            Assert.Null(failed.Doc);
            Assert.Equal(0, failed.Selection.Count);
            Assert.False(DocIdentity.Matches(DocIdentity.Fingerprint("n_1"), failed));

            var same = SnapshotRefresh.Failed(WithDoorsSelected(), sameDocument: true);
            Assert.True(same.Stale);
            Assert.Equal("n_1", same.Doc!.SessionNonce);
            Assert.Equal(3, same.Selection.Count);
        }

        [Fact]
        public void UnclassifiedSelection_CountsCapsAndSkipsCategories()
        {
            var sel = SnapshotBuilder.UnclassifiedSelection(5000, Enumerable.Range(1, 5000).Select(i => (long)i));
            Assert.Equal(5000, sel.Count);
            Assert.Equal(SnapshotLimits.MaxSelectionIds, sel.Ids.Count);
            Assert.Equal(5000, sel.ByCategory[SnapshotBuilder.NotClassified]);
            Assert.Empty(SnapshotBuilder.UnclassifiedSelection(0, new long[0]).ByCategory);
        }

        [Fact]
        public void SnapshotService_HandlersNeverReturnEarlyOnAClosedPane()
        {
            // Re-review 2, Minor 4: the R56 wiring is Revit-bound; pin its source shape.
            var src = SourceOf(Path.Combine("BinaVibe", "Copilot", "Read", "SnapshotService.cs"));
            string Body(string start)
            {
                var i = src.IndexOf(start, StringComparison.Ordinal);
                Assert.True(i >= 0, "missing: " + start);
                var j = src.IndexOf("        private static ", i + start.Length, StringComparison.Ordinal);
                return src.Substring(i, (j > i ? j : src.Length) - i);
            }
            foreach (var handler in new[] { "private static void OnIdling(", "private static void OnSelectionChanged(" })
            {
                var body = Body(handler);
                Assert.DoesNotMatch(new Regex(@"if\s*\(\s*!\s*_paneOpen\s*\)\s*(\{[^}]*)?return"), body);
            }
            var refresh = Body("private static void Refresh(UIApplication app, bool force)");
            Assert.Contains("SnapshotRefresh.Plan(", refresh);
            Assert.Contains("SnapshotRefresh.SelectionFor(", refresh);
            Assert.Contains("SnapshotRefresh.Failed(", refresh);
        }

        private static string SourceOf(string relative, [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!); dir != null; dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p)) return File.ReadAllText(p).Replace("\r\n", "\n");
            }
            throw new FileNotFoundException(relative);
        }
    }
}
