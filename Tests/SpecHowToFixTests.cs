using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The per-check "How to fix manually" steps on the issue cards (data: jkr_howto_fix_steps.csv).</summary>
    public class SpecHowToFixTests
    {
        [Fact]
        public void The_resource_is_embedded_under_its_logical_name()
        {
            Assert.Contains(typeof(SpecHowToFix).Assembly.GetManifestResourceNames(), n => n.EndsWith(SpecHowToFix.ResourceName));
        }

        [Fact]
        public void C1_5_2_steps_name_no_second_folder_level_because_Jenis_Lukisan_jkr_pit_is_a_sheet_parameter()
        {
            var steps = SpecHowToFix.Find("C1.5.2").Steps;
            Assert.DoesNotContain(steps, x => x.Contains("01_Lukisan Utama"));
            Assert.Contains(steps, x => x.Contains("07d_Dokumen > 3D Views") && x.Contains("second folder level stays blank"));
        }

        [Fact]
        public void Every_one_of_the_61_checks_has_steps_and_the_csv_has_no_unknown_id()
        {
            var catalog = SpecCatalog.Checks.Select(c => c.Id).ToList();
            Assert.Equal(61, catalog.Count);
            foreach (var id in catalog)
            {
                var e = SpecHowToFix.Find(id);
                Assert.True(e != null && e.Steps.Count > 0, "no steps for " + id);
            }
            Assert.Equal(61, SpecHowToFix.All.Count);
            Assert.Empty(SpecHowToFix.All.Keys.Except(catalog));
        }

        [Fact]
        public void The_embedded_text_has_no_bom_and_every_entry_has_why_and_source()
        {
            Assert.DoesNotContain(SpecHowToFix.All.Keys, k => k.Contains('﻿'));
            Assert.True(SpecHowToFix.All.ContainsKey("A1.1"));   // first row: a BOM would break this key
            foreach (var e in SpecHowToFix.All.Values) Assert.NotEmpty(e.Why);
        }

        [Fact]
        public void Parse_strips_a_bom_splits_steps_and_reads_quoted_commas()
        {
            var m = SpecHowToFix.Parse("﻿check_id,steps,why,source,copilot_fixable\r\nX1,\"Open A, then B | Do C\",\"Because \"\"x\"\"\",NS 1,yes\nX2, One ,w,s,no\n");
            Assert.Equal(new[] { "Open A, then B", "Do C" }, m["X1"].Steps);
            Assert.Equal("Because \"x\"", m["X1"].Why);
            Assert.True(m["X1"].CopilotFixable);
            Assert.Equal("Why: Because \"x\" Source: NS 1", m["X1"].Note);
            Assert.Equal(new[] { "One" }, m["X2"].Steps);
            Assert.False(m["X2"].CopilotFixable);
        }

        [Fact]
        public void Copilot_fixable_checks_still_get_their_manual_steps()
        {
            var yes = SpecHowToFix.All.Values.Where(e => e.CopilotFixable).ToList();
            foreach (var e in yes) Assert.NotEmpty(e.Steps);
        }
    }
}
