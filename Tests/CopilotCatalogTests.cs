using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class CopilotCatalogTests
    {
        [Fact]
        public void Catalog_has_5_vetted_and_12_ai()
        {
            Assert.Equal(5, CopilotCatalog.Vetted.Count);
            Assert.Equal(12, CopilotCatalog.Ai.Count);
            Assert.Equal(17, CopilotCatalog.All.Count());
        }

        [Fact]
        public void Every_tool_has_title_icon_and_tier()
        {
            Assert.All(CopilotCatalog.All, t =>
            {
                Assert.False(string.IsNullOrEmpty(t.Title));
                Assert.False(string.IsNullOrEmpty(t.Icon));
                Assert.True(t.Tier == 1 || t.Tier == 2);
            });
        }

        [Fact]
        public void Vetted_tools_have_fields_and_a_run_label()
        {
            Assert.All(CopilotCatalog.Vetted, t =>
            {
                Assert.NotEmpty(t.Fields);
                Assert.NotNull(t.RunLabel);
                Assert.NotNull(t.PlanText);
            });
        }

        [Fact]
        public void Ai_tools_have_plan_steps_and_code()
        {
            Assert.All(CopilotCatalog.Ai, t =>
            {
                Assert.NotEmpty(t.Plan);
                Assert.False(string.IsNullOrEmpty(t.Code));
            });
        }

        [Fact]
        public void Category_counts_match_tool_distribution()
        {
            foreach (var c in CopilotCatalog.Categories.Where(c => c.Id != "all"))
                Assert.Equal(c.Count, CopilotCatalog.All.Count(t => t.Category == c.Id));

            var all = CopilotCatalog.Categories.First(c => c.Id == "all");
            Assert.Equal(CopilotCatalog.All.Count(), all.Count);
        }

        [Fact]
        public void Seg_field_defaults_are_valid_options()
        {
            foreach (var t in CopilotCatalog.Vetted)
                foreach (var f in t.Fields.Where(f => f.Kind == CpFieldKind.Seg))
                    Assert.Contains(f.Default, f.Options);
        }

        [Fact]
        public void Run_label_and_plan_evaluate_from_defaults()
        {
            foreach (var t in CopilotCatalog.Vetted)
            {
                var values = t.Fields.ToDictionary(f => f.Id, f => f.Default);
                Assert.False(string.IsNullOrEmpty(t.RunLabel(values)));
                Assert.False(string.IsNullOrEmpty(t.PlanText(values)));
                Assert.NotNull(t.Result(values));
            }
        }
    }
}
