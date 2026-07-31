// place_socket_points / place_socket_on_wall — the write half of the socket
// workflow. MUTATE tools: the addin's ConfirmGate shows a Ya/Tidak card before
// either runs.
//
// place_socket_points takes a plan_id, never coordinates. The candidate points
// are read back out of SocketPlanCache, so the drafter's confirmation and the
// geometry that gets committed cannot drift apart — an LLM re-emitting 40 XYZ
// triples is exactly the transport hole this closes.
//
// Deliberately NOT routed through execute_revit_batch: BatchExecutor rolls the
// whole group back on any step failure, and one wall refusing a host must not
// destroy the other thirty-nine placements. A TransactionGroup here gives the
// same single Ctrl+Z with per-item tolerance.
//
// place_family_instance (Mutators.cs:540) is untouched. It refuses
// OneLevelBasedHosted families outright; the reasoning is reused below, the
// code is not.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class SocketPlacement
    {
        private const double MmPerFoot = 304.8;
        /// <summary>Mounting height is considered honoured within 1 mm.</summary>
        private const double ZTolFt = 1.0 / MmPerFoot;
        /// <summary>A faceplate this far off reads as wrong to a drafter.
        /// Not tunable from the wire — this is a quality bar, not a rule.</summary>
        private const double FacingTolDeg = 5.0;
        /// <summary>Insertion point drift this large means the rotation moved
        /// the instance as well as turning it, and has to be undone.</summary>
        private const double MoveTolFt = 0.5 / MmPerFoot;

        // ─── place_socket_points ────────────────────────────────────────
        public static Dictionary<string, object?> PlaceSocketPoints(Document doc, JsonElement args)
        {
            var planId = ArgsHelp.GetString(args, "plan_id")
                ?? throw new ArgumentException("missing plan_id");
            var familyType = ArgsHelp.GetString(args, "family_type")
                ?? throw new ArgumentException("missing family_type");

            var plan = SocketPlanCache.Get(planId, SocketCandidates.DocKey(doc));

            var wanted = ArgsHelp.GetLongList(args, "indices");
            var points = wanted.Count == 0
                ? plan.Points
                : plan.Points.Where(p => wanted.Contains(p.Index)).ToList();

            if (points.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"no candidates selected from plan {planId} " +
                                $"(plan holds {plan.Points.Count} points; indices are 0-based)",
                };

            var symbol = ResolveSymbol(doc, familyType)
                ?? throw new ArgumentException($"family type '{familyType}' not found in document");

            var levelOverride = ArgsHelp.GetString(args, "level");
            double? mountOverrideMm = ArgsHelp.GetDouble(args, "mount_height_mm");
            double facingOffsetDeg = ArgsHelp.GetDouble(args, "facing_offset_deg") ?? 0.0;

            var created = new List<object>();
            var failed = new List<object>();
            var placement = symbol.Family.FamilyPlacementType;

            using var group = new TransactionGroup(doc, "BinaVibe: place_socket_points");
            group.Start();
            try
            {
                foreach (var p in points)
                {
                    try
                    {
                        var row = PlaceOne(doc, symbol, placement, p, levelOverride,
                                           mountOverrideMm, facingOffsetDeg);
                        created.Add(row);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new Dictionary<string, object?>
                        {
                            ["index"] = p.Index,
                            ["room_id"] = p.RoomId,
                            ["reason"] = ex.Message,
                        });
                    }
                }
                // Assimilate: N sockets collapse into one undo step.
                group.Assimilate();
            }
            catch { group.RollBack(); throw; }

            // A run of sideways sockets must not read as a clean success.
            var uncorrected = created
                .OfType<Dictionary<string, object?>>()
                .Where(r => (r.TryGetValue("facing_method", out var m) ? m as string : null) == "uncorrectable")
                .Select(r => r.TryGetValue("index", out var i) ? i : null)
                .ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["family_type"] = familyType,
                ["facing_offset_deg"] = facingOffsetDeg,
                ["count"] = created.Count,
                ["created"] = created,
                ["failed"] = failed,
                ["facing_uncorrected"] = uncorrected.Count,
                ["facing_uncorrected_indices"] = uncorrected,
            };
        }

        private static Dictionary<string, object?> PlaceOne(
            Document doc, FamilySymbol symbol, FamilyPlacementType placement,
            PlannedPoint p, string? levelOverride, double? mountOverrideMm,
            double facingOffsetDeg)
        {
            double mountMm = mountOverrideMm ?? p.MountHeightMm;
            // ZMm was computed as floor + the plan's mount height; swapping the
            // mount height has to swap that component, not add to it.
            double zMm = p.ZMm - p.MountHeightMm + mountMm;
            var pt = new XYZ(p.XMm / MmPerFoot, p.YMm / MmPerFoot, zMm / MmPerFoot);

            // facing_offset_deg describes the FAMILY (a front axis authored the
            // wrong way round), not the room, so it is applied to the target
            // before any correction is attempted.
            SocketLayout.ApplyOffsetDeg(p.FacingDx, p.FacingDy, facingOffsetDeg,
                                        out double targetDx, out double targetDy);
            var facing = new XYZ(targetDx, targetDy, 0);

            bool hosted = placement == FamilyPlacementType.OneLevelBasedHosted;
            Wall? host = p.HostWallId.HasValue
                ? doc.GetElement(ElemIds.From(p.HostWallId.Value)) as Wall
                : null;

            if (hosted && host == null)
                // Never fall through to the unhosted overload: a hosted family
                // placed free-standing has a cutting void that intersects
                // nothing, and Revit rejects the commit outright. Same trap
                // place_family_instance guards at Mutators.cs:572.
                throw new InvalidOperationException(
                    $"candidate {p.Index} has no local host wall (host={p.Host}) but " +
                    $"'{symbol.Name}' is host-based — the bounding wall lives in a Revit " +
                    "link. Use an unhosted or face-based socket family for these points.");

            using var tx = new Transaction(doc, "BinaVibe: place socket");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                var level = ResolveLevel(doc, levelOverride ?? p.LevelName, host);
                FamilyInstance fi;

                if (host != null && (hosted || placement == FamilyPlacementType.WorkPlaneBased))
                {
                    var hostLevel = doc.GetElement(host.LevelId) as Level ?? level;
                    fi = doc.Create.NewFamilyInstance(pt, symbol, host, hostLevel, StructuralType.NonStructural);
                }
                else
                {
                    fi = level != null
                        ? doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.NonStructural)
                        : doc.Create.NewFamilyInstance(pt, symbol, StructuralType.NonStructural);
                }

                // Hosted and unhosted alike. The old code only ever flipped a
                // hosted instance 180 degrees, so a family whose front axis is
                // authored 90 degrees off scored a dot product of ~0, no
                // correction fired, and the socket shipped parallel to the wall.
                var orient = OrientToFace(doc, fi, facing);

                var elevationVia = EnsureMountHeight(doc, fi, pt.Z, mountMm);
                TxGuard.CommitOrThrow(tx);

                return new Dictionary<string, object?>
                {
                    ["index"] = p.Index,
                    ["room_id"] = p.RoomId,
                    ["created_id"] = fi.Id.Value,
                    ["host_wall_id"] = p.HostWallId,
                    ["host"] = host != null ? "wall" : "unhosted",
                    ["flipped"] = orient.Method == "flip",
                    ["facing"] = new List<object> { Math.Round(orient.Dx, 4), Math.Round(orient.Dy, 4) },
                    ["facing_target"] = new List<object> { Math.Round(targetDx, 4), Math.Round(targetDy, 4) },
                    ["facing_error_deg"] = Math.Round(orient.ErrorDeg, 2),
                    ["facing_method"] = orient.Method,
                    // Geometry, not the nominal axis — the only field that can
                    // catch a family authored back-to-front.
                    ["visible_profile"] = VisibleProfile(fi, host, facing),
                    ["elevation_set_via"] = elevationVia,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── place_socket_on_wall ───────────────────────────────────────
        // Sibling of place_door / place_window for the ad-hoc single socket.
        // Callable from inside execute_revit_batch.
        //
        // `facing` is optional and derived from the bounding room when omitted
        // (SocketCandidates.TryFacingAt) — the caller has no way to compute an
        // inward normal, so requiring it in practice meant nobody passed it and
        // the socket shipped uncorrected. Pass derive_facing=false for the old
        // place-and-leave-it behaviour.
        public static Dictionary<string, object?> PlaceSocketOnWall(Document doc, JsonElement args)
        {
            var hostId = ArgsHelp.GetLong(args, "host_wall_id")
                ?? throw new ArgumentException("missing host_wall_id");
            var typeName = ArgsHelp.GetString(args, "type_name")
                ?? throw new ArgumentException("missing type_name");
            var loc = ArgsHelp.GetPointMm(args, "location_mm")
                ?? throw new ArgumentException("missing location_mm [x,y,z]");

            var host = doc.GetElement(ElemIds.From(hostId)) as Wall
                ?? throw new ArgumentException($"host wall {hostId} not found");

            var symbol = ResolveSymbol(doc, typeName, BuiltInCategory.OST_ElectricalFixtures)
                ?? throw new ArgumentException(
                    $"electrical fixture type '{typeName}' not found in document");

            var facingArgs = ArgsHelp.GetXyz(args, "facing");
            double? mountMm = ArgsHelp.GetDouble(args, "mount_height_mm");
            double facingOffsetDeg = ArgsHelp.GetDouble(args, "facing_offset_deg") ?? 0.0;
            bool deriveFacing = ArgsHelp.GetBool(args, "derive_facing") ?? true;

            // Resolve the target direction BEFORE the transaction — the
            // derivation is strictly read-only, and keeping it out here means a
            // room walk can never interact with TxGuard or regeneration order.
            //
            // An explicit facing is caller intent and always wins; derivation is
            // a fallback, never an override. Omitting it used to mean no
            // correction at all (Measure(fi, null) -> "unknown"), which is how
            // ad-hoc sockets shipped parallel to their wall.
            double? targetDx = null, targetDy = null;
            string facingSource = "none";
            string? deriveError = null;
            long? facingRoomId = null;

            if (facingArgs != null)
            {
                targetDx = facingArgs.X; targetDy = facingArgs.Y;
                facingSource = "arg";
            }
            else if (!deriveFacing)
            {
                deriveError = "disabled";
            }
            else if (SocketCandidates.TryFacingAt(doc, host, loc,
                                                  out double ndx, out double ndy,
                                                  out long nRoomId, out string nReason))
            {
                targetDx = ndx; targetDy = ndy;
                facingSource = "derived_room_normal";
                facingRoomId = nRoomId;
            }
            else
            {
                deriveError = nReason;
            }

            using var tx = new Transaction(doc, "BinaVibe: place_socket_on_wall");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var hostLevel = doc.GetElement(host.LevelId) as Level
                    ?? throw new InvalidOperationException("host wall has no level");

                var fi = doc.Create.NewFamilyInstance(loc, symbol, host, hostLevel,
                    StructuralType.NonStructural);

                // Same general-angle correction as place_socket_points — a
                // 180-degree-only flip leaves a 90-degree family error uncorrected.
                //
                // facing_offset_deg describes the FAMILY, so it applies to a
                // derived normal exactly as it applies to a supplied one — same
                // reasoning as PlaceOne above.
                Orientation orient;
                double? aimedDx = null, aimedDy = null;
                if (targetDx.HasValue)
                {
                    SocketLayout.ApplyOffsetDeg(targetDx.Value, targetDy!.Value, facingOffsetDeg,
                                                out double tdx, out double tdy);
                    aimedDx = tdx; aimedDy = tdy;
                    orient = OrientToFace(doc, fi, new XYZ(tdx, tdy, 0));
                }
                else
                {
                    doc.Regenerate();
                    orient = Measure(fi, null);
                }

                string elevationVia = "insertion_point";
                if (mountMm.HasValue)
                    elevationVia = EnsureMountHeight(doc, fi, loc.Z, mountMm.Value);

                TxGuard.CommitOrThrow(tx);
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = fi.Id.Value,
                    ["host_wall_id"] = hostId,
                    ["flipped"] = orient.Method == "flip",
                    ["facing"] = new List<object> { Math.Round(orient.Dx, 4), Math.Round(orient.Dy, 4) },
                    // Null rather than 0.0 when nothing was aimed at: an error of
                    // zero against no target reads as "perfect", which is the
                    // bluff this whole change exists to remove.
                    ["facing_error_deg"] = aimedDx.HasValue ? Math.Round(orient.ErrorDeg, 2) : (object?)null,
                    ["facing_method"] = orient.Method,
                    ["facing_source"] = facingSource,
                    ["facing_target"] = aimedDx.HasValue
                        ? new List<object> { Math.Round(aimedDx.Value, 4), Math.Round(aimedDy!.Value, 4) }
                        : null,
                    ["facing_room_id"] = facingRoomId,
                    ["facing_derivation_error"] = deriveError,
                    ["visible_profile"] = VisibleProfile(
                        fi, host, aimedDx.HasValue ? new XYZ(aimedDx.Value, aimedDy!.Value, 0) : null),
                    ["facing_offset_deg"] = facingOffsetDeg,
                    ["elevation_set_via"] = elevationVia,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── helpers ────────────────────────────────────────────────────

        private static FamilySymbol? ResolveSymbol(Document doc, string name, BuiltInCategory? cat = null)
        {
            var q = new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol));
            if (cat.HasValue) q = q.OfCategory(cat.Value);

            return q.Cast<FamilySymbol>().FirstOrDefault(fs =>
                string.Equals(fs.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals($"{fs.FamilyName} : {fs.Name}", name, StringComparison.OrdinalIgnoreCase));
        }

        private static Level? ResolveLevel(Document doc, string? levelName, Wall? host)
        {
            if (!string.IsNullOrEmpty(levelName))
            {
                var byName = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (byName != null) return byName;
            }
            return host != null ? doc.GetElement(host.LevelId) as Level : null;
        }

        /// <summary>What an instance ended up pointing at, and how it got there.
        /// Method is one of as_placed | flip | rotate | uncorrectable | unknown.</summary>
        private readonly struct Orientation
        {
            public readonly double Dx, Dy, ErrorDeg;
            public readonly string Method;
            public Orientation(double dx, double dy, double errorDeg, string method)
            { Dx = dx; Dy = dy; ErrorDeg = errorDeg; Method = method; }
        }

        /// <summary>Turn a freshly placed instance to face into the room, and
        /// report what actually happened.
        ///
        /// Escalates: as placed -> 180-degree flip -> general-angle rotate ->
        /// give up and SAY SO. The old code stopped at the flip, which only ever
        /// fixes a family that is exactly backwards; a family whose front axis is
        /// authored 90 degrees off scores a dot product near zero, so nothing
        /// fired and the socket shipped parallel to the wall.
        ///
        /// A wall-hosted instance is constrained to its host, so Revit may refuse
        /// or ignore the rotation. That is an expected outcome, not an error —
        /// the instance is still placed and comes back "uncorrectable" with a
        /// measured angle, which the drafter can feed straight back in as
        /// facing_offset_deg.</summary>
        private static Orientation OrientToFace(Document doc, FamilyInstance fi, XYZ target)
        {
            doc.Regenerate();
            var state = Measure(fi, target);
            if (state.Method != "measured") return state;      // degenerate; nothing to correct
            if (state.ErrorDeg <= FacingTolDeg)
                return new Orientation(state.Dx, state.Dy, state.ErrorDeg, "as_placed");

            // Exactly backwards — the cheap, host-safe correction.
            if (state.ErrorDeg >= 180.0 - FacingTolDeg && fi.CanFlipFacing)
            {
                try
                {
                    fi.flipFacing();
                    doc.Regenerate();
                    var after = Measure(fi, target);
                    if (after.ErrorDeg <= FacingTolDeg)
                        return new Orientation(after.Dx, after.Dy, after.ErrorDeg, "flip");
                    state = after;
                }
                catch { /* fall through to the rotate attempt */ }
            }

            // General angle. Pivot on the CURRENT insertion point, re-read after
            // placement: a family's insertion point can sit well off its visible
            // geometry (Mutators.cs:273), so spinning about a stale point would
            // translate the faceplate off the wall face as well as turn it.
            var before = (fi.Location as LocationPoint)?.Point;
            if (before != null)
            {
                double angle = SocketLayout.SignedAngleDeg(state.Dx, state.Dy, target.X, target.Y)
                               * Math.PI / 180.0;
                if (Math.Abs(angle) > 1e-6)
                {
                    try
                    {
                        var axis = Line.CreateBound(before, before + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, fi.Id, axis, angle);
                        doc.Regenerate();

                        // Undo any drift the rotation introduced.
                        var after = (fi.Location as LocationPoint)?.Point;
                        if (after != null && after.DistanceTo(before) > MoveTolFt)
                        {
                            ElementTransformUtils.MoveElement(doc, fi.Id, before - after);
                            doc.Regenerate();
                        }

                        var rotated = Measure(fi, target);
                        if (rotated.ErrorDeg <= FacingTolDeg)
                            return new Orientation(rotated.Dx, rotated.Dy, rotated.ErrorDeg, "rotate");
                        state = rotated;
                    }
                    catch
                    {
                        // Hosted instances routinely refuse this. Keep the socket.
                        state = Measure(fi, target);
                    }
                }
            }

            return new Orientation(state.Dx, state.Dy, state.ErrorDeg, "uncorrectable");
        }

        /// <summary>Current plan facing plus the error against a target.
        /// Method comes back "measured" when both vectors are usable, else
        /// "unknown" — a family with a vertical facing axis has no plan
        /// direction to correct and must not be silently reported as correct.</summary>
        private static Orientation Measure(FamilyInstance fi, XYZ? target)
        {
            XYZ? cur = null;
            try { cur = fi.FacingOrientation; } catch { }
            if (cur == null) return new Orientation(0, 0, 0, "unknown");

            double len = Math.Sqrt(cur.X * cur.X + cur.Y * cur.Y);
            if (len < 1e-9) return new Orientation(0, 0, 0, "unknown");

            double dx = cur.X / len, dy = cur.Y / len;
            if (target == null) return new Orientation(dx, dy, 0, "unknown");

            double err = SocketLayout.AbsAngleDeg(dx, dy, target.X, target.Y);
            return new Orientation(dx, dy, err, "measured");
        }

        /// <summary>Where the instance's visible mass actually sits, measured
        /// along the target normal from the WALL CENTRELINE, in mm.
        ///
        /// Every other facing check in this file reads FamilyInstance
        /// FacingOrientation, which is a NOMINAL axis: a family authored with
        /// its faceplate on the opposite side still reports a clean
        /// facing_error_deg after OrientToFace has aimed that axis into the
        /// room, and the drafter gets a socket buried in the wall with nothing
        /// complaining. This is the only field that looks at geometry instead.
        ///
        /// Measured from the wall centreline rather than the insertion point on
        /// purpose. A correct wall socket has a back box INSIDE the wall and a
        /// thin plate outside it, so relative to the insertion point its mass
        /// sits on the wall side even when it is right — a sign test there
        /// would flip good sockets. Past the two wall faces the picture is
        /// unambiguous: a correct socket protrudes a plate thickness into the
        /// room and nothing out the back, a reversed one protrudes a box depth.
        ///
        /// REPORT ONLY for now. No correction fires off these numbers until
        /// real families have been measured — see the socket_placement recipe.
        /// Null when there is no host, no target, or no bounding box.</summary>
        private static Dictionary<string, object?>? VisibleProfile(
            FamilyInstance fi, Wall? host, XYZ? target)
        {
            if (host == null || target == null) return null;

            double len = Math.Sqrt(target.X * target.X + target.Y * target.Y);
            if (len < 1e-9) return null;
            var n = new XYZ(target.X / len, target.Y / len, 0);

            var ip = (fi.Location as LocationPoint)?.Point;
            var axis = (host.Location as LocationCurve)?.Curve;
            if (ip == null || axis == null) return null;

            XYZ centre;
            try { centre = axis.Project(ip)?.XYZPoint ?? ip; } catch { return null; }

            var bb = fi.get_BoundingBox(null);
            if (bb == null) return null;
            var t = bb.Transform ?? Transform.Identity;

            double hi = double.MinValue, lo = double.MaxValue;
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                    {
                        var c = t.OfPoint(new XYZ(x, y, z));
                        double s = (c - centre).DotProduct(n);
                        if (s > hi) hi = s;
                        if (s < lo) lo = s;
                    }
            if (hi == double.MinValue) return null;

            double halfWidthFt;
            try { halfWidthFt = host.Width / 2.0; } catch { return null; }

            return new Dictionary<string, object?>
            {
                ["wall_width_mm"] = Math.Round(halfWidthFt * 2.0 * MmPerFoot, 1),
                // Positive = sticks out past that face. A correct socket should
                // be a small positive on the room side and <= 0 out the back.
                ["protrude_room_mm"] = Math.Round((hi - halfWidthFt) * MmPerFoot, 1),
                ["protrude_back_mm"] = Math.Round((-lo - halfWidthFt) * MmPerFoot, 1),
                // Signed offset of the bbox centre from the wall centreline.
                ["centre_offset_mm"] = Math.Round((hi + lo) / 2.0 * MmPerFoot, 1),
            };
        }

        /// <summary>Confirm the instance actually sits at the requested height,
        /// and fix it if not.
        ///
        /// It is NOT guaranteed that NewFamilyInstance honours the insertion
        /// point's Z for a hosted electrical fixture rather than snapping to the
        /// level, so the result is read back and repaired through the elevation
        /// parameters. The route taken is reported per item — a mounting height
        /// that could not be set must be visible, never silent.</summary>
        private static string EnsureMountHeight(Document doc, FamilyInstance fi, double targetZFt, double mountMm)
        {
            if (ActualZ(fi, out double z0) && Math.Abs(z0 - targetZFt) <= ZTolFt)
                return "insertion_point";

            var candidates = new[]
            {
                BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM,
            };

            foreach (var bip in candidates)
            {
                var prm = fi.get_Parameter(bip);
                if (prm == null || prm.IsReadOnly || prm.StorageType != StorageType.Double) continue;
                try
                {
                    prm.Set(mountMm / MmPerFoot);
                    doc.Regenerate();
                }
                catch { continue; }
                if (ActualZ(fi, out double z1) && Math.Abs(z1 - targetZFt) <= ZTolFt)
                    return bip.ToString();
            }

            return "unset";
        }

        private static bool ActualZ(FamilyInstance fi, out double zFt)
        {
            zFt = 0;
            if (fi.Location is LocationPoint lp) { zFt = lp.Point.Z; return true; }
            var bb = fi.get_BoundingBox(null);
            if (bb == null) return false;
            zFt = (bb.Min.Z + bb.Max.Z) / 2.0;
            return true;
        }
    }
}
