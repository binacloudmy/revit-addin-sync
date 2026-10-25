#nullable enable
// The geometric and model kinds against the Revit API (spec §21). Each
// function gathers facts from the elements CheckRunner selected and hands
// them to the Revit-free verdicts (GeometricOutcomes, LinkGeometry,
// DuplicateKeys, ClearCircle, WarningGroups). Read-only: no transactions.
// One element that throws becomes a not_verified outcome; the rest go on.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    /// <summary>An element a rule's selector picked, with what was read from it.</summary>
    internal sealed class SelectedElement
    {
        public Element Element = null!;
        public ElementValues Values = null!;
        public CheckSource Source = null!;
    }

    internal static class RevitGeometricKinds
    {
        /// <summary>What can stand on a toilet or room floor and block a turning circle.</summary>
        private static readonly BuiltInCategory[] ObstacleCategories =
        {
            BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_FurnitureSystems,
            BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_SpecialityEquipment,
            BuiltInCategory.OST_GenericModel,
        };

        /// <summary>View types that can hold annotation tags.</summary>
        private static readonly ViewType[] TaggableViews =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan,
            ViewType.Elevation, ViewType.Section, ViewType.Detail, ViewType.ThreeD,
        };

        private static double Mm(double feet) => RevitCheckReader.Mm(feet);

        private static void Each(RuleRun run, IReadOnlyList<SelectedElement> selected, Func<SelectedElement, Outcome> evaluate)
        {
            foreach (var s in selected)
            {
                Outcome outcome;
                try
                {
                    outcome = s.Values.UnavailableReason != null
                        ? Outcome.NotVerified(s.Values.UnavailableReason, null, OutcomeCodes.SourceUnavailable)
                        : evaluate(s);
                }
                catch (Exception ex)
                {
                    outcome = Outcome.NotVerified($"could not be checked: {ex.GetType().Name}: {ex.Message}", null, OutcomeCodes.EngineError);
                }
                run.Add(s.Values.Ref, outcome);
            }
        }

        // ------------------------------------------------------------------
        // Rooms
        // ------------------------------------------------------------------

        private static Outcome NotARoom(SelectedElement s) =>
            Outcome.NotVerified($"element {s.Values.Ref.Id} ({s.Values.Category}) is not a room, area or space", null, OutcomeCodes.NotApplicable);

        private static double AreaM2(SpatialElement se) => UnitUtils.ConvertFromInternalUnits(se.Area, UnitTypeId.SquareMeters);

        public static void RoomPlaced(RuleRun run, IReadOnlyList<SelectedElement> selected) =>
            Each(run, selected, s => s.Element is SpatialElement se ? GeometricOutcomes.RoomPlaced(se.Location != null) : NotARoom(s));

        public static void RoomEnclosed(RuleRun run, IReadOnlyList<SelectedElement> selected) =>
            Each(run, selected, s => s.Element is SpatialElement se ? GeometricOutcomes.RoomEnclosed(se.Location != null, AreaM2(se)) : NotARoom(s));

        public static void TurningCircle(RuleRun run, IReadOnlyList<SelectedElement> selected)
        {
            var a = run.Rule.Assertion;
            var need = GeometricOutcomes.MinMm(a);
            Each(run, selected, s =>
            {
                if (!(s.Element is SpatialElement se)) return NotARoom(s);
                var placed = se.Location != null;
                var area = AreaM2(se);
                if (!placed || area <= 0) return GeometricOutcomes.TurningCircleGe(placed, area, null, a);
                if (!(se is Room room))
                    return Outcome.NotVerified("fixtures and door swings are read for rooms only, not spaces or areas", null, OutcomeCodes.NotApplicable);

                var loops = BoundaryLoops(se);
                if (loops.Count == 0) return GeometricOutcomes.TurningCircleGe(true, area, null, a);
                if (!TryObstacles(s.Source.Doc!, room, out var boxes, out var doors, out var why))
                    return Outcome.NotVerified(why ?? "the room's fixtures or doors could not be read", null, OutcomeCodes.Unreadable);
                return GeometricOutcomes.TurningCircleGe(true, area, TurningSpace.Evaluate(loops, boxes, doors, need), a);
            });
        }

        private static List<IReadOnlyList<(double X, double Y)>> BoundaryLoops(SpatialElement se)
        {
            var loops = new List<IReadOnlyList<(double X, double Y)>>();
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            var boundary = se.GetBoundarySegments(options);
            if (boundary == null) return loops;
            foreach (var loop in boundary)
            {
                var points = new List<(double X, double Y)>();
                foreach (var segment in loop)
                {
                    var pts = segment.GetCurve().Tessellate();
                    // Each segment's last point is the next one's first.
                    for (var i = 0; i < pts.Count - 1; i++) points.Add((Mm(pts[i].X), Mm(pts[i].Y)));
                }
                if (points.Count >= 3) loops.Add(points);
            }
            return loops;
        }

        /// <summary>Every fixture/furniture box the room's bounding box touches (boxes outside
        /// the room change nothing), and the swings of the doors that open into it. Anything
        /// that cannot be read makes the whole room not_verified: an obstacle silently left
        /// out could only make the circle look bigger.</summary>
        private static bool TryObstacles(Document doc, Room room, out List<ClearCircle.Box> boxes, out List<DoorSwing> doors, out string? why)
        {
            boxes = new List<ClearCircle.Box>();
            doors = new List<DoorSwing>();
            why = null;
            var bb = room.get_BoundingBox(null);
            if (bb == null)
            {
                why = "the room has no bounding box";
                return false;
            }

            IList<Element> fixtures;
            try
            {
                fixtures = new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(ObstacleCategories))
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max)))
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch (Exception ex)
            {
                why = $"the fixtures near the room could not be collected: {ex.Message}";
                return false;
            }
            foreach (var e in fixtures)
            {
                BoundingBoxXYZ? eb;
                try
                {
                    eb = e.get_BoundingBox(null);
                }
                catch (Exception ex)
                {
                    why = $"fixture {e.Id.Value} could not be read: {ex.Message}";
                    return false;
                }
                if (eb == null)
                {
                    why = $"fixture {e.Id.Value} ({e.Category?.Name}) has no bounding box";
                    return false;
                }
                boxes.Add(new ClearCircle.Box(Mm(eb.Min.X), Mm(eb.Min.Y), Mm(eb.Max.X), Mm(eb.Max.Y)));
            }

            IList<Element> nearDoors;
            var reach = new XYZ(DoorReachFt, DoorReachFt, 0);
            try
            {
                nearDoors = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min - reach, bb.Max + reach)))
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch (Exception ex)
            {
                why = $"the doors near the room could not be collected: {ex.Message}";
                return false;
            }
            var probeZ = (bb.Min.Z + bb.Max.Z) / 2;
            foreach (var e in nearDoors)
            {
                if (!TryDoorSwing(room, e, probeZ, out var swing, out why)) return false;
                if (swing != null) doors.Add(swing);
            }
            return true;
        }

        /// <summary>How far outside the room's box a door can stand and still be in its boundary wall.</summary>
        private const double DoorReachFt = 2.0;

        /// <summary>Probe distance past the wall face, to see which side of the door the room is on.</summary>
        private const double ProbeFt = 1.0;

        /// <summary>The door's swing into <paramref name="room"/>, null when it is not a door of
        /// this room or opens away from it; false (with why) when its geometry cannot be read.
        /// Convention (Revit's door families; verify per project): the leaf swings to the
        /// FacingOrientation side. The hinge end is left open (DoorSwing has both).</summary>
        private static bool TryDoorSwing(Room room, Element e, double probeZ, out DoorSwing? swing, out string? why)
        {
            swing = null;
            why = null;
            var label = $"door {e.Id.Value}";
            try
            {
                if (!(e is FamilyInstance door) || !(door.Location is LocationPoint lp))
                {
                    why = $"{label} has no point location, so its swing cannot be placed";
                    return false;
                }
                var loc = lp.Point;
                var facing = door.FacingOrientation;
                var hand = door.HandOrientation;
                var halfWallFt = door.Host is Wall wall ? wall.Width / 2 : 0;
                var probe = halfWallFt + ProbeFt;
                var front = room.IsPointInRoom(new XYZ(loc.X + facing.X * probe, loc.Y + facing.Y * probe, probeZ));
                var back = room.IsPointInRoom(new XYZ(loc.X - facing.X * probe, loc.Y - facing.Y * probe, probeZ));
                if (!front && !back) return true;      // a door of another room
                if (front && back)
                {
                    why = $"{label}: the room is on both sides of it, so its swing side cannot be told";
                    return false;
                }
                if (!front) return true;               // swings away from this room

                var leaf = LeafWidthFt(door);
                if (leaf == null)
                {
                    why = $"{label} has no readable Width, so its swing radius is unknown";
                    return false;
                }
                swing = DoorSwing.FromOrientation(label, Mm(loc.X), Mm(loc.Y), hand.X, hand.Y, facing.X, facing.Y, Mm(leaf.Value), Mm(halfWallFt));
                return true;
            }
            catch (Exception ex)
            {
                why = $"{label} geometry could not be read: {ex.Message}";
                return false;
            }
        }

        private static double? LeafWidthFt(FamilyInstance door)
        {
            foreach (var p in new[]
            {
                door.get_Parameter(BuiltInParameter.DOOR_WIDTH),
                door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH),
                door.LookupParameter("Width"),
                door.Symbol?.LookupParameter("Width"),
            })
            {
                if (p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 0) return p.AsDouble();
            }
            return null;
        }

        // ------------------------------------------------------------------
        // Doors, dimensions, families, duplicates
        // ------------------------------------------------------------------

        public static void ClearWidth(RuleRun run, IReadOnlyList<SelectedElement> selected) =>
            Each(run, selected, s => GeometricOutcomes.ClearWidthGe(s.Values, run.Rule.Assertion));

        public static void DimensionOverridden(RuleRun run, IReadOnlyList<SelectedElement> selected) =>
            Each(run, selected, s =>
            {
                if (!(s.Element is Dimension d))
                    return Outcome.NotVerified($"element {s.Values.Ref.Id} is not a dimension", null, OutcomeCodes.NotApplicable);
                var segments = new List<(string? Override, string? Actual)>();
                if (d.NumberOfSegments > 1)
                {
                    foreach (DimensionSegment segment in d.Segments) segments.Add((segment.ValueOverride, Safe(() => segment.ValueString)));
                }
                else
                {
                    segments.Add((d.ValueOverride, Safe(() => d.ValueString)));
                }
                return GeometricOutcomes.DimensionOverridden(segments);
            });

        public static void InPlaceFamily(RuleRun run, IReadOnlyList<SelectedElement> selected) =>
            Each(run, selected, s =>
            {
                var family = (s.Element as FamilyInstance)?.Symbol?.Family;
                return GeometricOutcomes.InPlaceFamily(family?.IsInPlace == true, family?.Name ?? s.Values.FamilyName);
            });

        public static void Duplicates(RuleRun run, IReadOnlyList<SelectedElement> selected)
        {
            var items = new List<BoxItem>();
            foreach (var s in selected)
            {
                BoundingBoxXYZ? bb = null;
                try
                {
                    bb = s.Element.get_BoundingBox(null);
                }
                catch
                {
                    // No box: DuplicateKeys reports it not_verified.
                }
                items.Add(new BoxItem(
                    s.Values.Ref, s.Source.Label, s.Values.Category ?? "",
                    s.Element.GetTypeId()?.Value.ToString() ?? "", s.Element.LevelId?.Value.ToString() ?? "",
                    bb == null ? null : new[] { Mm(bb.Min.X), Mm(bb.Min.Y), Mm(bb.Min.Z) },
                    bb == null ? null : new[] { Mm(bb.Max.X), Mm(bb.Max.Y), Mm(bb.Max.Z) }));
            }
            foreach (var r in DuplicateKeys.Evaluate(items, run.Rule.Assertion.ToleranceMm)) run.Add(r.Ref, r.Outcome);
        }

        // ------------------------------------------------------------------
        // Tags in the active view
        // ------------------------------------------------------------------

        /// <summary>Why <paramref name="view"/> cannot be checked for tags, or null when it can.</summary>
        public static string? UntaggableViewReason(View? view)
        {
            if (view == null) return "there is no active view to check tags in";
            if (view.IsTemplate) return $"view '{view.Name}' is a view template";
            if (!TaggableViews.Contains(view.ViewType))
                return $"the active view '{view.Name}' ({view.ViewType}) cannot hold tags: open a plan, section or elevation";
            return null;
        }

        /// <param name="selected">Host elements visible in <paramref name="view"/>.</param>
        public static void TaggedInView(RuleRun run, IReadOnlyList<SelectedElement> selected, Document doc, View view)
        {
            var tagged = new HashSet<long>();
            foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                foreach (var id in tag.GetTaggedLocalElementIds())
                    tagged.Add(id.Value);
            foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpatialElementTag)))
            {
                ElementId? id = tag switch
                {
                    RoomTag rt => rt.TaggedLocalRoomId,
                    AreaTag at => at.Area?.Id,
                    SpaceTag st => st.Space?.Id,
                    _ => null,
                };
                if (id != null && id != ElementId.InvalidElementId) tagged.Add(id.Value);
            }

            Each(run, selected, s => s.Source.IsHost
                ? GeometricOutcomes.TaggedInView(tagged.Contains(s.Values.Ref.Id), view.Name)
                : Outcome.NotVerified("tags on linked elements are not checked", null, OutcomeCodes.NotApplicable));
        }

        // ------------------------------------------------------------------
        // Document-wide kinds
        // ------------------------------------------------------------------

        public static void ImportedCad(RuleRun run, Document doc)
        {
            var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            if (imports.Count == 0)
            {
                run.AddDocument(Outcome.Pass("0", "no imported or linked CAD in the model"));
                return;
            }
            foreach (var ii in imports)
            {
                var name = doc.GetElement(ii.GetTypeId())?.Name ?? ii.Category?.Name ?? "CAD";
                run.Add(new ElementRef(ii.Id.Value, null), GeometricOutcomes.ImportedCad(ii.IsLinked, name));
            }
        }

        public static void Warnings(RuleRun run, Document doc)
        {
            var registry = Autodesk.Revit.ApplicationServices.Application.GetFailureDefinitionRegistry();
            var items = new List<WarningItem>();
            foreach (var w in doc.GetWarnings())
            {
                var definition = w.GetFailureDefinitionId();
                string? description = null;
                try
                {
                    description = registry?.FindFailureDefinition(definition)?.GetDescriptionText();
                }
                catch
                {
                    // Fall back to the warning's own text.
                }
                if (string.IsNullOrWhiteSpace(description)) description = w.GetDescriptionText();
                items.Add(new WarningItem(
                    definition.Guid.ToString(),
                    description ?? "",
                    w.GetFailingElements().Select(id => new ElementRef(id.Value, null)).ToList()));
            }
            WarningGroups.AddTo(run, WarningGroups.Group(items));
        }

        // ------------------------------------------------------------------
        // Host levels and grids against a link
        // ------------------------------------------------------------------

        public static void LevelsAgainstLink(RuleRun run, IReadOnlyList<SelectedElement> selected, CheckSource link)
        {
            var linkName = LinkNames.Clean(link.Name);
            if (link.Doc == null)
            {
                Each(run, selected, _ => Outcome.NotVerified(link.UnavailableReason ?? $"link {linkName} is not loaded", null, OutcomeCodes.SourceUnavailable));
                return;
            }
            var t = link.Transform;
            var linkLevels = new FilteredElementCollector(link.Doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => new NamedLevel(l.Name, Mm(t.OfPoint(new XYZ(0, 0, l.ProjectElevation)).Z)))
                .ToList();
            Each(run, selected, s => s.Element is Level host
                ? LinkGeometry.CompareLevel(host.Name, Mm(host.ProjectElevation), linkLevels, linkName, run.Rule.Assertion.ToleranceMm)
                : Outcome.NotVerified($"element {s.Values.Ref.Id} is not a level", null, OutcomeCodes.NotApplicable));
        }

        public static void GridsAgainstLink(RuleRun run, IReadOnlyList<SelectedElement> selected, CheckSource link)
        {
            var linkName = LinkNames.Clean(link.Name);
            if (link.Doc == null)
            {
                Each(run, selected, _ => Outcome.NotVerified(link.UnavailableReason ?? $"link {linkName} is not loaded", null, OutcomeCodes.SourceUnavailable));
                return;
            }
            var linkGrids = new List<NamedGrid>();
            var linkMultiSegment = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in new FilteredElementCollector(link.Doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                var plan = Plan(g, link.Transform);
                if (plan.HasValue) linkGrids.Add(new NamedGrid(g.Name, plan.Value));
                else linkMultiSegment.Add(g.Name);
            }

            var a = run.Rule.Assertion;
            Each(run, selected, s =>
            {
                if (!(s.Element is Grid host))
                    return Outcome.NotVerified($"element {s.Values.Ref.Id} is not a grid", null, OutcomeCodes.NotApplicable);
                var plan = Plan(host, Transform.Identity);
                if (!plan.HasValue || (linkMultiSegment.Contains(host.Name) && !linkGrids.Any(g => string.Equals(g.Name, host.Name, StringComparison.OrdinalIgnoreCase))))
                    return Outcome.NotVerified($"grid '{host.Name}' has several segments here or in {linkName}; only single-curve grids are compared", null, OutcomeCodes.NotApplicable);
                return LinkGeometry.CompareGrid(host.Name, plan.Value, linkGrids, linkName, a.ToleranceMm, a.ToleranceDeg);
            });
        }

        private static PlanCurve? Plan(Grid g, Transform t)
        {
            Curve curve;
            try
            {
                // A segment of a multi-segment grid still has a Curve: only its own leg.
                if (MultiSegmentGrid.GetMultiSegementGridId(g) != ElementId.InvalidElementId) return null;
                curve = g.Curve;
            }
            catch
            {
                return null;
            }
            if (curve == null) return null;
            var p0 = t.OfPoint(curve.GetEndPoint(0));
            var p1 = t.OfPoint(curve.GetEndPoint(1));
            return new PlanCurve(Mm(p0.X), Mm(p0.Y), Mm(p1.X), Mm(p1.Y), curve is Line);
        }

        private static string? Safe(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }
    }
}
