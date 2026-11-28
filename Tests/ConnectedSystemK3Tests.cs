// r22, live K3 4e3aa38e: the forced resize of duct 1382112 was refused because its own Duct System 1365689 changed and was not in the
// connected set. ConnectedSet now reads the systems the targets and their connected elements belong to (Revit-bound); this pins what the
// judgement does with them. Revit-free.
using System.Collections.Generic;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ConnectedSystemK3Tests
    {
        private const long Duct = 1382112, Fitting = 1382156, OwnSystem = 1365689, OtherSystem = 1365700;

        private static BatchShape Shape(params long[] modified)
        {
            var s = new BatchShape();
            s.Modified.UnionWith(modified);
            s.Touched.Add(Duct);
            return s;
        }

        [Fact]
        public void TheDuctsOwnSystem_IsConnected_AndTheForcedAuditPasses()
        {
            var before = new Dictionary<long, string> { [Fitting] = "Duct Fittings", [OwnSystem] = "Duct Systems" };
            var commit = Shape(Duct, Fitting, OwnSystem);
            var verdict = ShapeComparer.JudgeConnected(Shape(Duct), commit, before, new Dictionary<long, string>(), new[] { Duct });
            Assert.True(verdict.ConnectedOnly, verdict.Outside);
            Assert.Null(ShapeComparer.ForcedOutside(commit, new HashSet<long> { Duct, Fitting, OwnSystem }));
        }

        [Fact]
        public void AnotherSystem_IsStillOutside()
        {
            var before = new Dictionary<long, string> { [Fitting] = "Duct Fittings", [OwnSystem] = "Duct Systems" };
            var commit = Shape(Duct, OtherSystem);
            var verdict = ShapeComparer.JudgeConnected(Shape(Duct), commit, before, new Dictionary<long, string>(), new[] { Duct });
            Assert.False(verdict.ConnectedOnly);
            Assert.Equal(OtherSystem, ShapeComparer.ForcedOutside(commit, new HashSet<long> { Duct, Fitting, OwnSystem }));
        }
    }
}
