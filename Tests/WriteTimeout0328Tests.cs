#nullable enable
// AI-328: a bulk type swap (335 walls) hit the code time budget ("0/1 done, 1 failed (timed out)", nothing changed). The budget is 180 s.
using System;
using System.IO;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WriteTimeout0328Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheCodeTimeBudget_Is180Seconds_InTheJsonAndTheTable()
        {
            Assert.Contains("\"max_seconds\": 180,", Src("Resources/copilot/code-policy.json"));
            Assert.Contains("MaxSeconds = 180,", Src("BinaVibe/Copilot/Code/CodePolicyTable.cs"));
            Assert.Equal(TimeSpan.FromSeconds(180), LoopBudget.FromPolicy().MaxTime);
        }

        [Fact]
        public void TheStepAndDepthLimits_AreUnchanged()
        {
            var b = LoopBudget.FromPolicy();
            Assert.Equal(2_000_000, b.MaxTicks);
            Assert.Equal(64, b.MaxDepth);
        }

        [Fact]
        public void ATimeStop_TellsTheModelToWorkInBatches()
        {
            var f = ErrorMapper.FromException(new BudgetExceededException(BudgetReason.Time, 1000, TimeSpan.FromSeconds(181), 1), null, readMode: false);
            Assert.Equal("timeout", f.Code);
            Assert.Contains("180 s", f.Summary);
            Assert.Contains("about 50 elements per run", f.Next);
            var steps = ErrorMapper.FromException(new BudgetExceededException(BudgetReason.Steps, 2_000_001, TimeSpan.FromSeconds(2), 1), null, readMode: false);
            Assert.DoesNotContain("50 elements", steps.Next);
        }
    }
}
