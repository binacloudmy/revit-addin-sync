#nullable enable
// W2 Task 3: Malay <-> English synonyms for find_elements (plan §5 W2 Task 3).
// Resources/copilot/synonyms-ms-en.json is the source of truth; the add-in
// compiles BinaVibe/Copilot/Read/SynonymTable.g.cs because the csproj does not
// embed Resources/copilot/*. JsonAndTableAgree renders the table from the JSON
// and compares it byte-for-byte; on drift it writes the expected file next to
// the test output, so regenerating is: run the test, copy the .expected file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SynonymTests
    {
        private static string RepoFile(string relative, [CallerFilePath] string thisFile = "")
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!); dir != null; dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p)) return p;
            }
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p)) return p;
            }
            throw new FileNotFoundException(relative);
        }

        private static string JsonPath => RepoFile(Path.Combine("Resources", "copilot", "synonyms-ms-en.json"));
        private static string TablePath => RepoFile(Path.Combine("BinaVibe", "Copilot", "Read", "SynonymTable.g.cs"));

        [Theory]
        [InlineData("pintu", "OST_Doors")]
        [InlineData("Pintu", "OST_Doors")]
        [InlineData("pintu-pintu", "OST_Doors")]
        [InlineData("doors", "OST_Doors")]
        [InlineData("tingkap", "OST_Windows")]
        [InlineData("dinding", "OST_Walls")]
        [InlineData("lantai", "OST_Floors")]
        [InlineData("siling", "OST_Ceilings")]
        [InlineData("bumbung", "OST_Roofs")]
        [InlineData("tangga", "OST_Stairs")]
        [InlineData("aras", "OST_Levels")]
        [InlineData("bilik", "OST_Rooms")]
        [InlineData("tiang struktur", "OST_StructuralColumns")]
        [InlineData("rasuk", "OST_StructuralFraming")]
        [InlineData("sinki", "OST_PlumbingFixtures")]
        [InlineData("lampu", "OST_LightingFixtures")]
        [InlineData("pemercik", "OST_Sprinklers")]
        public void MalayTermsMapToCategories(string term, string expected)
        {
            Assert.Equal(expected, Synonyms.CategoryFor(term));
        }

        [Theory]
        [InlineData("tandas")]
        [InlineData("surau")]
        [InlineData("kalis api")]
        [InlineData("oku")]
        [InlineData("zzz")]
        public void RoomAndAdjectiveWords_HaveNoCategory(string term)
        {
            Assert.Null(Synonyms.CategoryFor(term));
        }

        [Theory]
        [InlineData("tandas", "toilet")]
        [InlineData("toilet", "tandas")]
        [InlineData("pintu", "door")]
        [InlineData("tingkap", "window")]
        [InlineData("aras", "level")]
        [InlineData("siling", "ceiling")]
        [InlineData("dinding", "wall")]
        [InlineData("lantai", "floor")]
        [InlineData("bumbung", "roof")]
        [InlineData("tangga", "stair")]
        [InlineData("surau", "prayer room")]
        [InlineData("kalis api", "fire rated")]
        [InlineData("Kalis-Api", "fire resistant")]
        [InlineData("OKU", "accessible")]
        public void BothSidesAreVariantsOfEachOther(string term, string other)
        {
            Assert.Contains(Fuzzy.Fold(other), Synonyms.Variants(term));
        }

        [Fact]
        public void Scan_FindsLongestPhrasesFirst_AndLeavesTheRest()
        {
            var scan = Synonyms.Scan("pintu kalis api tandas OKU blok A");
            Assert.Equal(new[] { "pintu", "kalis api", "tandas oku" }, scan.Matches.Select(m => m.Phrase));
            Assert.Equal("OST_Doors", scan.Matches[0].Entry.Category);
            Assert.Equal(new[] { "blok", "a" }, scan.Leftover);
        }

        [Fact]
        public void Scan_CollapsesMalayReduplication()
        {
            var scan = Synonyms.Scan("pintu-pintu");
            Assert.Single(scan.Matches);
            Assert.Empty(scan.Leftover);
        }

        [Fact]
        public void Table_HasAtLeast80Pairs_AndNoVariantInTwoEntries()
        {
            Assert.True(Synonyms.All.Count >= 80, "pairs: " + Synonyms.All.Count);
            var owner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in Synonyms.All)
                foreach (var v in e.Variants)
                {
                    var f = Fuzzy.Fold(v);
                    Assert.False(owner.TryGetValue(f, out var other) && other != e.Ms, "'" + v + "' is in both '" + other + "' and '" + e.Ms + "'");
                    owner[f] = e.Ms;
                }
            Assert.All(Synonyms.All.Where(e => e.Category != null), e => Assert.StartsWith("OST_", e.Category));
        }

        [Fact]
        public void JsonAndTableAgree()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(JsonPath));
            var expected = Render(doc);
            var actual = File.ReadAllText(TablePath).Replace("\r\n", "\n");
            if (expected != actual)
            {
                var outPath = Path.Combine(AppContext.BaseDirectory, "SynonymTable.g.cs.expected");
                File.WriteAllText(outPath, expected);
                Assert.Fail("SynonymTable.g.cs is out of date with synonyms-ms-en.json. Copy " + outPath + " over " + TablePath + ".");
            }
        }

        /// <summary>The generator: synonyms-ms-en.json -> SynonymTable.g.cs.</summary>
        internal static string Render(JsonDocument doc)
        {
            static string Lit(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var root = doc.RootElement;
            var sb = new StringBuilder();
            sb.Append("#nullable enable\n");
            sb.Append("// <auto-generated>\n");
            sb.Append("// Generated from Resources/copilot/synonyms-ms-en.json, the source of truth.\n");
            sb.Append("// RevitWebAppSync.csproj does not embed Resources/copilot/*, so the table is\n");
            sb.Append("// compiled in. Do not edit by hand: edit the JSON, run SynonymTests.JsonAndTableAgree\n");
            sb.Append("// and copy the SynonymTable.g.cs.expected it writes over this file.\n");
            sb.Append("// </auto-generated>\n\n");
            sb.Append("namespace BinaVibe.Copilot.Read\n{\n");
            sb.Append("    internal static class SynonymTable\n    {\n");
            sb.Append("        public const int Version = " + root.GetProperty("version").GetInt32() + ";\n\n");
            sb.Append("        public static readonly SynonymEntry[] Entries =\n        {\n");
            foreach (var p in root.GetProperty("pairs").EnumerateArray())
            {
                var ms = p.GetProperty("ms").GetString()!;
                var en = p.GetProperty("en").GetString()!;
                var cat = p.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? Lit(c.GetString()!) : "null";
                var also = p.TryGetProperty("also", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => Lit(x.GetString()!)).ToList()
                    : new List<string>();
                var alsoText = also.Count == 0 ? "new string[0]" : "new[] { " + string.Join(", ", also) + " }";
                sb.Append("            new SynonymEntry(" + Lit(ms) + ", " + Lit(en) + ", " + cat + ", " + alsoText + "),\n");
            }
            sb.Append("        };\n    }\n}\n");
            return sb.ToString();
        }
    }
}
