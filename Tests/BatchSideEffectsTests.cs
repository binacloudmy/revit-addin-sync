#nullable enable
// r21 (live L4 H3, 0a1dff76; L3 G7, 69bb5d56; Snowdon HVAC, duct 1382112 resized to 300 x 200 as a Width call then a Height call):
// both calls listed the same 2 Duct Fittings and 1 Air Terminal as modified side effects, and the pane said "adjusted 4 Duct Fittings,
// 2 Air Terminals" while the model said 2 and 1. One batch counts each element once. Revit-free.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class BatchSideEffectsTests
    {
        private const long Duct = 1382112;
        private const long Elbow1 = 1382200, Elbow2 = 1382201, Terminal = 1382300, System_ = 1382400, View = 1382500, Sketch = 1382501;
        private static readonly long[] Elements = { 1382600, 1382601, 1382602, 1382603 };

        /// <summary>The modified side effects of each call in the live diff: Duct Systems 1, Duct Fittings 2, Air Terminals 1, class:View 1,
        /// class:Element 4, class:SketchPlane 1.</summary>
        private static Dictionary<long, string> LiveModified()
        {
            var m = new Dictionary<long, string>
            {
                [Elbow1] = "Duct Fittings", [Elbow2] = "Duct Fittings", [Terminal] = "Air Terminals", [System_] = "Duct Systems",
                [View] = "class:View", [Sketch] = "class:SketchPlane",
            };
            foreach (var id in Elements) m[id] = "class:Element";
            return m;
        }

        private static Dictionary<string, int> Count(Dictionary<long, string> m) => m.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Count());

        private static Envelope Commit(string param, Dictionary<long, string> modifiedSide, Dictionary<string, int>? added = null, Dictionary<string, int>? deleted = null) =>
            new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Modified = new List<FieldChangeDto> { new FieldChangeDto { Id = Duct, Field = param } } },
                    SideEffects = new SideEffectsDto
                    {
                        AddedByCategory = added ?? new Dictionary<string, int>(), ModifiedByCategory = Count(modifiedSide),
                        DeletedByCategory = deleted ?? new Dictionary<string, int>(),
                    },
                    TouchedByCategory = new SideEffectsDto { ModifiedByCategory = new Dictionary<string, int> { ["Ducts"] = 1 } },
                },
            };

        private static void Fold(Envelope[] envs, Dictionary<long, string>[] modified, long[][] added, long[][] deleted)
        {
            var drop = BatchSideEffects.Repeated(Enumerable.Range(0, envs.Length).Select(k => new BatchSideEffects.Call
            {
                ModifiedSide = modified[k], Touched = new[] { Duct }, Added = added[k], Deleted = deleted[k],
            }).ToList());
            for (var k = 0; k < envs.Length; k++) BatchSideEffects.DropModified(envs[k], modified[k], drop[k]);
        }

        [Fact]
        public void LiveH3_TheHeightCallsRepeatOfTheWidthCallsFittingsIsCountedOnce()
        {
            const long Added = 1550710;
            var m0 = LiveModified();
            var m1 = LiveModified();
            var width = Commit("Width", m0);
            var height = Commit("Height", m1, added: new Dictionary<string, int> { ["Duct Fittings"] = 1 });
            Assert.Equal(7, ResultCardModel.RippledBy(new[] { width, height }));   // before: 1 added + 2x(2 + 1)

            Fold(new[] { width, height }, new[] { m0, m1 }, new[] { new long[0], new[] { Added } }, new[] { new long[0], new long[0] });

            Assert.Equal(2, width.Changes!.SideEffects!.ModifiedByCategory["Duct Fittings"]);
            Assert.Equal(1, width.Changes.SideEffects.ModifiedByCategory["Air Terminals"]);
            Assert.Empty(height.Changes!.SideEffects!.ModifiedByCategory);
            Assert.Equal(1, height.Changes.SideEffects.AddedByCategory["Duct Fittings"]);
            Assert.Equal(4, ResultCardModel.RippledBy(new[] { width, height }));   // 1 added, 2 Duct Fittings, 1 Air Terminal: the model's 2 and 1
        }

        [Fact]
        public void LiveG7_AFittingAddedThenReplacedAndTheRepeatedFittings_AreOneAddedTwoAndOneAdjusted()
        {
            // G7 (69bb5d56, before the r19 fold): Width added fitting A; Height deleted A ("Other") and added B; both listed the same run.
            const long A = 1550700, B = 1550710;
            var m0 = LiveModified();
            var m1 = LiveModified();
            var added0 = new Dictionary<long, string> { [A] = "Duct Fittings" };
            var added1 = new Dictionary<long, string> { [B] = "Duct Fittings" };
            var deleted1 = new Dictionary<long, string> { [A] = "Other" };
            var width = Commit("Width", m0, added: new Dictionary<string, int> { ["Duct Fittings"] = 1 });
            var height = Commit("Height", m1, added: new Dictionary<string, int> { ["Duct Fittings"] = 1 }, deleted: new Dictionary<string, int> { ["Other"] = 1 });
            Assert.Equal(8, ResultCardModel.RippledBy(new[] { width, height }));   // the live "added 2 Duct Fittings; adjusted 4 Duct Fittings, 2 Air Terminals"

            Fold(new[] { width, height }, new[] { m0, m1 }, new[] { new[] { A }, new[] { B } }, new[] { new long[0], new[] { A } });
            var gone = BornIds.AddedThenDeleted(new List<(IEnumerable<long>?, IEnumerable<long>?)> { (new[] { A }, new long[0]), (new[] { B }, new[] { A }) });
            BornIds.DropAddedFromEnvelope(width, added0, gone);
            BornIds.DropFromEnvelope(height, new Dictionary<long, string>(), deleted1, gone);
            BornIds.DropAddedFromEnvelope(height, added1, gone);

            Assert.Equal(4, ResultCardModel.RippledBy(new[] { width, height }));   // 1 added ("Revit added one Duct Fitting"), 2 + 1 adjusted
            Assert.Empty(height.Changes!.SideEffects!.ModifiedByCategory);
            Assert.Empty(height.Changes.SideEffects.DeletedByCategory);
        }

        [Fact]
        public void DifferentElementsInEachCallAllCount_AndWrittenAddedOrLaterDeletedOnesAreNotAlsoAModification()
        {
            const long Other = 1382700, Written = 1382800, Born = 1550800, Removed = 1382900;
            var m0 = new Dictionary<long, string> { [Elbow1] = "Duct Fittings", [Written] = "Ducts", [Removed] = "Duct Fittings" };
            var m1 = new Dictionary<long, string> { [Elbow1] = "Duct Fittings", [Other] = "Duct Fittings", [Born] = "Duct Fittings" };
            var drop = BatchSideEffects.Repeated(new List<BatchSideEffects.Call>
            {
                new BatchSideEffects.Call { ModifiedSide = m0, Touched = new[] { Duct }, Added = new[] { Born } },
                new BatchSideEffects.Call { ModifiedSide = m1, Touched = new[] { Duct, Written }, Deleted = new[] { Removed } },
            });
            Assert.Equal(new[] { Written, Removed }, drop[0].OrderBy(i => i));   // written by call 1; deleted by call 1
            Assert.Equal(new[] { Elbow1, Born }, drop[1].OrderBy(i => i));       // counted by call 0; added in the batch
        }

        [Fact]
        public void OneCallOrNothingRepeated_LeavesTheEnvelopeAsItWas()
        {
            var m = LiveModified();
            var env = Commit("Width", m);
            var before = env.Changes!.SideEffects!.ModifiedByCategory;
            var drop = BatchSideEffects.Repeated(new List<BatchSideEffects.Call> { new BatchSideEffects.Call { ModifiedSide = m, Touched = new[] { Duct } } });
            Assert.False(BatchSideEffects.DropModified(env, m, drop[0]));
            Assert.Same(before, env.Changes.SideEffects.ModifiedByCategory);
        }
    }
}
