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
            return PlacePlanPoints(doc, planId,
                // Optional: a drawing plan names a family type per point, and the point's own wins.
                familyType: ArgsHelp.GetString(args, "family_type"),
                wanted: ArgsHelp.GetLongList(args, "indices"),
                roles: ArgsHelp.GetStringList(args, "roles"),
                levelOverride: ArgsHelp.GetString(args, "level"),
                mountOverrideMm: ArgsHelp.GetDouble(args, "mount_height_mm"),
                facingOffsetDeg: ArgsHelp.GetDouble(args, "facing_offset_deg") ?? 0.0);
        }

        /// <summary>The body of place_socket_points, shared with place_cad_electrical. Its own
        /// TransactionGroup is assimilated; started inside an outer group (place_cad_electrical),
        /// it folds into that group's single undo step.</summary>
        internal static Dictionary<string, object?> PlacePlanPoints(
            Document doc, string planId, string? familyType, List<long> wanted, List<string> roles,
            string? levelOverride, double? mountOverrideMm, double facingOffsetDeg)
        {
            var plan = SocketPlanCache.Get(planId, SocketCandidates.DocKey(doc));

            var points = SocketPlanSelect.Select(plan, wanted, roles);
            int skippedExcluded = SocketPlanSelect.SkippedExcluded(plan, wanted, roles);

            if (points.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"no candidates selected from plan {planId} " +
                                $"(plan holds {plan.Points.Count} points, {skippedExcluded} of them excluded; " +
                                "indices are 0-based)",
                };

            // Family per point. Each distinct type is resolved once.
            var typeOf = new Dictionary<int, string?>();
            foreach (var p in points) typeOf[p.Index] = SocketPlanSelect.FamilyTypeFor(p, familyType);
            if (typeOf.Values.All(t => t == null))
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "no family type for any selected point — pass family_type, or give the " +
                                "block a family_type in the plan_cad_electrical mapping",
                };

            var symbols = new Dictionary<string, FamilySymbol?>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in typeOf.Values)
                if (t != null && !symbols.ContainsKey(t)) symbols[t] = ResolveSymbol(doc, t);
            if (symbols.Values.All(s => s == null))
                throw new ArgumentException(
                    "family type " + string.Join(", ", symbols.Keys.Select(k => "'" + k + "'")) + " not found in document");

            // The devices that exist now: a second run of the same plan must not double up.
            var present = CadElectricalResolver.ExistingDevices(doc);

            var created = new List<object>();
            var failed = new List<object>();
            var skippedPresent = new List<object?>();
            var placedPoints = new List<PlannedPoint>();
            var byRole = new Dictionary<string, object?>(StringComparer.Ordinal);
            var byType = new Dictionary<string, object?>(StringComparer.Ordinal);

            using var group = new TransactionGroup(doc, "BinaVibe: place_socket_points");
            group.Start();
            try
            {
                var inactive = symbols.Values.Where(s => s != null && !s.IsActive).ToList();
                if (inactive.Count > 0)
                {
                    using var activate = new Transaction(doc, "BinaVibe: activate socket types");
                    TxGuard.StartSwallowing(activate);
                    try
                    {
                        foreach (var s in inactive) s!.Activate();
                        doc.Regenerate();
                        TxGuard.CommitOrThrow(activate);
                    }
                    catch { activate.RollBack(); throw; }
                }

                foreach (var p in points)
                {
                    var t = typeOf[p.Index];
                    var symbol = t != null && symbols.TryGetValue(t, out var found) ? found : null;
                    if (symbol == null)
                    {
                        failed.Add(new Dictionary<string, object?>
                        {
                            ["index"] = p.Index,
                            ["room_id"] = p.RoomId,
                            ["reason"] = t == null
                                ? "no family type for this point — the plan names none and family_type was not passed"
                                : $"family type '{t}' not found in document",
                        });
                        continue;
                    }

                    // A point the drafter picked by index although the plan saw a device there
                    // is placed as asked; any other point next to a device is skipped.
                    bool chosenAnyway = wanted.Count > 0 && p.Flags.Contains("already_present");
                    if (!chosenAnyway && AlreadyPresent(p, mountOverrideMm, present))
                    {
                        skippedPresent.Add(p.Index);
                        continue;
                    }

                    try
                    {
                        var row = PlaceOne(doc, symbol, symbol.Family.FamilyPlacementType, p, levelOverride,
                                           mountOverrideMm, facingOffsetDeg);
                        row["family_type"] = t;
                        created.Add(row);
                        placedPoints.Add(p);
                        // route_conduit's drop finds the device by this id.
                        if (row.TryGetValue("created_id", out var createdId) && createdId != null)
                            p.PlacedId = Convert.ToInt64(createdId, System.Globalization.CultureInfo.InvariantCulture);
                        string role = p.Role.Length == 0 ? "socket" : p.Role;
                        byRole[role] = (byRole.TryGetValue(role, out var nr) ? (int)nr! : 0) + 1;
                        byType[t!] = (byType.TryGetValue(t!, out var nt) ? (int)nt! : 0) + 1;
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
            var rows = created.OfType<Dictionary<string, object?>>().ToList();
            var uncorrected = rows
                .Where(r => (r.TryGetValue("facing_method", out var m) ? m as string : null) == "uncorrectable")
                .Select(r => r.TryGetValue("index", out var i) ? i : null)
                .ToList();
            int elevationUnset = rows.Count(r => (r.TryGetValue("elevation_set_via", out var v) ? v as string : null) == "unset");
            // Hosted at the right height, but the family draws the fitting well away from it.
            var geometryOff = rows
                .Where(r => r.TryGetValue("geometry_touches_host", out var g) && g is bool b && !b)
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
                ["geometry_off_host_indices"] = geometryOff,
                ["by_role"] = byRole,
                ["by_family_type"] = byType,
                ["skipped_excluded"] = skippedExcluded,
                ["skipped_already_present"] = skippedPresent,
                ["review"] = CadElectricalReview.PlaceReview(placedPoints, uncorrected.Count, elevationUnset),
            };
        }

        /// <summary>A device of the model already stands where this point would go, against the
        /// model as it is now. Existing devices are at their contact height, not their
        /// LocationPoint Z. Within ExistingMm in 3D; a ceiling point also counts a device hosted
        /// on its own ceiling within ExistingMm in plan (OverheadPlacement.IsPresent).</summary>
        private static bool AlreadyPresent(PlannedPoint p, double? mountOverrideMm, List<PresentDevice> present)
        {
            return OverheadPlacement.IsPresent(p.XMm, p.YMm, CadSymbolResolve.TargetZMm(p, mountOverrideMm),
                                               IsOverhead(p) ? p.HostCeilingId : null,
                                               present, new ResolveOptions().ExistingMm);
        }

        /// <summary>A point that is not on a wall (a ceiling device, a host "none" one): no facing.</summary>
        private static bool IsOverhead(PlannedPoint p) =>
            !p.HostWallId.HasValue && p.FacingDx == 0 && p.FacingDy == 0;

        private static Dictionary<string, object?> PlaceOne(
            Document doc, FamilySymbol symbol, FamilyPlacementType placement,
            PlannedPoint p, string? levelOverride, double? mountOverrideMm,
            double facingOffsetDeg)
        {
            // A point that is not on a wall (a ceiling device, a host "none" one) has no facing
            // and goes its own way, by how the family hosts.
            if (IsOverhead(p))
                return PlaceOverhead(doc, symbol, p, levelOverride, mountOverrideMm);

            double mountMm = mountOverrideMm ?? p.MountHeightMm;
            // ZMm was computed as floor + the plan's mount height; swapping the
            // mount height has to swap that component, not add to it.
            double zMm = CadSymbolResolve.TargetZMm(p, mountOverrideMm);
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
                FamilyInstance? fi = null;
                bool onFace = false;

                // A work-plane-based (face) family goes on the wall's side face that looks the
                // way the point faces. If that is refused, the wall overload below is tried.
                if (host != null && placement == FamilyPlacementType.WorkPlaneBased)
                {
                    fi = TryOnSideFace(doc, symbol, host, pt, facing);
                    onFace = fi != null;
                }

                if (fi == null && host != null && (hosted || placement == FamilyPlacementType.WorkPlaneBased))
                {
                    var hostLevel = doc.GetElement(host.LevelId) as Level ?? level;
                    fi = doc.Create.NewFamilyInstance(pt, symbol, host, hostLevel, StructuralType.NonStructural);
                }
                else if (fi == null)
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
                    ["host"] = onFace ? "wall_face" : host != null ? "wall" : "unhosted",
                    ["flipped"] = orient.Method == "flip",
                    ["facing"] = new List<object> { Math.Round(orient.Dx, 4), Math.Round(orient.Dy, 4) },
                    ["facing_target"] = new List<object> { Math.Round(targetDx, 4), Math.Round(targetDy, 4) },
                    ["facing_error_deg"] = Math.Round(orient.ErrorDeg, 2),
                    ["facing_method"] = orient.Method,
                    ["elevation_set_via"] = elevationVia,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        /// <summary>
        /// A point not on a wall: a ceiling device, or a host "none" one. By the family's
        /// FAMILY_HOSTING_BEHAVIOR (CadElectricalTools.HostingKind):
        ///   ceiling, or another host-based kind: the host overload on the plan's ceiling; with
        ///     no ceiling the point fails alone (no_ceiling), never placed free-standing;
        ///   face or work plane: on a bottom face of the ceiling over the point, reference
        ///     direction along X (TryOnBottomFace); with no ceiling, or when Revit refuses every
        ///     face, the level overload;
        ///   wall: fails alone, there is no wall here;
        ///   anything else (unhosted): the level overload, then the height from the level.
        ///
        /// Every height is a contact height (CadElectricalResolver.ContactHeight), never the
        /// raw LocationPoint Z: a work-plane-based family's origin can sit off its work plane by
        /// a per-family amount. A hosted point whose contact height ends up more than 20 mm off
        /// the target fails alone (height_mismatch); an unhosted point whose height cannot be set
        /// fails alone (elevation_unset). One Transaction, as for a wall point, so a refusal
        /// leaves nothing behind.
        /// </summary>
        private static Dictionary<string, object?> PlaceOverhead(
            Document doc, FamilySymbol symbol, PlannedPoint p, string? levelOverride, double? mountOverrideMm)
        {
            string hosting = CadElectricalTools.HostingKind(symbol.Family);
            Ceiling? ceiling = p.HostCeilingId.HasValue
                ? doc.GetElement(ElemIds.From(p.HostCeilingId.Value)) as Ceiling
                : null;

            if (hosting == "wall")
                throw new InvalidOperationException(
                    $"candidate {p.Index} is not on a wall (host={p.Host}) but '{symbol.Name}' is wall-hosted. " +
                    "Use a ceiling, face-based or unhosted family for it.");
            bool needsHost = hosting == "ceiling" || hosting == "hosted";
            if (needsHost && ceiling == null)
                throw new InvalidOperationException(
                    $"no_ceiling: candidate {p.Index} found no ceiling on its level, and '{symbol.Name}' is " +
                    "ceiling-hosted. Add the ceiling, or use a face-based or unhosted family.");

            double zMm = CadSymbolResolve.TargetZMm(p, mountOverrideMm);
            var pt = new XYZ(p.XMm / MmPerFoot, p.YMm / MmPerFoot, zMm / MmPerFoot);

            using var tx = new Transaction(doc, "BinaVibe: place ceiling device");
            TxGuard.StartSwallowing(tx);
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }

                Level? level = ResolveLevel(doc, levelOverride ?? p.LevelName, null)
                               ?? (ceiling != null ? doc.GetElement(ceiling.LevelId) as Level : null);
                FamilyInstance? fi;
                string placedOn;
                // On a ceiling the host sets the height; it is measured, never pushed off the ceiling.
                string elevationVia = "host";

                if (needsHost)
                {
                    var hostLevel = doc.GetElement(ceiling!.LevelId) as Level ?? level;
                    fi = doc.Create.NewFamilyInstance(pt, symbol, ceiling, hostLevel, StructuralType.NonStructural);
                    doc.Regenerate();
                    placedOn = "ceiling";
                    if (!OverheadPlacement.HeightOk(ContactMm(doc, fi), zMm))
                        throw HeightMismatch(p, zMm, new List<Dictionary<string, object?>> { Attempt(doc, fi, null) });
                }
                else
                {
                    bool faceKind = hosting == "face" || hosting == "work_plane";
                    var misses = new List<Dictionary<string, object?>>();
                    fi = faceKind && ceiling != null ? TryOnBottomFace(doc, symbol, ceiling, pt, zMm, misses) : null;
                    if (fi != null)
                    {
                        placedOn = "ceiling_face";
                    }
                    else if (misses.Count > 0)
                    {
                        // Revit took the face but the device did not land on the underside:
                        // never fall back to a free-standing one at the level.
                        throw HeightMismatch(p, zMm, misses);
                    }
                    else
                    {
                        fi = level != null
                            ? doc.Create.NewFamilyInstance(pt, symbol, level, StructuralType.NonStructural)
                            : doc.Create.NewFamilyInstance(pt, symbol, StructuralType.NonStructural);
                        doc.Regenerate();
                        placedOn = "unhosted";
                        elevationVia = SetLevelHeight(doc, fi, level, pt.Z, p.Index);
                    }
                }

                TxGuard.CommitOrThrow(tx);

                var contact = CadElectricalResolver.ContactHeight(doc, fi);
                double contactMm = contact.ZFt * MmPerFoot;
                ActualZ(fi, out double originZ);
                var row = new Dictionary<string, object?>
                {
                    ["index"] = p.Index,
                    ["room_id"] = p.RoomId,
                    ["created_id"] = fi.Id.Value,
                    ["host_ceiling_id"] = ceiling != null && placedOn != "unhosted" ? p.HostCeilingId : null,
                    ["host"] = placedOn,
                    ["facing_method"] = "not_applicable",
                    ["elevation_set_via"] = elevationVia,
                    ["z_mm"] = Math.Round(contactMm, 1),
                    ["height_via"] = contact.Via,
                    ["origin_z_mm"] = Math.Round(originZ * MmPerFoot, 1),
                    ["target_z_mm"] = Math.Round(zMm, 1),
                };
                BoundingBoxXYZ? bb = null;
                try { bb = fi.get_BoundingBox(null); } catch { bb = null; }
                if (bb != null)
                {
                    double minMm = bb.Min.Z * MmPerFoot, maxMm = bb.Max.Z * MmPerFoot;
                    row["geometry_z_mm"] = new List<object> { Math.Round(minMm, 1), Math.Round(maxMm, 1) };
                    row["geometry_touches_host"] = OverheadPlacement.GeometryTouches(minMm, maxMm, contactMm);
                }
                return row;
            }
            catch { tx.RollBack(); throw; }
        }

        /// <summary>
        /// Place a face-based or work-plane-based family on a bottom face of the ceiling over the
        /// point, reference direction along X, at the point where a vertical through the target
        /// crosses that face. The faces are tried in OverheadPlacement.FaceOrder: looking down,
        /// the one holding the target first. Each try is a SubTransaction and is measured: its
        /// contact height must be within 20 mm of the target, else it is rolled back, recorded in
        /// <paramref name="misses"/>, and the next face is tried, two at most. Null when no face
        /// took the device at the right height; <paramref name="misses"/> empty then means no
        /// face lay over the point or Revit refused them all.
        /// </summary>
        private static FamilyInstance? TryOnBottomFace(Document doc, FamilySymbol symbol, Ceiling ceiling, XYZ pt,
                                                       double targetZMm, List<Dictionary<string, object?>> misses)
        {
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetBottomFaces(ceiling); } catch { return null; }
            BoundingBoxXYZ? bb;
            try { bb = ceiling.get_BoundingBox(null); } catch { bb = null; }
            if (bb == null) return null;

            var candidates = new List<(Reference Ref, double ZFt)>();
            var keys = new List<(double NormalZ, double? ZMm)>();
            foreach (var r in refs)
            {
                double? z = null, nz = null;
                try
                {
                    if (ceiling.GetGeometryObjectFromReference(r) is Face face)
                    {
                        z = CadElectricalResolver.UndersideAt(new[] { face }, pt.X, pt.Y, bb.Min.Z - 1, bb.Max.Z + 1);
                        // GetBottomFaces names them bottom faces: one whose normal cannot be read
                        // is taken to look down.
                        if (z.HasValue) nz = CadElectricalResolver.NormalZ(face, pt.X, pt.Y, z.Value) ?? -1;
                    }
                }
                catch { z = null; /* a face that cannot be read is not a candidate */ }
                candidates.Add((r, z ?? 0));
                keys.Add((nz ?? 0, z.HasValue ? z.Value * MmPerFoot : (double?)null));
            }

            foreach (int i in OverheadPlacement.FaceOrder(keys, targetZMm).Take(2))
            {
                var faceZ = candidates[i].ZFt;
                using var st = new SubTransaction(doc);
                st.Start();
                try
                {
                    var fi = doc.Create.NewFamilyInstance(candidates[i].Ref, new XYZ(pt.X, pt.Y, faceZ), XYZ.BasisX, symbol);
                    doc.Regenerate();
                    if (OverheadPlacement.HeightOk(ContactMm(doc, fi), targetZMm))
                    {
                        st.Commit();
                        return fi;
                    }
                    misses.Add(Attempt(doc, fi, faceZ));
                    st.RollBack();
                }
                catch
                {
                    // Refused by Revit: not a height miss; the next face is tried.
                    if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                }
            }
            return null;
        }

        private static double ContactMm(Document doc, FamilyInstance fi) =>
            CadElectricalResolver.ContactHeight(doc, fi).ZFt * MmPerFoot;

        /// <summary>One placement as measured, for a height_mismatch report.</summary>
        private static Dictionary<string, object?> Attempt(Document doc, FamilyInstance fi, double? faceZFt)
        {
            var contact = CadElectricalResolver.ContactHeight(doc, fi);
            ActualZ(fi, out double originZ);
            return new Dictionary<string, object?>
            {
                ["face_z_mm"] = faceZFt.HasValue ? Math.Round(faceZFt.Value * MmPerFoot, 1) : (double?)null,
                ["z_mm"] = Math.Round(contact.ZFt * MmPerFoot, 1),
                ["height_via"] = contact.Via,
                ["origin_z_mm"] = Math.Round(originZ * MmPerFoot, 1),
            };
        }

        private static InvalidOperationException HeightMismatch(
            PlannedPoint p, double targetZMm, List<Dictionary<string, object?>> tries)
        {
            string tried = string.Join("; ", tries.Select(t =>
                (t["face_z_mm"] != null ? $"face at {t["face_z_mm"]} mm: " : "") +
                $"contact {t["z_mm"]} mm ({t["height_via"]}), origin {t["origin_z_mm"]} mm"));
            return new InvalidOperationException(
                $"height_mismatch: candidate {p.Index} did not land on the ceiling's underside at " +
                $"{Math.Round(targetZMm, 1)} mm (more than {OverheadPlacement.HeightTolMm} mm off). Tried {tried}.");
        }

        /// <summary>
        /// Put an overhead device placed with the level overload at its target height. Its
        /// height is the plane it was placed on (CadElectricalResolver.LevelBase) plus its offset
        /// parameter, never its LocationPoint: a work-plane-based family's origin can sit off its
        /// work plane (a live test read 2044 mm under an offset that had taken). The parameters
        /// that apply (CadElectricalResolver.OffsetParams: "Offset from Host" first for a
        /// work-plane-based family, which has no "Elevation from Level"; the other way round for
        /// an unhosted one) are each set to target minus that plane, and the first whose read-back
        /// value equals the value set wins. elevation_unset, failing the point alone, only when
        /// every one is missing, read-only, refused, or reads back a different value; it is never
        /// left at the level.
        /// </summary>
        private static string SetLevelHeight(Document doc, FamilyInstance fi, Level? level, double targetZFt, int index)
        {
            if (!CadElectricalResolver.LevelBase(doc, fi, level, out double baseFt))
                baseFt = level?.Elevation ?? 0;
            double want = targetZFt - baseFt;

            var why = new List<string>();
            foreach (var bip in CadElectricalResolver.OffsetParams(fi))
            {
                var prm = fi.get_Parameter(bip);
                if (prm == null) { why.Add(bip + " missing"); continue; }
                if (prm.IsReadOnly || prm.StorageType != StorageType.Double) { why.Add(bip + " read-only"); continue; }
                double read;
                try
                {
                    prm.Set(want);
                    doc.Regenerate();
                    read = prm.AsDouble();
                }
                catch (Exception ex) { why.Add(bip + " refused (" + ex.Message + ")"); continue; }
                if (OverheadPlacement.ReadBackOk(want * MmPerFoot, read * MmPerFoot)) return bip.ToString();
                why.Add($"{bip} set {Math.Round(want * MmPerFoot, 1)} mm, read back {Math.Round(read * MmPerFoot, 1)} mm");
            }

            // A family with neither parameter keeps the insertion point when that is right.
            if (why.All(w => w.EndsWith(" missing", StringComparison.Ordinal)) &&
                ActualZ(fi, out double z) && Math.Abs(z - targetZFt) <= ZTolFt)
                return "insertion_point";

            throw new InvalidOperationException(
                $"elevation_unset: candidate {index} was placed at the level but its height could not be set to " +
                $"{Math.Round(targetZFt * MmPerFoot, 1)} mm ({Math.Round(want * MmPerFoot, 1)} mm above " +
                $"{Math.Round(baseFt * MmPerFoot, 1)} mm): {string.Join("; ", why)}. " +
                "Use a ceiling, face-based or unhosted family for it.");
        }

        // ─── place_socket_on_wall ───────────────────────────────────────
        // Sibling of place_door / place_window for the ad-hoc single socket.
        // Callable from inside execute_revit_batch.
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
                Orientation orient;
                if (facingArgs != null)
                {
                    SocketLayout.ApplyOffsetDeg(facingArgs.X, facingArgs.Y, facingOffsetDeg,
                                                out double tdx, out double tdy);
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
                    ["facing_error_deg"] = Math.Round(orient.ErrorDeg, 2),
                    ["facing_method"] = orient.Method,
                    ["facing_offset_deg"] = facingOffsetDeg,
                    ["elevation_set_via"] = elevationVia,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── helpers ────────────────────────────────────────────────────

        /// <summary>Place a face-based family on the wall side face whose normal is closest to
        /// the point's facing (and nearest the point among those), in a SubTransaction so a
        /// refusal leaves nothing behind. Null when there is no such face or Revit refuses.</summary>
        private static FamilyInstance? TryOnSideFace(Document doc, FamilySymbol symbol, Wall host, XYZ pt, XYZ facing)
        {
            Reference? best = null;
            XYZ? bestPoint = null;
            double bestDistance = double.MaxValue;
            foreach (var side in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(host, side); } catch { continue; }
                foreach (var r in refs)
                {
                    try
                    {
                        if (!(host.GetGeometryObjectFromReference(r) is Face face)) continue;
                        var hit = face.Project(pt);
                        if (hit == null) continue;
                        var n = face.ComputeNormal(hit.UVPoint);
                        if (n.X * facing.X + n.Y * facing.Y < 0.5) continue;
                        if (hit.Distance >= bestDistance) continue;
                        best = r;
                        bestPoint = hit.XYZPoint;
                        bestDistance = hit.Distance;
                    }
                    catch { /* a face that cannot be read is not a candidate */ }
                }
            }
            if (best == null || bestPoint == null) return null;

            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                // Along the wall, so the family's own up stays up on a vertical face.
                var alongWall = new XYZ(-facing.Y, facing.X, 0);
                var fi = doc.Create.NewFamilyInstance(best, bestPoint, alongWall, symbol);
                doc.Regenerate();
                st.Commit();
                return fi;
            }
            catch
            {
                st.RollBack();
                return null;
            }
        }

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

            // A face-hosted instance faces out of the face it sits on, and that face was picked
            // to look the way the point faces. Flipping turns it about the face (upside down) and
            // a rotation about Z would tear it off the face, so it is measured, never corrected.
            if (FaceHostedFacing(fi) != null)
                return new Orientation(state.Dx, state.Dy, state.ErrorDeg,
                    state.ErrorDeg <= FacingTolDeg ? "as_placed" : "uncorrectable");
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
            XYZ? cur = FaceHostedFacing(fi);
            if (cur == null)
            {
                try { cur = fi.FacingOrientation; } catch { }
            }
            if (cur == null) return new Orientation(0, 0, 0, "unknown");

            double len = Math.Sqrt(cur.X * cur.X + cur.Y * cur.Y);
            if (len < 1e-9) return new Orientation(0, 0, 0, "unknown");

            double dx = cur.X / len, dy = cur.Y / len;
            if (target == null) return new Orientation(dx, dy, 0, "unknown");

            double err = SocketLayout.AbsAngleDeg(dx, dy, target.X, target.Y);
            return new Orientation(dx, dy, err, "measured");
        }

        /// <summary>
        /// The plan facing of a face-based (work-plane-based) instance hosted on a face: the
        /// host face's outward normal at the instance's point, flattened to plan. Null for any
        /// other instance, so wall-hosted and unhosted ones keep reading FacingOrientation.
        ///
        /// FacingOrientation is not the front of a face-based instance: it is the family's Y
        /// axis, which lies in the host face, so on a wall face it points up and reads [0,0] in
        /// plan. The face normal is used rather than HandOrientation turned 90 degrees in plan,
        /// because the sign of that turn depends on how the family was authored and on
        /// mirroring, while the face normal is fixed by the face the socket sits on. When the
        /// face cannot be read (a host in a link, a face gone after an edit) the instance's own
        /// Z axis (GetTotalTransform().BasisZ, the work plane normal it was placed with) stands
        /// in. A horizontal face has no plan facing and gives null.
        /// </summary>
        internal static XYZ? FaceHostedFacing(FamilyInstance fi)
        {
            Reference? hostFace = null;
            try
            {
                if (fi.Symbol?.Family?.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased) return null;
                hostFace = fi.HostFace;
            }
            catch { hostFace = null; }
            if (hostFace == null) return null;

            XYZ? n = null;
            try
            {
                if (fi.Host != null && fi.Host.GetGeometryObjectFromReference(hostFace) is Face face &&
                    fi.Location is LocationPoint lp)
                {
                    IntersectionResult? hit = face.Project(lp.Point);
                    if (hit != null) n = face.ComputeNormal(hit.UVPoint);
                }
            }
            catch { n = null; }
            if (n == null)
            {
                try { n = fi.GetTotalTransform().BasisZ; } catch { n = null; }
            }
            if (n == null) return null;

            double len = Math.Sqrt(n.X * n.X + n.Y * n.Y);
            return len < 1e-6 ? null : new XYZ(n.X / len, n.Y / len, 0);
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
