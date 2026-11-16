using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The UiHarness sample runs (data/jkr_run_*.json) through the add-in's own spec
    /// evaluators: the passing sample's model really fails nothing, the failing one
    /// fails what it was built to fail. Keeps the screenshots honest.
    /// </summary>
    public class SpecSampleRunTests
    {
        private static SpecModel Model(string file)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)));
            return json["model"].ToObject<SpecModel>(JsonSerializer.CreateDefault());
        }

        [Fact]
        public void The_passing_sample_model_fails_no_spec_check()
        {
            var report = SpecReport.Build(SpecChecks.Run(Model("jkr_run_compliant.json")));
            var failing = report.Checks.Where(c => c.State == SpecState.Fail)
                .Select(c => c.Check.Id + ": " + string.Join(" | ", c.Notes)).ToList();
            Assert.True(failing.Count == 0, "Failing in the passing sample:\n" + string.Join("\n", failing));
        }

        [Fact]
        public void The_failing_sample_model_fails_the_checks_it_was_built_for()
        {
            var report = SpecReport.Build(SpecChecks.Run(Model("jkr_run_failing.json")));
            var failing = report.Checks.Where(c => c.State == SpecState.Fail).Select(c => c.Check.Id).ToList();
            // Built in: undersized doors and a 35 mm main door, a level without a 3D view,
            // an empty sheet, room-schedule gaps, a grid jump, an RC flat roof without
            // waterproofing, a 2700 mm ceiling, an 1800 mm fence.
            foreach (var id in new[] { "A1.1", "A1.2", "A1.3", "A1.4", "A1.5", "A5.2", "C1.1.3", "C1.6.2", "C1.6.3", "C4.1.3" })
                Assert.True(failing.Contains(id), id + " should fail in the failing sample; failing: " + string.Join(", ", failing));
        }
    }
}
