// AI-638: writes Tests/data/cad2bim/aswara-sheet.json from the real ASWARA DWG (laptop only), and
// guards it against drift. Gated like DwgRealSheetTests: AI610_ASWARA_DWG names the drawing (it is
// not in the repo); AI638_CAPTURE=1 additionally (re)writes the committed snapshot. Unset = returns
// early and passes (xunit 2 has no runtime skip).

#nullable enable

using System;
using System.IO;
using System.Runtime.CompilerServices;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim.Check;
using Xunit;
using Xunit.Abstractions;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class CheckFixtureCapture
    {
        private readonly ITestOutputHelper _out;
        public CheckFixtureCapture(ITestOutputHelper output) => _out = output;

        private static string SourceDataDir([CallerFilePath] string self = "") =>
            Path.Combine(Path.GetDirectoryName(self)!, "data", "cad2bim");

        [Fact]
        public void Aswara_snapshot_matches_the_real_read()
        {
            string? path = Environment.GetEnvironmentVariable("AI610_ASWARA_DWG");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { _out.WriteLine("skipped: AI610_ASWARA_DWG not set or missing"); return; }

            string json = SheetSnapshot.Read(path, new CadToBimSettings()).ToJson();
            string committed = Path.Combine(SourceDataDir(), "aswara-sheet.json");

            if (Environment.GetEnvironmentVariable("AI638_CAPTURE") == "1")
            {
                File.WriteAllText(committed, json);
                _out.WriteLine("wrote " + committed + " (" + json.Length / 1024 + " KB)");
                return;
            }
            // Drift guard: a classifier change must re-capture on purpose.
            Assert.Equal(File.ReadAllText(committed).Replace("\r\n", "\n"), json.Replace("\r\n", "\n"));
        }
    }
}
