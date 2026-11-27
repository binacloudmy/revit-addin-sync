// AI-647: IDwgReader selection, fallback, structured warnings, the key-file parser, and the
// guards that keep ODA types and the licence key where they belong. Runs on every machine;
// nothing here needs ODA (the ODA comparison is OdaParityTests, laptop only).

using System.IO;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.Services.CadToBim.Dwg;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class DwgReaderTests
    {
        private sealed class Fake : IDwgReader
        {
            public string Engine { get; }
            public int Reads;
            public System.Exception Throw;
            public Fake(string engine) => Engine = engine;
            public DwgDrawing Read(string path)
            {
                Reads++;
                if (Throw != null) throw Throw;
                return new DwgDrawing { Document = new CadDocument(), Engine = Engine };
            }
        }

        private static long Plenty() => long.MaxValue;

        [Fact]
        public void Acadsharp_Mode_NeverTouchesOda()
        {
            var oda = new Fake("oda");
            var acad = new Fake("acadsharp");
            var d = DwgReaders.Read("x.dwg", "acadsharp", true, () => oda, acad, Plenty);
            Assert.Equal("acadsharp", d.Engine);
            Assert.Equal(0, oda.Reads);
            Assert.Empty(d.Notes);
        }

        // Owner decision B: v1 ships on ACadSharp; ODA only when config says auto or oda.
        [Fact]
        public void Default_IsAcadSharp_EvenWithOdaInstalled()
        {
            var oda = new Fake("oda");
            Assert.Equal("acadsharp", new CadToBimSettings().DwgReader);
            Assert.Equal("acadsharp", DwgReaders.Read("x.dwg", null, true, () => oda, new Fake("acadsharp"), Plenty).Engine);
            Assert.Equal("acadsharp", DwgReaders.Read("x.dwg", new CadToBimSettings().DwgReader, true, () => oda, new Fake("acadsharp"), Plenty).Engine);
            Assert.Equal(0, oda.Reads);
        }

        [Fact]
        public void Auto_OdaNotInstalled_IsSilentAcadSharp()
        {
            var acad = new Fake("acadsharp");
            var d = DwgReaders.Read("x.dwg", "auto", false, () => new Fake("oda"), acad, Plenty);
            Assert.Equal("acadsharp", d.Engine);
            Assert.Empty(d.Notes);
        }

        [Fact]
        public void Auto_OdaInstalled_ReadsWithOda()
        {
            var d = DwgReaders.Read("x.dwg", "auto", true, () => new Fake("oda"), new Fake("acadsharp"), Plenty);
            Assert.Equal("oda", d.Engine);
        }

        [Fact]
        public void Auto_OdaFails_FallsBackWithOneRedactedNote()
        {
            var oda = new Fake("oda") { Throw = new System.Exception(@"Not activated! C:\Users\someone\oda\bin") };
            var d = DwgReaders.Read("x.dwg", "auto", true, () => oda, new Fake("acadsharp"), Plenty);
            Assert.Equal("acadsharp", d.Engine);
            string note = Assert.Single(d.Notes);
            Assert.StartsWith("Read with the built-in reader: ODA isn't set up correctly (Not activated!", note);
            Assert.DoesNotContain("someone", note);
        }

        [Fact]
        public void Forced_Oda_NeverFallsBack()
        {
            var oda = new Fake("oda") { Throw = new System.InvalidOperationException("boom") };
            Assert.Throws<System.InvalidOperationException>(() =>
                DwgReaders.Read("x.dwg", "oda", true, () => oda, new Fake("acadsharp"), Plenty));
            Assert.Throws<System.InvalidOperationException>(() =>
                DwgReaders.Read("x.dwg", "oda", false, () => oda, new Fake("acadsharp"), Plenty));
        }

        [Fact]
        public void LowMemory_AutoIsAsIfNotInstalled_ForcedRefuses_WarmNeedsLess()
        {
            long between = (DwgReaders.OdaFirstNeedBytes + DwgReaders.OdaLaterNeedBytes) / 2;
            var oda = new Fake("oda");

            // First use, too little for ODA: ACadSharp, no note, no refusal (even with almost nothing free).
            var d = DwgReaders.Read("x.dwg", "auto", true, () => oda, new Fake("acadsharp"), () => 1);
            Assert.Equal("acadsharp", d.Engine);
            Assert.Empty(d.Notes);
            Assert.Equal(0, oda.Reads);

            Assert.Throws<System.InsufficientMemoryException>(() =>
                DwgReaders.Read("x.dwg", "oda", true, () => oda, new Fake("acadsharp"), () => between));

            // Runtime already started: the later-read need applies.
            Assert.Equal("acadsharp", DwgReaders.Read("x.dwg", "auto", true, () => oda, new Fake("acadsharp"), () => between, odaWarm: false).Engine);
            Assert.Equal("oda", DwgReaders.Read("x.dwg", "auto", true, () => oda, new Fake("acadsharp"), () => between, odaWarm: true).Engine);
        }

        // ── D6: "not installed" proven by a config pointing at a missing ODA folder ──

        [Fact]
        public void MissingOdaFolder_GivesTheSameReadDwgPlanAnswerAsBefore()
        {
            string dxf = Path.Combine(Path.GetTempPath(), "ai647-" + System.Guid.NewGuid().ToString("N") + ".dxf");
            try
            {
                var doc = new CadDocument();
                doc.Header.InsUnits = UnitsType.Millimeters;
                var wall = new Layer("A-WALL");
                doc.Layers.Add(wall);
                void L(double x1, double y1, double x2, double y2) =>
                    doc.Entities.Add(new Line { StartPoint = new XYZ(x1, y1, 0), EndPoint = new XYZ(x2, y2, 0), Layer = wall });
                foreach (double o in new[] { 0.0, 200.0 })
                {
                    L(o, o, 10000 - o, o); L(10000 - o, o, 10000 - o, 8000 - o);
                    L(10000 - o, 8000 - o, o, 8000 - o); L(o, 8000 - o, o, o);
                }
                doc.Entities.Add(new TextEntity { Value = "PELAN ARAS 1", InsertPoint = new XYZ(2000, -1500, 0), Height = 250 });
                new DxfWriter(dxf, doc, false).Write();

                var missing = new CadToBimSettings { OdaDir = Path.Combine(Path.GetTempPath(), "no-such-oda-" + System.Guid.NewGuid().ToString("N")) };
                DwgPlanSheet viaReader = DwgPlanReader.Read(DwgReaders.Read(dxf, missing), missing);
                DwgPlanSheet before = DwgPlanReader.Read(Cad2Bim.Services.CadRenderSource.Read(dxf), missing);

                Assert.Equal("acadsharp", viaReader.Engine);
                Assert.Equal(DwgPlanReader.Build(before, new int[0]).Data!.Value.GetRawText(),
                             DwgPlanReader.Build(viaReader, new int[0]).Data!.Value.GetRawText());
                Assert.Equal(DwgPlanReader.Build(before, new[] { 1 }).Data!.Value.GetRawText(),
                             DwgPlanReader.Build(viaReader, new[] { 1 }).Data!.Value.GetRawText());
            }
            finally { try { File.Delete(dxf); } catch (IOException) { } }
        }

        // ── structured warnings (ACadSharp side; AI-624: by the block record's flags) ──

        [Fact]
        public void AcadSharpWarnings_XrefImageAndHostMessages()
        {
            var doc = new CadDocument();
            doc.BlockRecords.Add(new BlockRecord("SITE", @"C:\nowhere\site-" + System.Guid.NewGuid().ToString("N") + ".dwg", false));
            doc.BlockRecords.Add(new BlockRecord("PLAIN"));   // an ordinary block named like nothing: no warning
            doc.ImageDefinitions.Add(new ImageDefinition { Name = "logo", FileName = "missing-logo-" + System.Guid.NewGuid().ToString("N") + ".png" });

            var warnings = DwgDrawing.AcadSharpWarnings(doc, null, new System.Collections.Generic.List<string> { "font romans.shx not found", "font romans.shx not found" });

            var xref = Assert.Single(warnings, w => w.Kind == "xref_missing");
            Assert.Equal("SITE", xref.Name);
            Assert.Single(warnings, w => w.Kind == "image_missing");
            var host = Assert.Single(warnings, w => w.Kind == "host_warning");
            Assert.Equal(2, host.Count);
            Assert.DoesNotContain(warnings, w => w.Name == "PLAIN");
        }

        [Fact]
        public void HostWarnings_CollapseRepeats()
        {
            var rows = DwgDrawing.HostWarnings(new[] { "a", " a ", "b", "" }).ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows.Single(r => r.Detail == "a").Count);
        }

        // ── key file: shape only, synthetic values ──

        [Fact]
        public void KeyFile_Base64Pair_AndRawFallback()
        {
            string path = Path.GetTempFileName();
            try
            {
                string blob = System.Convert.ToBase64String(Encoding.UTF8.GetBytes("\"INFO-123\",\"SIG-4567\""));
                File.WriteAllText(path, blob + "\r\n\r\nSome Label\r\n");
                var (info, sig, shape) = OdaKeyFile.Read(path);
                Assert.Equal("INFO-123", info);
                Assert.Equal("SIG-4567", sig);
                Assert.Equal("base64 pair, lengths 8/8", shape);
                Assert.DoesNotContain("INFO", shape);

                File.WriteAllText(path, "rawinfo\nrawsig\n");
                Assert.Equal(("rawinfo", "rawsig", "raw lines, lengths 7/6"), OdaKeyFile.Read(path));

                File.WriteAllText(path, "\n");
                Assert.Throws<InvalidDataException>(() => OdaKeyFile.Read(path));
            }
            finally { File.Delete(path); }
        }

        // ── guards ──

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root");
            return dir!.FullName;
        }

        private static System.Collections.Generic.IEnumerable<string> SourceFiles(string root) =>
            Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar));

        [Fact]
        public void NoOdaTypeOutsideTheAdapter()
        {
            string root = RepoRoot();
            string adapter = Path.Combine(root, "Services", "CadToBim", "Dwg", "Oda") + Path.DirectorySeparatorChar;
            var leaks = SourceFiles(root)
                .Where(f => f.EndsWith(".cs") && !f.StartsWith(adapter, System.StringComparison.OrdinalIgnoreCase))
                .Where(f => File.ReadAllText(f).Contains("using " + "ODA."))   // split so this file does not match itself
                .ToList();
            Assert.Empty(leaks);
        }

        /// <summary>The key line decodes to "&lt;info&gt;","&lt;signature&gt;" (AI-646 F9); a base64 image or fixture does not.</summary>
        private static bool LooksLikeOdaKey(string candidate)
        {
            try
            {
                string text = Encoding.UTF8.GetString(System.Convert.FromBase64String(candidate));
                return System.Text.RegularExpressions.Regex.IsMatch(text, "^\"[^\"]{16,}\",\"[^\"]{300,}\"");
            }
            catch (System.FormatException) { return false; }
        }

        [Fact]
        public void NoLicenceKeyInTheRepo()
        {
            string root = RepoRoot();
            var named = SourceFiles(root).Where(f =>
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                return n.EndsWith(".key") || (n.Contains("oda") && n.Contains("licen"));
            }).ToList();
            Assert.Empty(named);

            var blob = new System.Text.RegularExpressions.Regex("[A-Za-z0-9+/=]{600,}");
            var text = new[] { ".cs", ".json", ".txt", ".md", ".ps1", ".iss", ".wxs", ".xml", ".config", ".csproj", ".props", ".targets", ".yml" };
            var hits = SourceFiles(root)
                .Where(f => text.Contains(Path.GetExtension(f).ToLowerInvariant()) && new FileInfo(f).Length < 4_000_000)
                .Where(f => blob.Matches(File.ReadAllText(f)).Cast<System.Text.RegularExpressions.Match>().Any(m => LooksLikeOdaKey(m.Value)))
                .ToList();
            Assert.Empty(hits);

            string ignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
            Assert.Contains("*.key", ignore);
            Assert.Contains("*oda*licen*", ignore);
        }
    }
}
