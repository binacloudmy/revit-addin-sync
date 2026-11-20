#nullable enable
// Socket plan cache — pure, Revit-free, MILLIMETRES ONLY.
//
// One SocketPlan per suggest_socket_points call, cached under
// "socket_plan:<guid>" so place_socket_points places the EXACT points the
// drafter reviewed. Coordinates never travel back through the model: the
// confirmation carries a plan_id plus small integer indices, so there is no
// opportunity for silent coordinate corruption or mm/ft slippage in transit.
//
// Modelled on AuditResultCache (Audit/AuditModels.cs) with two deliberate
// differences:
//   1. Entries carry a document key and Get() rejects a mismatch. Serving
//      coordinates computed against a different model is actively dangerous;
//      an audit record is merely wrong.
//   2. CloseAll() is actually wired up (App.cs). AuditResultCache.CloseAll is
//      dead code — never called — and that leak should not be inherited.
//
// plan_cad_electrical stores its plans here too (Source "cad"): each point then carries its
// role, block class, family type and flags, and the plan its drawing alignment. Every field
// it added defaults to what a suggest_socket_points plan means, so those plans read as before.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One reviewable point, already resolved to absolute
    /// project-internal coordinates in mm.</summary>
    public sealed class PlannedPoint
    {
        /// <summary>Stable 0-based index — what the drafter confirms with.</summary>
        public int Index;
        public long RoomId;
        public string RoomName = "";
        public string LevelName = "";
        /// <summary>Local wall to host on; null when the boundary came from a
        /// Revit link and there is nothing local to host against.</summary>
        public long? HostWallId;
        /// <summary>Local ceiling to host on, for a ceiling device; null otherwise.</summary>
        public long? HostCeilingId;
        /// <summary>"wall" | "ceiling" | "unhosted"</summary>
        public string Host = "unhosted";
        public double XMm;
        public double YMm;
        /// <summary>Absolute project-internal Z, i.e. level elevation + room
        /// base offset + mount height.</summary>
        public double ZMm;
        /// <summary>Mount height above the room's finished floor. Carried
        /// separately from ZMm because hosted placement may have to set it as a
        /// parameter rather than through the insertion point.</summary>
        public double MountHeightMm;
        /// <summary>Unit vector pointing into the room.</summary>
        public double FacingDx;
        public double FacingDy;
        public double StationMm;
        public double WallLengthMm;
        public int LoopIndex;

        /// <summary>The mapping's role ("socket", "switch", ...); empty on a rule plan,
        /// whose points are all sockets.</summary>
        public string Role = "";
        /// <summary>The drawing's block class key; empty on a rule plan.</summary>
        public string SymbolKey = "";
        /// <summary>"Family : Type" from the mapping; null when the caller names it.</summary>
        public string? FamilyType;
        /// <summary>Review flags (no_room, wet_room, already_present, ...).</summary>
        public List<string> Flags = new();
        /// <summary>"rule" (suggest_socket_points) | "cad" (plan_cad_electrical).</summary>
        public string Source = "rule";
        /// <summary>Left out of a default placement because of a flag; explicit indices
        /// still place it.</summary>
        public bool Excluded;
        /// <summary>The device place_socket_points made for this point; null until then. A
        /// conduit run's drop connects to it.</summary>
        public long? PlacedId;
        /// <summary>Diagnostic only, on a drawing plan: where this point would go from each
        /// candidate anchor of its drawn symbol (centre first). Null on a rule plan.</summary>
        public List<PlanAnchor>? Anchors;
    }

    /// <summary>
    /// One candidate anchor of a drawn symbol (centre, insert, wall_edge_mid, bbox_min,
    /// bbox_max), carried to the placement point the resolver would make of it: same wall,
    /// side, face snap and Z as the point itself. Never used to place.
    /// </summary>
    public sealed class PlanAnchor
    {
        public string Name = "";
        /// <summary>False when the candidate does not apply (a far insert point, no box, no
        /// host wall for wall_edge_mid): X, Y, Z mean nothing.</summary>
        public bool Known;
        public double XMm, YMm, ZMm;
        /// <summary>Signed, along the host wall from the centre anchor's point; null with no
        /// host wall.</summary>
        public double? AlongOffsetMm;
    }

    /// <summary>Where a drawing was found to sit on the model, for a drawing plan.</summary>
    public sealed class PlanAlignment
    {
        public string Level = "";
        public int Cluster;
        public double RotationDeg;
        public double TxMm;
        public double TyMm;
        /// <summary>Share of the drawing's wall inside the level's footprint lying on a model wall.</summary>
        public double Score;
        /// <summary>The same share of all the cluster's drawing wall.</summary>
        public double ScoreUncropped;
        /// <summary>Share of the cluster's drawing wall inside the level's footprint.</summary>
        public double InsideShare;
        /// <summary>Share of the level's model wall lying on a drawing wall.</summary>
        public double ModelCoverage;
        /// <summary>Which of several equal placements the caller picked; -1 when there was one.</summary>
        public int Pick = -1;
        /// <summary>"accepted" | "provisional"</summary>
        public string Outcome = "";
        /// <summary>"fit" | "fit_faces" | "linked_instance"</summary>
        public string Method = "";
        /// <summary>The CAD link or import whose position placed the drawing (method
        /// linked_instance); null otherwise.</summary>
        public long? LinkId;
    }

    /// <summary>A conduit or cable tray run of a drawing plan (CadRunPlan), in model mm.</summary>
    public sealed class PlannedRun
    {
        /// <summary>Stable 0-based index across the plan's runs: what route_conduit selects by.</summary>
        public int Index;
        public string Layer = "";
        /// <summary>"conduit" | "cable_tray"</summary>
        public string Kind = "conduit";
        /// <summary>"Family : Type" of the conduit or cable tray type.</summary>
        public string? TypeName;
        public double? DiameterMm, WidthMm, HeightMm;
        public string LevelName = "";
        /// <summary>Height of the horizontal run above the level.</summary>
        public double RunHeightMm;
        /// <summary>x, y, z in project-internal mm, drops included, start to end.</summary>
        public List<double[]> PointsMm = new();
        public RunEnd Start = new(), End = new();
        public double LengthMm;
        /// <summary>Vertical drops to devices this run has (0 to 2).</summary>
        public int Drops;
        /// <summary>open_end, junction, shares_drop, short_leg, drop_too_short, device_ambiguous,
        /// provisional_alignment, closed_loop.</summary>
        public List<string> Flags = new();
        /// <summary>Left out of a default route_conduit because of a flag; explicit indices still route it.</summary>
        public bool Excluded;
        public string Source = "cad";
        /// <summary>The elements route_conduit made for this run (segments and fittings).</summary>
        public List<long> CreatedIds = new();
    }

    /// <summary>One end of a planned run.</summary>
    public sealed class RunEnd
    {
        /// <summary>"open" | "device" | "junction" | "shares_drop"</summary>
        public string Kind = "open";
        /// <summary>The plan point the end drops to, when it is a planned device.</summary>
        public int? PointIndex;
        /// <summary>The model device the end drops to, when it is an existing one.</summary>
        public long? ElementId;
        /// <summary>Run height minus the device's height: positive drops down, 0 with no drop.</summary>
        public double DropMm;
    }

    /// <summary>Everything one suggest_socket_points or plan_cad_electrical run produced.</summary>
    public sealed class SocketPlan
    {
        public string PlanId = "";
        /// <summary>doc.PathName (or doc.Title for an unsaved model). Guards
        /// against replaying a plan into the wrong document.</summary>
        public string DocKey = "";
        public DateTime CreatedUtc;
        public List<PlannedPoint> Points = new();
        /// <summary>The effective rule values this plan was built with, echoed
        /// back on the wire so any answer is auditable.</summary>
        public Dictionary<string, object?> ParamsUsed = new();
        /// <summary>"rule" | "cad"</summary>
        public string Source = "rule";
        /// <summary>The drawing a "cad" plan was read from; empty on a rule plan.</summary>
        public string SourceFile = "";
        public PlanAlignment? Alignment;
        public List<PlannedRun> Runs = new();
    }

    public static class SocketPlanCache
    {
        private sealed class Entry
        {
            public SocketPlan Plan = new();
            public DateTime LastUsed;
        }

        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

        public static string Store(SocketPlan plan, string docKey)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            Sweep();
            var id = "socket_plan:" + Guid.NewGuid().ToString("N");
            plan.PlanId = id;
            plan.DocKey = docKey ?? "";
            plan.CreatedUtc = DateTime.UtcNow;
            _entries[id] = new Entry { Plan = plan, LastUsed = DateTime.UtcNow };
            return id;
        }

        /// <summary>Retrieve a plan for the document it was built against.
        /// Throws — with drafter-readable guidance — on an unknown/expired id
        /// or a document mismatch.</summary>
        public static SocketPlan Get(string planId, string docKey)
        {
            if (string.IsNullOrWhiteSpace(planId) || !_entries.TryGetValue(planId, out var e))
                throw new InvalidOperationException(
                    "unknown plan_id " + planId +
                    " — run suggest_socket_points again (plans expire after 2 hours)" +
                    "; for a drawing plan, run plan_cad_electrical again");

            if (!string.Equals(e.Plan.DocKey, docKey ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "plan_id " + planId + " was generated for a different model (" +
                    (string.IsNullOrEmpty(e.Plan.DocKey) ? "<unsaved>" : e.Plan.DocKey) +
                    ") — run suggest_socket_points again in this model");

            e.LastUsed = DateTime.UtcNow;
            return e.Plan;
        }

        public static void CloseAll() => _entries.Clear();

        private static void Sweep()
        {
            var stale = new List<string>();
            foreach (var kv in _entries)
                if (DateTime.UtcNow - kv.Value.LastUsed > Ttl) stale.Add(kv.Key);
            foreach (var key in stale) _entries.Remove(key);
        }
    }
}
