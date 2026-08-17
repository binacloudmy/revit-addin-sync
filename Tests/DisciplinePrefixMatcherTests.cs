using System.Collections.Generic;
using RevitWebAppSync;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class DisciplinePrefixMatcherTests
    {
        private static BimDiscipline D(string code, string shortCode = null, string name = null) =>
            new BimDiscipline { Code = code, Name = name ?? code, ShortCode = shortCode };

        private static List<BimDiscipline> SystemSix() => new List<BimDiscipline>
        {
            D("Architecture", "AR"),
            D("Structure", "ST"),
            D("Mechanical", "ME"),
            D("Electrical", "EL"),
            D("Civil", "CV"),
            D("MainFile", "MF", "Coordinated Model"),
        };

        // ── GetPrefix ────────────────────────────────────────

        [Fact]
        public void GetPrefix_PrefersShortCode()
        {
            Assert.Equal("AR", DisciplinePrefixMatcher.GetPrefix(D("Architecture", "AR")));
        }

        [Fact]
        public void GetPrefix_FallsBackToCode_WhenNoShortCode()
        {
            Assert.Equal("FireProtection", DisciplinePrefixMatcher.GetPrefix(D("FireProtection", null)));
        }

        [Fact]
        public void GetPrefix_FallsBackToCode_WhenShortCodeBlank()
        {
            Assert.Equal("FireProtection", DisciplinePrefixMatcher.GetPrefix(D("FireProtection", "   ")));
        }

        [Fact]
        public void GetPrefix_Null_ForNullDiscipline()
        {
            Assert.Null(DisciplinePrefixMatcher.GetPrefix(null));
        }

        // ── Match: current (ShortCode-driven) prefixes ──────────

        [Theory]
        [InlineData("AR_Building.rvt", "Architecture")]
        [InlineData("ST_Building.rvt", "Structure")]
        [InlineData("ME_Building.rvt", "Mechanical")]
        [InlineData("EL_Building.rvt", "Electrical")]
        [InlineData("CV_Building.rvt", "Civil")]
        public void Match_UsesShortCodePrefix(string fileName, string expectedCode)
        {
            var match = DisciplinePrefixMatcher.Match(fileName, SystemSix());
            Assert.NotNull(match);
            Assert.Equal(expectedCode, match.Code);
        }

        [Fact]
        public void Match_IsCaseInsensitive()
        {
            var match = DisciplinePrefixMatcher.Match("ar_Building.rvt", SystemSix());
            Assert.NotNull(match);
            Assert.Equal("Architecture", match.Code);
        }

        [Fact]
        public void Match_FallsBackToCode_WhenDisciplineHasNoShortCode()
        {
            var disciplines = new List<BimDiscipline> { D("FireProtection", null) };
            var match = DisciplinePrefixMatcher.Match("FireProtection_Riser.rvt", disciplines);
            Assert.NotNull(match);
            Assert.Equal("FireProtection", match.Code);
        }

        [Fact]
        public void Match_NeverMatchesMainFile()
        {
            // Even though MainFile's ShortCode is "MF", a file literally
            // prefixed "MF_" must not be classified as MainFile via this path —
            // MainFile is a federation output, not a discipline files are
            // prefixed for.
            var match = DisciplinePrefixMatcher.Match("MF_Combined.rvt", SystemSix());
            Assert.Null(match);
        }

        [Fact]
        public void Match_ReturnsNull_WhenNoPrefixMatches()
        {
            var match = DisciplinePrefixMatcher.Match("RandomFile.rvt", SystemSix());
            Assert.Null(match);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Match_ReturnsNull_ForNullOrEmptyFileName(string fileName)
        {
            Assert.Null(DisciplinePrefixMatcher.Match(fileName, SystemSix()));
        }

        [Fact]
        public void Match_ReturnsNull_ForNullDisciplineList()
        {
            Assert.Null(DisciplinePrefixMatcher.Match("AR_Building.rvt", null));
        }

        [Fact]
        public void Match_ReturnsNull_ForEmptyButNonNullDisciplineList()
        {
            // Distinct from the null-list case above: an empty List<BimDiscipline>
            // is what a project with disciplines configured but none matching
            // (or a fetch that legitimately returned zero rows) looks like — it
            // must fail the same way as "no match found", not throw.
            var match = DisciplinePrefixMatcher.Match("AR_Building.rvt", new List<BimDiscipline>());
            Assert.Null(match);
        }

        [Fact]
        public void Match_FirstListEntryWins_OnShortCodeCollision()
        {
            // DisciplinePrefixMatcher.Match takes the first list-order match with
            // no collision detection. That is intentional, not an oversight: the
            // backend now rejects a second discipline sharing a ShortCode within
            // the same project (project-discipline.service.ts create()/update(),
            // added alongside this test) — see task-8-report.md's follow-up fix.
            // Per-project ShortCode uniqueness is therefore a server-side
            // invariant this code trusts, not something it needs to re-detect.
            // This test only pins the (arbitrary, first-wins) fallback behaviour
            // for data that predates that guard.
            var disciplines = new List<BimDiscipline>
            {
                D("FireProtection", "FP", "Fire Protection"),
                D("FirePump", "FP", "Fire Pump"),
            };

            var match = DisciplinePrefixMatcher.Match("FP_Riser.rvt", disciplines);

            Assert.NotNull(match);
            Assert.Equal("FireProtection", match.Code);
        }

        // ── HVAC -> Mechanical legacy alias ─────────────────────

        [Fact]
        public void Match_AcceptsLegacyHvacPrefix_ForMechanical()
        {
            var match = DisciplinePrefixMatcher.Match("HVAC_Building.rvt", SystemSix());
            Assert.NotNull(match);
            Assert.Equal("Mechanical", match.Code);
        }

        [Fact]
        public void Match_LegacyHvacAlias_IsCaseInsensitive()
        {
            var match = DisciplinePrefixMatcher.Match("hvac_Building.rvt", SystemSix());
            Assert.NotNull(match);
            Assert.Equal("Mechanical", match.Code);
        }

        [Fact]
        public void GetAcceptedPrefixes_IsKeyedByCode_NotByTheAliasString()
        {
            // A hypothetical discipline literally coded "HVAC" gets no extra
            // alias entries — LegacyPrefixAliases is keyed by "Mechanical"
            // (the current code), not by "HVAC" (the alias string), so this
            // must resolve to just its own prefix with no duplication.
            var prefixes = new List<string>(DisciplinePrefixMatcher.GetAcceptedPrefixes(D("HVAC", null)));
            Assert.Equal(new[] { "HVAC" }, prefixes);
        }

        [Fact]
        public void GetAcceptedPrefixes_ForMechanical_IncludesCurrentAndLegacy()
        {
            var prefixes = new List<string>(DisciplinePrefixMatcher.GetAcceptedPrefixes(D("Mechanical", "ME")));
            Assert.Contains("ME", prefixes);
            Assert.Contains("HVAC", prefixes);
        }

        [Fact]
        public void GetAcceptedPrefixes_ForOtherDisciplines_HasNoLegacyAlias()
        {
            var prefixes = new List<string>(DisciplinePrefixMatcher.GetAcceptedPrefixes(D("Electrical", "EL")));
            Assert.Single(prefixes);
            Assert.Equal("EL", prefixes[0]);
        }

        // ── DescribePrefixes ─────────────────────────────────────

        [Fact]
        public void DescribePrefixes_ExcludesMainFile()
        {
            string described = DisciplinePrefixMatcher.DescribePrefixes(SystemSix());
            Assert.DoesNotContain("MF_", described);
            Assert.Contains("AR_", described);
            Assert.Contains("CV_", described);
        }

        [Fact]
        public void DescribePrefixes_EmptyForEmptyList()
        {
            Assert.Equal(string.Empty, DisciplinePrefixMatcher.DescribePrefixes(new List<BimDiscipline>()));
        }
    }
}
