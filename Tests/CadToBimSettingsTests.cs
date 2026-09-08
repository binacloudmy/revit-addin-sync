// CadToBimSettings — the pane's persisted defaults, stored under the "cadToBim"
// key of the same config.json BinaConfig owns. Pinned here: the spec defaults,
// a Save→Load round trip that REPLACES lists (Newtonsoft's default
// ObjectCreationHandling.Auto appends into field initialisers, so a saved
// 6-item hint list would come back as 11), preservation of the other keys in
// the file, and the EN+Malay layer hints.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitWebAppSync.Tests
{
    [Collection("Cad2Bim")]
    public class CadToBimSettingsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public CadToBimSettingsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "bina_cadtobim_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "config.json");
            CadToBimSettings.ConfigPathOverride = _path;
        }

        public void Dispose()
        {
            CadToBimSettings.ConfigPathOverride = null;
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        [Fact]
        public void Defaults_MatchSpec()
        {
            var s = new CadToBimSettings();
            Assert.Equal(50, s.SMinMm);
            Assert.Equal(400, s.SMaxMm);
            Assert.Equal(3000, s.WallHeightMm);
            Assert.Equal(500, s.DoorMinRadiusMm);
            Assert.Equal(1500, s.DoorMaxRadiusMm);
            Assert.Equal(900, s.WindowSillMm);
            Assert.Equal(new[] { "wall", "dinding", "tembok", "partition", "bata" }, s.WallLayerHints);
            Assert.Equal(new[] { "door", "pintu", "win", "tingkap", "glaz" }, s.OpeningLayerHints);
            Assert.Equal(new[] { "win", "window", "tingkap", "glaz", "wdw" }, s.WindowLayerHints);
            Assert.Equal(new[] { "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting",
                                 "*-DIM*", "DEFPOINTS", "G-bubble", "GRID*" }, s.ExcludeGlobs);
            Assert.Null(s.TemplatePath);
        }

        [Fact]
        public void Load_NoFile_ReturnsDefaults()
        {
            Assert.False(File.Exists(_path));
            var s = CadToBimSettings.Load();
            Assert.Equal(400, s.SMaxMm);
            Assert.Equal(5, s.WallLayerHints.Count);
        }

        [Fact]
        public void Load_MissingKey_ReturnsDefaults()
        {
            File.WriteAllText(_path, "{ \"Email\": \"a@b.c\", \"EnginePort\": 48820 }");
            var s = CadToBimSettings.Load();
            Assert.Equal(50, s.SMinMm);
        }

        [Fact]
        public void Load_CorruptFile_ReturnsDefaults()
        {
            File.WriteAllText(_path, "{ not json");
            var s = CadToBimSettings.Load();
            Assert.Equal(3000, s.WallHeightMm);
        }

        [Fact]
        public void SaveThenLoad_RoundTrips_AndReplacesListsInsteadOfAppending()
        {
            var s = new CadToBimSettings { SMinMm = 75, SMaxMm = 350, WallHeightMm = 3300, TemplatePath = @"C:\t\JKR.rte" };
            s.WallLayerHints.Add("kekisi");          // 6 items now
            s.ExcludeGlobs.Clear();
            s.ExcludeGlobs.Add("XREF*");             // 1 item
            s.Save();

            var back = CadToBimSettings.Load();
            Assert.Equal(75, back.SMinMm);
            Assert.Equal(350, back.SMaxMm);
            Assert.Equal(3300, back.WallHeightMm);
            Assert.Equal(@"C:\t\JKR.rte", back.TemplatePath);
            Assert.Equal(6, back.WallLayerHints.Count);          // 11 if lists appended
            Assert.Equal("kekisi", back.WallLayerHints.Last());
            Assert.Equal(new[] { "XREF*" }, back.ExcludeGlobs);   // not defaults + XREF*
        }

        [Fact]
        public void Save_PreservesOtherConfigKeys_AndWritesUnderCadToBim()
        {
            File.WriteAllText(_path, "{ \"Email\": \"a@b.c\", \"EnginePort\": 48820 }");
            new CadToBimSettings { WindowSillMm = 1000 }.Save();

            JObject root = JObject.Parse(File.ReadAllText(_path));
            Assert.Equal("a@b.c", (string)root["Email"]);
            Assert.Equal(48820, (int)root["EnginePort"]);
            Assert.Equal(1000, (double)root["cadToBim"]["WindowSillMm"]);
        }

        [Fact]
        public void Save_CreatesMissingDirectory()
        {
            CadToBimSettings.ConfigPathOverride = Path.Combine(_dir, "nested", "deeper", "config.json");
            new CadToBimSettings().Save();
            Assert.True(File.Exists(CadToBimSettings.ConfigPathOverride));
        }

        [Fact]
        public void IsWallLayer_MatchesHintsCaseInsensitiveSubstring()
        {
            var s = new CadToBimSettings();
            Assert.True(s.IsWallLayer("A-DINDING"));
            Assert.True(s.IsWallLayer("a-wall-ext"));
            Assert.True(s.IsWallLayer("PARTITION_150"));
            Assert.False(s.IsWallLayer("PERABUT"));
            Assert.False(s.IsWallLayer(""));
            Assert.False(s.IsWallLayer(null));
        }

        [Fact]
        public void IsOpeningLayer_MatchesDoorsAndWindows()
        {
            var s = new CadToBimSettings();
            Assert.True(s.IsOpeningLayer("A-PINTU"));
            Assert.True(s.IsOpeningLayer("A-Glazing"));
            Assert.True(s.IsOpeningLayer("WIN-1"));
            Assert.False(s.IsOpeningLayer("A-DINDING"));
        }

        [Fact]
        public void IsWindowLayer_MatchesWindowsOnly_NeverDoors()
        {
            // Finding F6: door-layer linework must never read as a window mark. WindowLayerHints
            // is a window-only subset of OpeningLayerHints (no "door"/"pintu").
            var s = new CadToBimSettings();
            Assert.True(s.IsWindowLayer("A-GLAZ"));
            Assert.True(s.IsWindowLayer("A-WIN-1"));
            Assert.True(s.IsWindowLayer("A-TINGKAP"));
            Assert.False(s.IsWindowLayer("A-DOOR"));
            Assert.False(s.IsWindowLayer("A-PINTU"));
            Assert.False(s.IsWindowLayer(""));
            Assert.False(s.IsWindowLayer(null));
        }

        [Fact]
        public void ToLayerFilter_CarriesExclusionsOnly_NoIncludeList()
        {
            var s = new CadToBimSettings();
            Cad2Bim.LayerFilter f = s.ToLayerFilter();
            Assert.Empty(f.Include);
            Assert.Equal(s.ExcludeGlobs, f.Exclude);
            Assert.False(f.IncludeHatch);
            Assert.False(f.IncludeDimensions);
            // FURN* is start-anchored, so A-FURN-01 (doesn't start with FURN) is allowed
            Assert.True(f.Allows("A-FURN-01", Cad2Bim.Services.CadSource.Geometry));
            // FURN* matches layers starting with FURN
            Assert.False(f.Allows("FURN-01", Cad2Bim.Services.CadSource.Geometry));
            // *-DIM* matches layers containing -DIM anywhere
            Assert.False(f.Allows("A-DIM-TEXT", Cad2Bim.Services.CadSource.Geometry));
            // PERABUT exact match
            Assert.False(f.Allows("PERABUT", Cad2Bim.Services.CadSource.Geometry));
            // A-DINDING contains "dinding" (case-insensitive)
            Assert.True(f.Allows("A-DINDING", Cad2Bim.Services.CadSource.Geometry));
            // 0 has no exclusion match
            Assert.True(f.Allows("0", Cad2Bim.Services.CadSource.Geometry));
        }
    }
}
