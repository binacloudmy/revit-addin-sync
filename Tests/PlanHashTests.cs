#nullable enable
// W3 Task 1 (lean-harness plan §5 W3, spec §7.4 step 1). The plan hash binds
// the confirm card's Ya to the batch the add-in holds: it must not move when
// only key order or list order changes, and must move when any target,
// intended value, expected count, code text or tool changes.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class PlanHashTests
    {
        private static WritePlan Plan(
            string args = "{\"param\":\"Mark\",\"target\":{\"ids\":[3,1,2]},\"value\":\"A1\"}",
            long[]? targets = null,
            Dictionary<string, string>? intended = null) => new WritePlan
        {
            Tool = ToolNames.SetParameters,
            CanonicalArgs = args,
            TargetIds = targets ?? new long[] { 1, 2, 3 },
            IntendedValues = intended ?? new Dictionary<string, string> { ["1"] = "A1", ["2"] = "A1", ["3"] = "A1" },
            ExpectedAddedByCategory = new Dictionary<string, int> { ["Walls"] = 2, ["Doors"] = 1 },
            ExpectedDeletedByCategory = new Dictionary<string, int>(),
        };

        [Fact]
        public void StableAcrossKeyOrder()
        {
            var a = Plan();
            var b = new WritePlan
            {
                Tool = ToolNames.SetParameters,
                // Same args with every object's keys in another order and the number written differently.
                CanonicalArgs = "{ \"value\": \"A1\", \"target\": { \"ids\": [3, 1, 2] }, \"param\": \"Mark\" }",
                TargetIds = new long[] { 3, 2, 1, 2 },
                IntendedValues = new Dictionary<string, string> { ["3"] = "A1", ["1"] = "A1", ["2"] = "A1" },
                ExpectedAddedByCategory = new Dictionary<string, int> { ["Doors"] = 1, ["Walls"] = 2 },
                ExpectedDeletedByCategory = new Dictionary<string, int>(),
            };

            var ha = PlanHash.Compute(new[] { a });
            Assert.Equal(ha, PlanHash.Compute(new[] { b }));
            Assert.Matches("^sha256:[0-9a-f]{64}$", ha);
            // Deterministic across calls.
            Assert.Equal(ha, PlanHash.Compute(new[] { Plan() }));
        }

        [Fact]
        public void StableAcrossNumberSpelling()
        {
            var a = Plan(args: "{\"param\":\"Offset\",\"value\":2700}");
            var b = Plan(args: "{\"value\":2700.0,\"param\":\"Offset\"}");
            Assert.Equal(PlanHash.Compute(new[] { a }), PlanHash.Compute(new[] { b }));
        }

        [Fact]
        public void ChangesWhenTargetsChange()
        {
            var baseHash = PlanHash.Compute(new[] { Plan() });
            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { Plan(targets: new long[] { 1, 2, 4 }) }));
            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { Plan(targets: new long[] { 1, 2 }) }));
        }

        [Fact]
        public void ChangesWhenAnythingElseInThePlanChanges()
        {
            var baseHash = PlanHash.Compute(new[] { Plan() });

            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { Plan(args: "{\"param\":\"Mark\",\"value\":\"A2\"}") }));
            Assert.NotEqual(baseHash, PlanHash.Compute(new[]
            {
                Plan(intended: new Dictionary<string, string> { ["1"] = "A1", ["2"] = "A1", ["3"] = "B1" }),
            }));

            var moreAdds = Plan();
            moreAdds.ExpectedAddedByCategory["Walls"] = 3;
            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { moreAdds }));

            var deletes = Plan();
            deletes.ExpectedDeletedByCategory["Walls"] = 1;
            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { deletes }));

            var code = Plan();
            code.CodeText = "Print(1);";
            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { code }));

            var tool = Plan();
            tool.Tool = ToolNames.RunRevitCode;
            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { tool }));

            var estimate = Plan();
            estimate.Previewable = false;
            Assert.NotEqual(baseHash, PlanHash.Compute(new[] { estimate }));
        }

        [Fact]
        public void BatchOrderAndSizeCount()
        {
            var a = Plan();
            var b = Plan(targets: new long[] { 9 });
            var ab = PlanHash.Compute(new[] { a, b });
            Assert.NotEqual(ab, PlanHash.Compute(new[] { b, a }));
            Assert.NotEqual(ab, PlanHash.Compute(new[] { a }));
        }

        [Fact]
        public void ArgsThatAreNotJsonAreHashedAsText()
        {
            var a = Plan(args: "not json");
            var b = Plan(args: "not json!");
            Assert.NotEqual(PlanHash.Compute(new[] { a }), PlanHash.Compute(new[] { b }));
            Assert.Equal(PlanHash.Compute(new[] { a }), PlanHash.Compute(new[] { Plan(args: "not json") }));
        }

        [Fact]
        public void FrozenCopiesKeepTheHashWhenTheOriginalIsMutated()
        {
            var plan = Plan();
            var frozen = PlanHash.Freeze(plan);
            var before = PlanHash.Compute(new[] { frozen });

            // An Execute that mutates its plan (W4) must not move the approved hash.
            plan.TargetIds[0] = 99;
            plan.IntendedValues["1"] = "changed";
            plan.ExpectedAddedByCategory["Walls"] = 7;

            Assert.Equal(before, PlanHash.Compute(new[] { frozen }));
            Assert.NotSame(plan.TargetIds, frozen.TargetIds);
            Assert.Equal(PlanHash.Compute(new[] { Plan() }), before);
        }

        [Fact]
        public void CanonicalTextSortsKeysAndIds()
        {
            var text = PlanHash.CanonicalText(new[] { Plan(targets: new long[] { 3, 1, 2 }) });
            Assert.StartsWith("[{\"args\":{\"param\":\"Mark\",\"target\":{\"ids\":[3,1,2]},\"value\":\"A1\"}", text);
            Assert.Contains("\"expected_added\":{\"Doors\":1,\"Walls\":2}", text);
            Assert.Contains("\"targets\":[1,2,3]", text);
        }
    }
}
