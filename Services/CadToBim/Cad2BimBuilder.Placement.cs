// The Revit-free half of Cad2BimBuilder: where each plan goes and what the undo step is called.
// Linked into the Tests project (AI-642), so "KeepPosition never asks for a new level" is tested.

using System.Collections.Generic;
using Cad2Bim;

using CadWall = Cad2Bim.Wall;

namespace RevitWebAppSync.Services.CadToBim
{
    public static partial class Cad2BimBuilder
    {
        public const string DefaultGroupName = "CAD to BIM";

        /// <summary>The TransactionGroup's name, i.e. the one undo step: legacy keeps "CAD to BIM".</summary>
        internal static string GroupNameFor(BuildRequest req) =>
            string.IsNullOrWhiteSpace(req.GroupName) ? DefaultGroupName : req.GroupName;

        /// <summary>Legacy only: a level id that no longer resolves falls back to the lowest level.</summary>
        internal static bool MayFallBackToLowestLevel(BuildRequest req) => !req.RequireLevel;

        /// <summary>One plan cluster and where it goes: the shift that brings its corner to
        /// the origin (zero in KeepPosition mode) and which storey it is.</summary>
        internal sealed class Placement
        {
            public PlanCluster Plan;
            public double OriginX;
            public double OriginY;
            public int LevelIndex;
        }

        /// <summary>
        /// One storey per floor plan. Read literally the sheet is a single level 350 metres
        /// across with every floor lying flat beside the others - a carpet, not a building.
        /// Each plan gets its own level, and each is shifted so its own corner meets the
        /// origin, which stacks them the way the building actually is.
        ///
        /// Two things a converted model gets used for, and they want opposite placement. To
        /// keep as a model, each plan belongs at the origin on its own level. To check against
        /// the drawing it came from, it has to land exactly on top of the linked CAD - same
        /// coordinates, same layout, no stacking - or the two cannot be compared at all.
        /// </summary>
        internal static List<Placement> ClusterAndPlace(BuildRequest req)
        {
            var placements = new List<Placement>();

            // The full layout, not just this Confirm's pending subset - otherwise a second
            // Confirm re-clusters from the pending walls' own bounding box and every appended
            // wall lands shifted toward the model origin instead of where the first build put it.
            List<CadWall> layout = req.LayoutWalls ?? req.Walls;

            if (req.Storeys == StoreyMode.KeepPosition)
            {
                // One cluster covering everything, placed where the drawing has it.
                var whole = new PlanCluster();
                foreach (CadWall wall in layout) whole.Add(wall);
                whole.MinX = 0;
                whole.MinY = 0;
                placements.Add(new Placement { Plan = whole, OriginX = 0, OriginY = 0, LevelIndex = 0 });
                return placements;
            }

            List<PlanCluster> plans = CadClassifier.ClusterPlans(layout);
            for (int i = 0; i < plans.Count; i++)
            {
                placements.Add(new Placement
                {
                    Plan = plans[i],
                    OriginX = plans[i].MinX,
                    OriginY = plans[i].MinY,
                    LevelIndex = i,
                });
            }

            return placements;
        }

    }
}
