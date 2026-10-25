#nullable enable
// W3 fix round 1 (ruling R42). Undo entry names and tool labels are owned by
// the add-in: the model's run_revit_code intent never reaches the undo name
// or the headline; it is exposed separately for the card's "Assistant's
// description" line.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WriteLabelsTests
    {
        private static WritePlan Code(string intent) => new WritePlan
        {
            Tool = ToolNames.RunRevitCode,
            CanonicalArgs = "{\"code\":\"Print(1);\",\"intent\":\"" + intent + "\"}",
        };

        [Fact]
        public void LabelsAreAddInOwned()
        {
            Assert.Equal("Revit code", WriteLabels.ForPlan(Code("Delete every wall, then rename it 'Set Mark'")));
            Assert.Equal("BINA: Revit code", WriteLabels.GroupName(new[] { Code("anything") }));
            var set = new WritePlan { Tool = ToolNames.SetParameters, CanonicalArgs = "{\"param\":\"Mark\",\"value\":\"A1\"}", TargetIds = new long[] { 1, 2 } };
            Assert.Equal("Set parameter Mark", WriteLabels.ForPlan(set));
            Assert.Equal("BINA: Set parameter Mark (+1 more)", WriteLabels.GroupName(new[] { set, Code("x") }));
        }

        [Fact]
        public void IntentIsExposedSeparately()
        {
            Assert.Equal("Add 4 walls on Level 1", WriteLabels.IntentOf(Code("Add 4 walls on Level 1")));
            Assert.Null(WriteLabels.IntentOf(new WritePlan { Tool = ToolNames.SetParameters, CanonicalArgs = "{\"param\":\"Mark\"}" }));
            Assert.Equal("Set the marks.\nCode: Add 4 walls on Level 1",
                WriteLabels.AssistantDescription("Set the marks.", new[] { Code("Add 4 walls on Level 1") }));
            Assert.Null(WriteLabels.AssistantDescription(null, new WritePlan[0]));
        }
    }
}
