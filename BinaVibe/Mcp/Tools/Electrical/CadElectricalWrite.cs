// place_cad_electrical - the single write after plan_cad_electrical. MUTATE: one
// confirmation card covers the whole write, devices and conduit together.
//
// Devices first, then runs, so a drop finds the device placed a moment ago through the plan
// point's PlacedId. Both halves are the shared bodies of place_socket_points and
// route_conduit's plan mode; each keeps its own TransactionGroup, and both groups are
// started inside one outer group here, so one Ctrl+Z undoes the whole write.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class CadElectricalWrite
    {
        public static Dictionary<string, object?> PlaceCadElectrical(Document doc, JsonElement args)
        {
            var planId = ArgsHelp.GetString(args, "plan_id")
                ?? throw new ArgumentException("missing plan_id");
            // Checks the plan belongs to this document before anything is written.
            var plan = SocketPlanCache.Get(planId, SocketCandidates.DocKey(doc));

            var indices = ArgsHelp.GetLongList(args, "indices");
            var roles = ArgsHelp.GetStringList(args, "roles");
            var runIndices = ArgsHelp.GetLongList(args, "run_indices");
            bool routeRuns = (ArgsHelp.GetBool(args, "include_runs") ?? true) && plan.Runs.Count > 0;

            Dictionary<string, object?> devices;
            Dictionary<string, object?> runs;
            using var group = new TransactionGroup(doc, "BinaVibe: place_cad_electrical");
            group.Start();
            try
            {
                try
                {
                    devices = SocketPlacement.PlacePlanPoints(doc, planId, familyType: null, wanted: indices, roles: roles,
                                                              levelOverride: null, mountOverrideMm: null,
                                                              facingOffsetDeg: 0.0);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    // place_socket_points throws these before or around its own group, which is
                    // rolled back; the runs can still be routed (their drops stay open).
                    devices = new Dictionary<string, object?> { ["ok"] = false, ["plan_id"] = planId, ["error"] = ex.Message };
                }

                runs = routeRuns
                    ? MutatorsMepRouting.RoutePlanRuns(doc, planId, runIndices)
                    : CadElectricalReview.SkippedRuns();

                bool anything = (devices.TryGetValue("count", out var c) && c is int n && n > 0) ||
                                (runs.TryGetValue("runs_created", out var rc) && rc is int m && m > 0);
                if (anything) group.Assimilate();
                else group.RollBack();
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw;
            }

            return CadElectricalReview.WriteResult(planId, devices, runs);
        }
    }
}
