#nullable enable
// Live test round 9, F-t04: find_elements {"category": "Lighting Fixtures", "type": "<Family>: Normal"}
// returned every lighting family's "Normal" type (the wanted text CONTAINED the type name "Normal"),
// and a type-parameter write on that handle changed 10 types instead of 2.
//
// TypeNameMatch.Filter is the one type-filter rule of find_elements (FindExecutor), set_parameters'
// filter target and the old predicate (Inspectors.PredicateMatches). The families here are invented.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TypeFilterTests
    {
        // Three families each with a type "Normal"; one has a second type.
        private static readonly (string Family, string Type)[] Fixtures =
        {
            ("Lumen Alpha Downlight", "Normal"),
            ("Lumen Alpha Downlight", "Wide Beam"),
            ("Lumen Beta Panel", "Normal"),
            ("Lumen Gamma Strip", "Normal"),
        };

        private static string[] Matched(string want) =>
            Fixtures.Where(f => TypeNameMatch.Filter(want, f.Family, f.Type)).Select(f => f.Family + ": " + f.Type).ToArray();

        [Fact]
        public void Family_and_type_keeps_to_that_family()
        {
            Assert.Equal(new[] { "Lumen Alpha Downlight: Normal" }, Matched("Lumen Alpha Downlight: Normal"));
        }

        [Theory]
        [InlineData("lumen alpha downlight: NORMAL")]
        [InlineData("Lumen Alpha Downlight:Normal")]
        [InlineData("  Lumen  Alpha Downlight  :   Normal ")]
        [InlineData("Lumen Alpha Downlight : Normal")]
        public void Family_and_type_ignores_case_and_spacing(string want)
        {
            Assert.Equal(new[] { "Lumen Alpha Downlight: Normal" }, Matched(want));
        }

        [Fact]
        public void Bare_type_name_reaches_every_family_with_it()
        {
            Assert.Equal(new[] { "Lumen Alpha Downlight: Normal", "Lumen Beta Panel: Normal", "Lumen Gamma Strip: Normal" }, Matched("Normal"));
        }

        [Fact]
        public void Family_name_alone_takes_all_its_types_and_no_other_family()
        {
            // F-t01: a family name with no type returned that family's fixtures; that stays.
            Assert.Equal(new[] { "Lumen Alpha Downlight: Normal", "Lumen Alpha Downlight: Wide Beam" }, Matched("Lumen Alpha Downlight"));
        }

        [Theory]
        [InlineData("Lumen Delta Spot: Normal")]       // no such family
        [InlineData("Lumen Alpha Downlight: Narrow")]  // no such type in that family
        [InlineData("Lumen Alpha: Normal")]            // a family part is matched whole, never as a part
        [InlineData("Lumen Beta Panel: Wide Beam")]    // the type exists, but in another family
        public void Family_and_type_that_does_not_exist_matches_nothing(string want)
        {
            Assert.Empty(Matched(want));
        }

        [Fact]
        public void Type_name_holding_a_colon_still_matches_whole()
        {
            Assert.True(TypeNameMatch.Filter("Ratio 1:2", "Vent Grille", "Ratio 1:2"));
            Assert.True(TypeNameMatch.Filter("Vent Grille: Ratio 1:2", "Vent Grille", "Ratio 1:2"));
            Assert.False(TypeNameMatch.Filter("Vent Grille: Ratio 1:2", "Other Grille", "Ratio 1:2"));
        }

        [Fact]
        public void A_bare_part_of_a_coded_name_still_matches()
        {
            // JKR-style names carry a code in parentheses and the model often passes one part.
            Assert.True(TypeNameMatch.Filter("(XQz042a)", "Basin Pedestal (XQz042a)", "Normal"));
            Assert.True(TypeNameMatch.Filter("basin pedestal", "Basin Pedestal (XQz042a)", "Normal"));
            Assert.False(TypeNameMatch.Filter("(XQz043a)", "Basin Pedestal (XQz042a)", "Normal"));
        }

        [Fact]
        public void A_longer_text_does_not_match_a_short_name_inside_it()
        {
            // The old rule matched when the wanted text contained the type name.
            Assert.False(TypeNameMatch.Filter("Lumen Delta Spot Normal", "Lumen Beta Panel", "Normal"));
        }

        [Fact]
        public void Write_resolution_and_read_filter_agree_on_family_and_type()
        {
            var candidates = Fixtures.Select((f, i) => new TypeCandidate(i + 1, f.Type, f.Family)).ToList();
            var picked = TypeNameMatch.Pick("lumen beta panel:normal", candidates);
            Assert.Equal(TypeMatchKind.Found, picked.Kind);
            var c = candidates.Single(x => x.Id == picked.Id);
            Assert.Equal(new[] { c.Label }, Matched("lumen beta panel:normal"));
        }

        // ── what find_elements says about it ─────────────────────────────

        private static FindPlan PlanOf(string json)
        {
            Assert.True(QueryParser.TryParse(JsonDocument.Parse(json).RootElement.Clone(), out var plan, out var error), error);
            return plan;
        }

        private static FindOutcome OutcomeOf(FindPlan plan, IEnumerable<(string Family, string Type, int Count)> rows)
        {
            var o = new FindOutcome { Sources = { "host" }, Categories = { "Lighting Fixtures" } };
            long id = 1000;
            foreach (var (family, type, n) in rows)
                for (var i = 0; i < n; i++)
                    o.Rows.Add(new FoundRow { Ref = new ElementRef(id++, null), Category = "Lighting Fixtures", Family = family, Type = type, GroupKey = type });
            var q = plan.Queries[0];
            o.Queries.Add(new QueryOutcome { Count = o.Rows.Count, Filter = FindText.Filter(q, "Lighting Fixtures", null), Subject = FindText.Subject(q, "Lighting Fixtures", null) });
            return o;
        }

        [Fact]
        public void Bare_type_in_several_families_names_them_with_counts()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Lighting Fixtures\",\"type\":\"Normal\"}]}");
            var env = FindResultBuilder.Build(plan, OutcomeOf(plan, new[]
            {
                ("Lumen Alpha Downlight", "Normal", 38),
                ("Lumen Beta Panel", "Normal", 20),
                ("Lumen Gamma Strip", "Normal", 5),
            }));
            Assert.True(EnvelopeInvariants.Violations(env).Count == 0, string.Join("; ", EnvelopeInvariants.Violations(env)));
            Assert.Equal(63, env.Count);
            Assert.Equal("63 Lighting Fixtures of type 'Normal' in the host model, in 3 families: 38 Lumen Alpha Downlight, 20 Lumen Beta Panel, 5 Lumen Gamma Strip.", env.Summary);
            Assert.Equal("category = Lighting Fixtures AND type ~ 'Normal' (3 families)", env.Searched!.Filter);
            var notes = env.Data!.Value.GetProperty("notes").EnumerateArray().Select(n => n.GetString()).ToList();
            Assert.Contains(notes, n => n!.StartsWith("Type 'Normal' is in 3 families: Lumen Alpha Downlight 38, Lumen Beta Panel 20, Lumen Gamma Strip 5.")
                                       && n.Contains("'Family: Normal'"));
        }

        [Fact]
        public void Family_and_type_says_family_and_type_in_the_filter_and_adds_no_spread()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Lighting Fixtures\",\"type\":\"Lumen Alpha Downlight: Normal\"}]}");
            var env = FindResultBuilder.Build(plan, OutcomeOf(plan, new[] { ("Lumen Alpha Downlight", "Normal", 2) }));
            Assert.Equal("category = Lighting Fixtures AND family = 'Lumen Alpha Downlight' AND type = 'Normal'", env.Searched!.Filter);
            Assert.StartsWith("2 Lighting Fixtures of type 'Lumen Alpha Downlight: Normal' in the host model.", env.Summary);
            Assert.False(env.Data!.Value.TryGetProperty("notes", out _));
        }

        [Fact]
        public void Bare_type_in_one_family_adds_no_spread()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Lighting Fixtures\",\"type\":\"Wide Beam\"}]}");
            var env = FindResultBuilder.Build(plan, OutcomeOf(plan, new[] { ("Lumen Alpha Downlight", "Wide Beam", 4) }));
            Assert.Equal("category = Lighting Fixtures AND type ~ 'Wide Beam'", env.Searched!.Filter);
            Assert.False(env.Data!.Value.TryGetProperty("notes", out _));
        }
    }
}
