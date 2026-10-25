#nullable enable
// W2 Task 4: "why can't I see it?" (spec §11 #3). The Revit-bound explainer
// reads plain facts per element; VisibilityReasons orders them (category,
// element, temporary hide, filters, template, workset, phase, design option,
// view range / crop, other level) and gives each a fix in EN and BM.

using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class VisibilityReasonTests
    {
        private static VisibilityFacts Facts(long id) =>
            new VisibilityFacts { Ref = new ElementRef(id, null), Category = "Doors", CollectorVisible = false };

        [Fact]
        public void OrdersAndFormatsReasons()
        {
            var f = Facts(1);
            // Set out of order on purpose.
            f.OtherLevel = "Level 3";
            f.ViewLevel = "Level 2";
            f.WorksetHidden = "Doors-L3";
            f.HidingFilters.Add("Hide FR doors");
            f.CategoryHidden = true;
            f.TemplateName = "JKR Plan 1:100";
            f.TemplateControlsCategories = true;
            f.TemplateControlsFilters = true;
            f.TemporaryHidden = true;
            f.ElementHidden = true;
            f.Range = RangeCheck.Below;
            f.RangeDetail = "below the view depth (-300 mm)";
            f.PhaseHidden = "Demolished, not shown by phase filter 'Show New'";
            f.DesignOptionHidden = "Option 2 (secondary, set 'Lobby')";

            var reasons = VisibilityReasons.Explain(f);
            Assert.Equal(new[]
            {
                "category_hidden", "element_hidden", "temporary_hide_isolate", "filter", "template_controlled",
                "workset_hidden", "phase", "design_option", "view_range_or_crop", "other_level",
            }, reasons.Select(r => r.Code));
            Assert.Equal(reasons.Select(r => (int)r.Kind).OrderBy(k => k), reasons.Select(r => (int)r.Kind));

            var cat = reasons[0];
            Assert.Contains("Doors", cat.Detail);
            Assert.Contains("JKR Plan 1:100", cat.FixEn);           // template controls it: fix the template
            Assert.Contains("templat", cat.FixMs);
            Assert.Contains("Hide FR doors", reasons[3].Detail);
            Assert.Contains("JKR Plan 1:100", reasons[3].FixEn);
            Assert.Contains("Doors-L3", reasons[5].FixEn);
            Assert.Contains("Level 3", reasons[9].Detail);
            Assert.Contains("Level 2", reasons[9].Detail);
            Assert.All(reasons, r =>
            {
                Assert.False(string.IsNullOrWhiteSpace(r.FixEn));
                Assert.False(string.IsNullOrWhiteSpace(r.FixMs));
                Assert.NotEqual(r.FixEn, r.FixMs);
            });
        }

        [Fact]
        public void WithoutTemplate_FixesPointAtTheViewItself()
        {
            var f = Facts(1);
            f.CategoryHidden = true;
            var r = Assert.Single(VisibilityReasons.Explain(f));
            Assert.Contains("Visibility/Graphics", r.FixEn);
            Assert.DoesNotContain("template", r.FixEn);
        }

        [Fact]
        public void TemplateReason_OnlyWhenTheTemplateControlsWhatHides()
        {
            var f = Facts(1);
            f.TemplateName = "JKR Plan";
            f.TemplateControlsCategories = true;              // controls categories, but nothing is hidden by them
            f.WorksetHidden = "Shared Levels";
            Assert.DoesNotContain(VisibilityReasons.Explain(f), r => r.Code == "template_controlled");

            f.TemplateControlsWorksets = true;
            Assert.Contains(VisibilityReasons.Explain(f), r => r.Code == "template_controlled");
        }

        [Fact]
        public void NoReason_WhenVisible_AndAnUnknownWhenHiddenForNoKnownReason()
        {
            var visible = Facts(1);
            visible.CollectorVisible = true;
            Assert.Empty(VisibilityReasons.Explain(visible));

            var odd = Facts(2);
            var r = Assert.Single(VisibilityReasons.Explain(odd));
            Assert.Equal("not_determined", r.Code);
            Assert.Contains("Detail Level", r.FixEn);
        }

        [Fact]
        public void Report_GroupsElementsWithTheSameReasons_AndSummarizes()
        {
            var a = Facts(1); a.HidingFilters.Add("Hide FR doors");
            var b = Facts(2); b.HidingFilters.Add("Hide FR doors");
            var c = Facts(3); c.WorksetHidden = "Doors-L3";
            var d = Facts(4); d.CollectorVisible = true;

            var env = VisibilityReasons.Build("Level 2 - Floor Plan", "FloorPlan", null, new[] { a, b, c, d });
            Assert.True(EnvelopeInvariants.Violations(env).Count == 0, string.Join("; ", EnvelopeInvariants.Violations(env)));
            var data = env.Data!.Value;
            Assert.Equal(4, data.GetProperty("checked").GetInt32());
            Assert.Equal(1, data.GetProperty("visible").GetInt32());
            Assert.Equal(3, data.GetProperty("not_visible").GetInt32());
            var groups = data.GetProperty("groups");
            Assert.Equal(2, groups.GetArrayLength());
            Assert.Equal(2, groups[0].GetProperty("count").GetInt32());
            Assert.Equal("filter", groups[0].GetProperty("reasons")[0].GetProperty("code").GetString());
            Assert.StartsWith("In 'Level 2 - Floor Plan', 3 of 4 elements are not visible", env.Summary);
            Assert.Contains("filter 'Hide FR doors'", env.Summary);
        }

        [Theory]
        [InlineData(0, 3000, -300, 1200, RangeCheck.InRange)]   // cut through
        [InlineData(-900, -400, -300, 1200, RangeCheck.Below)]  // under the view depth
        [InlineData(1500, 2400, -300, 1200, RangeCheck.AboveCut)]
        [InlineData(-300, -300, -300, 1200, RangeCheck.InRange)]
        public void PlanRange_ClassifiesAgainstDepthAndCutPlane(double min, double max, double depth, double cut, RangeCheck expected)
        {
            Assert.Equal(expected, ViewExtent.PlanRange(min, max, depth, cut));
        }

        [Fact]
        public void CeilingPlanRange_LooksUp()
        {
            Assert.Equal(RangeCheck.Below, ViewExtent.CeilingRange(0, 1000, 1200, 4000));
            Assert.Equal(RangeCheck.InRange, ViewExtent.CeilingRange(2600, 2700, 1200, 4000));
            Assert.Equal(RangeCheck.AboveTop, ViewExtent.CeilingRange(4100, 4200, 1200, 4000));
        }

        [Theory]
        [InlineData(1, 1, 2, 2, CropCheck.Inside)]
        [InlineData(-1, 1, 2, 2, CropCheck.Partial)]
        [InlineData(11, 11, 12, 12, CropCheck.Outside)]
        public void Crop_ClassifiesAgainstTheCropRectangle(double minX, double minY, double maxX, double maxY, CropCheck expected)
        {
            Assert.Equal(expected, ViewExtent.Crop(minX, minY, maxX, maxY, 0, 0, 10, 10));
        }
    }
}
