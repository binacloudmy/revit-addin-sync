#nullable enable
// Live round 9 (E-c07): a stored script changing the type of a few hundred walls was stopped at 30 s, though
// Revit itself needs that long. The backend now keeps the stored script's name in run_revit_code's args
// ("playbook", only when it rendered the code); such a run gets budget.max_seconds_stored (120 s). The model's
// own code keeps 30 s; the steps limit is the same for both. The stop message names the limit that applied.

using System;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class StoredScriptBudgetTests
    {
        private static RunRevitCodeArgs Parse(string json)
        {
            Assert.True(RunRevitCodeArgs.TryParse(JsonDocument.Parse(json).RootElement.Clone(), out var args, out var error), error);
            return args!;
        }

        [Fact]
        public void Args_with_a_playbook_name_carry_it()
        {
            var a = Parse("{\"code\":\"Print(1);\",\"intent\":\"change type\",\"playbook\":\" change-type \"}");
            Assert.Equal("change-type", a.Playbook);
            Assert.Contains("\"playbook\":\"change-type\"", a.Canonical());
        }

        [Theory]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\"}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"playbook\":\"\"}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"playbook\":\"   \"}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"playbook\":null}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"playbook\":7}")]
        [InlineData("{\"code\":\"Print(1);\",\"intent\":\"x\",\"playbook\":{\"name\":\"change-type\"}}")]
        public void Args_without_a_usable_name_are_model_code(string json)
        {
            var a = Parse(json);
            Assert.Null(a.Playbook);
            Assert.DoesNotContain("playbook", a.Canonical());
        }

        [Fact]
        public void A_stored_script_gets_120_s_and_model_code_30_s_with_the_same_steps()
        {
            var model = LoopBudget.FromPolicy();
            var stored = LoopBudget.FromPolicy(storedScript: true);
            Assert.Equal(30, model.MaxTime.TotalSeconds);
            Assert.Equal(120, stored.MaxTime.TotalSeconds);
            Assert.Equal(model.MaxTicks, stored.MaxTicks);
            Assert.Equal(model.MaxDepth, stored.MaxDepth);
        }

        [Fact]
        public void The_stop_message_names_the_limit_that_applied()
        {
            var clock = TimeSpan.Zero;
            var budget = new LoopBudget(long.MaxValue, TimeSpan.FromSeconds(120), 64, () => clock += TimeSpan.FromSeconds(1));
            var prepared = CodeRunner.Prepare("while (true) { }", CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.True(prepared.Snippet != null, prepared.Failure?.Message);
            var r = CodeRunner.Execute(prepared.Snippet!, Array.Empty<object?>(), budget, new OutputBuffer());

            Assert.False(r.Ok);
            Assert.Contains("the time limit of 120 s", r.Failure!.Summary);
            Assert.Contains("the time limit of 120 s", r.Failure.Message);
        }
    }
}
