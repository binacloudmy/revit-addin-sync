using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The tally: the pane's checklist is the JKR Model Audit spec, one-to-one.
    /// data/jkr_spec_rules.tsv and data/jkr_spec_items.tsv are the spec workbook's
    /// "Peraturan comply" and "Borang A-E" sheets exported verbatim (openpyxl), so a
    /// spec edit that is not carried into SpecCatalog — or a check with no evaluator
    /// and no stated backend dependency — fails here.
    /// </summary>
    public class SpecTallyTests
    {
        private sealed class SpecRow
        {
            public string Section, Item, Id, What, Comply, NotComply, Manual, Class, Reference;
        }

        private static List<string[]> Tsv(string name) =>
            File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", name))
                .Where(l => !l.StartsWith("#")).Skip(1)          // comment, header
                .Select(l => l.Split('\t')).ToList();

        private static List<SpecRow> Rules() =>
            Tsv("jkr_spec_rules.tsv")
                .Where(c => c.Length >= 9 && c[2].Length > 0)       // the totals row has no id
                .Select(c => new SpecRow
                {
                    Section = c[0], Item = NormItem(c[1]), Id = c[2], What = c[3], Comply = c[4],
                    NotComply = c[5], Manual = c[6], Class = c[7], Reference = c[8],
                }).ToList();

        /// <summary>"C 1.1" → "C1.1"; "C 2.0 / 2.1" → "C2.1" (the scored child row).</summary>
        private static string NormItem(string item)
        {
            var s = (item ?? "").Trim();
            if (s.Contains("/")) s = s.Split('/').Last().Trim().Insert(0, s.Substring(0, 1));
            return s.Replace(" ", "");
        }

        private static SpecCellClass ClassOf(string kelas)
        {
            switch (kelas.Trim())
            {
                case "AI-verifiable": return SpecCellClass.Ai;
                case "human-decides": return SpecCellClass.Human;
                default: return SpecCellClass.List;   // "senarai sahaja (tiada verdict)"
            }
        }

        public static IEnumerable<object[]> SpecIds() => Rules().Select(r => new object[] { r.Id });

        // ── the 61 rows ──

        [Fact]
        public void Spec_and_catalog_have_the_same_61_checks_in_the_same_order()
        {
            var spec = Rules();
            Assert.Equal(61, spec.Count);
            Assert.Equal(spec.Select(r => r.Id), SpecCatalog.Checks.Select(c => c.Id));
        }

        [Fact]
        public void Totals_row_matches_the_catalog_classes()
        {
            var totals = Tsv("jkr_spec_rules.tsv").Single(c => c[0] == "JUMLAH");
            Assert.Contains("61", totals[3]);
            Assert.Equal("AI-verifiable: " + SpecCatalog.Checks.Count(c => c.Class == SpecCellClass.Ai), totals[4]);
            Assert.Equal("human-decides: " + SpecCatalog.Checks.Count(c => c.Class == SpecCellClass.Human), totals[5]);
            Assert.Equal("senarai sahaja: " + SpecCatalog.Checks.Count(c => c.Class == SpecCellClass.List), totals[6]);
        }

        [Theory]
        [MemberData(nameof(SpecIds))]
        public void Each_check_has_the_spec_section_item_class_reference_and_wording(string id)
        {
            var row = Rules().Single(r => r.Id == id);
            var c = SpecCatalog.Find(id);
            Assert.NotNull(c);
            Assert.Equal(row.Section, c.Section);
            Assert.Equal(row.Item, c.Item);
            Assert.Equal(ClassOf(row.Class), c.Class);
            Assert.Equal(row.Reference, c.Reference);
            Assert.Equal(row.What, c.Title.Ms);                 // BM = the spec's "Apa yang disemak", verbatim
            Assert.False(string.IsNullOrWhiteSpace(c.Title.En));
        }

        [Theory]
        [MemberData(nameof(SpecIds))]
        public void Each_check_carries_the_spec_verdict_criteria(string id)
        {
            var row = Rules().Single(r => r.Id == id);
            var c = SpecCatalog.Find(id);
            Assert.Equal(row.Comply, c.Comply.Ms);
            Assert.Equal(row.NotComply, c.NotComply.Ms);
            Assert.Equal(row.Manual, c.Manual.Ms);
        }

        [Fact]
        public void Item_titles_are_the_Borang_A_E_titles()
        {
            var items = Tsv("jkr_spec_items.tsv").Where(c => c.Length >= 3).ToDictionary(c => NormItem(c[1]), c => c[2]);
            foreach (var item in SpecCatalog.Items)
            {
                Assert.True(items.ContainsKey(item.No), "Borang A-E has no item " + item.No);
                Assert.Equal(items[item.No], item.Title.Ms);
            }
            Assert.Equal(SpecCatalog.Items.Select(i => i.No).Distinct(), Rules().Select(r => r.Item).Distinct());
        }

        // ── every check is implemented, or its backend dependency is stated ──

        [Theory]
        [MemberData(nameof(SpecIds))]
        public void Each_check_has_an_evaluator_or_a_stated_backend_dependency(string id)
        {
            var hasRule = SpecChecks.Rules.ContainsKey(id);
            var backend = SpecChecks.BackendOnly.TryGetValue(id, out var why);
            Assert.True(hasRule || backend, id + " has no evaluator and no stated backend dependency");
            if (backend) Assert.False(string.IsNullOrWhiteSpace(why), id + ": backend dependency without a reason");
        }

        [Fact]
        public void No_evaluator_answers_a_check_the_spec_does_not_have()
        {
            var ids = new HashSet<string>(SpecCatalog.Checks.Select(c => c.Id));
            Assert.Empty(SpecChecks.Rules.Keys.Where(k => !ids.Contains(k)));
            Assert.Empty(SpecChecks.BackendOnly.Keys.Where(k => !ids.Contains(k)));
        }

        [Fact]
        public void An_empty_model_runs_every_evaluator_without_a_crash()
        {
            // An empty model has nothing to fail on; checks whose rule is "absence complies"
            // (A1.4: no RC flat roof) may pass, as the spec defines. None may crash.
            var results = SpecChecks.Run(new SpecModel());
            var report = SpecReport.Build(results);
            Assert.DoesNotContain(results, r => (r.Note ?? "").Contains("check failed to run"));
            Assert.Equal(61, report.Checks.Count);
        }

        [Fact]
        public void A_null_model_runs_like_an_empty_one()
        {
            var results = SpecChecks.Run(null);
            Assert.All(results, r => Assert.Equal(SpecReport.Model, r.Source));
            Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.CheckId)));
        }
    }
}
