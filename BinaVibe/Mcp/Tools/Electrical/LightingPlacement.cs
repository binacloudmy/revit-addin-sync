// place_lighting_points — commit the points suggest_lighting_points proposed.
// MUTATE. The read half is LightingCandidates.
//
// Only a plan_id and small integer indices cross the wire, never coordinates:
// the mm the drafter reviewed are the mm placed, with no chance of unit
// slippage in transit. Same contract as place_socket_points.
//
// Every fixture lands in ONE TransactionGroup, so the whole run is a single
// undo — a half-placed grid the drafter has to pick apart by hand is worse
// than a clean failure.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class LightingPlacement
    {
        public static Dictionary<string, object?> PlaceLightingPoints(Document doc, JsonElement args)
        {
            var planId = ArgsHelp.GetString(args, "plan_id")
                ?? throw new ArgumentException("missing plan_id");

            var plan = LightingPlanCache.Get(planId, SocketCandidates.DocKey(doc));
            var familyType = ArgsHelp.GetString(args, "family_type") ?? plan.FamilyType;

            var wanted = ArgsHelp.GetLongList(args, "indices");
            var points = wanted.Count == 0
                ? plan.Points
                : plan.Points.Where(p => wanted.Contains(p.Index)).ToList();

            if (points.Count == 0)
                return ToolResult.Fail($"no candidates selected from plan {planId} " +
                    $"(plan holds {plan.Points.Count} points; indices are 0-based)");

            var symbol = SocketPlacement.ResolveSymbol(doc, familyType)
                ?? throw new ArgumentException($"family type '{familyType}' not found in document");

            var levelOverride = ArgsHelp.GetString(args, "level");
            double? mountOverrideMm = ArgsHelp.GetDouble(args, "mount_height_mm");
            var placement = symbol.Family.FamilyPlacementType;

            // A host-based family with nowhere to host is the failure that reads
            // as "the tools cannot place lights". Say it ONCE, up front, naming
            // the fix — rather than the same exception N times from inside the
            // group, which is what buries it.
            bool hostBased = placement == FamilyPlacementType.OneLevelBasedHosted;
            if (hostBased && points.All(p => p.HostCeilingId == null))
                return ToolResult.Fail(
                    $"'{familyType}' is host-based (placement={placement}) and no ceiling was " +
                    "found over any selected point, so every instance would be placed " +
                    "free-standing — its cutting void intersects nothing and Revit rejects " +
                    "the commit. Either model the ceiling first, or re-run " +
                    "suggest_lighting_points and place an unhosted or face-based lighting " +
                    "type. This is a family/model choice, not a case for hand-written code.");

            var created = new List<object>();
            var failed = new List<object>();

            TxGuard.ForEachInGroup(doc, "BinaVibe: place_lighting_points", points,
                p => created.Add(PlaceOne(doc, symbol, hostBased, p, levelOverride, mountOverrideMm)),
                (p, ex) => failed.Add(new Dictionary<string, object?>
                {
                    ["index"] = p.Index,
                    ["room_id"] = p.RoomId,
                    ["reason"] = ex.Message,
                }));

            int hosted = created.OfType<Dictionary<string, object?>>()
                .Count(r => (r.TryGetValue("host", out var h) ? h as string : null) == "ceiling");

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["family_type"] = familyType,
                ["count"] = created.Count,
                ["created"] = created,
                ["failed"] = failed,
                ["hosted_count"] = hosted,
                ["unhosted_count"] = created.Count - hosted,
                ["created_ids"] = created.OfType<Dictionary<string, object?>>()
                    .Select(r => r.TryGetValue("created_id", out var id) ? id : null)
                    .Where(id => id != null).ToList(),
                // The next step, named here because the drafter's ask rarely
                // stops at "placed": these ids go straight to suggest_circuits.
                ["next"] = "suggest_circuits(device_ids=[created_ids], " +
                           "categories=[\"lighting_fixtures\"]) to circuit them",
            };
        }

        private static Dictionary<string, object?> PlaceOne(
            Document doc, FamilySymbol symbol, bool hostBased, PlannedLight p,
            string? levelOverride, double? mountOverrideMm)
        {
            double mountMm = mountOverrideMm ?? p.MountHeightMm;
            // ZMm was computed as floor + the plan's mount height; swapping the
            // mount height has to swap that component, not add to it.
            double zMm = p.ZMm - p.MountHeightMm + mountMm;
            var pt = new XYZ(p.XMm / MmPerFoot, p.YMm / MmPerFoot, zMm / MmPerFoot);

            var ceiling = p.HostCeilingId.HasValue
                ? doc.GetElement(ElemIds.From(p.HostCeilingId.Value))
                : null;

            if (hostBased && ceiling == null)
                // Never fall through to the unhosted overload: a hosted family
                // placed free-standing has a cutting void that intersects
                // nothing, and Revit rejects the commit outright.
                throw new InvalidOperationException(
                    $"candidate {p.Index} has no ceiling to host on but '{symbol.Name}' is " +
                    "host-based — model a ceiling here, or use an unhosted or face-based " +
                    "lighting family for these points.");

            using var tx = new Transaction(doc, "BinaVibe: place lighting fixture");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                var level = SocketPlacement.ResolveLevel(doc, levelOverride ?? p.LevelName, ceiling);
                FamilyInstance fi;

                if (ceiling != null && (hostBased || PlacementNeedsHost(symbol)))
                {
                    var hostLevel = doc.GetElement(ceiling.LevelId) as Level ?? level;
                    fi = doc.Create.NewFamilyInstance(pt, symbol, ceiling, hostLevel,
                                                      StructuralType.NonStructural);
                }
                else
                {
                    fi = level != null
                        ? doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.NonStructural)
                        : doc.Create.NewFamilyInstance(pt, symbol, StructuralType.NonStructural);
                }

                TxGuard.CommitOrThrow(tx);

                // Re-read rather than trust the request: a hosted instance is
                // constrained to its host, so the Z asked for is not always the
                // Z achieved, and reporting the request would hide that.
                double actualZMm = zMm;
                bool zKnown = false;
                try
                {
                    if (fi.Location is LocationPoint lp)
                    {
                        actualZMm = lp.Point.Z * MmPerFoot;
                        zKnown = true;
                    }
                }
                catch { zKnown = false; }

                return new Dictionary<string, object?>
                {
                    ["index"] = p.Index,
                    ["created_id"] = fi.Id.Value,
                    ["room_id"] = p.RoomId,
                    ["room_name"] = p.RoomName,
                    ["host"] = ceiling != null ? "ceiling" : "unhosted",
                    ["host_id"] = ceiling?.Id.Value,
                    ["xyz_mm"] = new List<object>
                    {
                        Math.Round(p.XMm, 1), Math.Round(p.YMm, 1), Math.Round(actualZMm, 1),
                    },
                    ["z_requested_mm"] = Math.Round(zMm, 1),
                    ["z_verified"] = zKnown,
                };
            }
            // CommitOrThrow throws AFTER Revit has already rolled back, so only
            // roll back here for a failure mid-build (tx still Started).
            catch { TxGuard.SafeRollBack(tx); throw; }
        }

        /// <summary>Work-plane-based families also want a host when one exists —
        /// same rule place_socket_points applies to its wall.</summary>
        private static bool PlacementNeedsHost(FamilySymbol symbol)
        {
            try { return symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased; }
            catch { return false; }
        }
    }
}
