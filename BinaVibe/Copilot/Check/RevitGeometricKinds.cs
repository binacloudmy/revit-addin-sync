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
            Each(run, selected, s =>
            {
                if (!(s.Element is SpatialElement se)) return NotARoom(s);
                var placed = se.Location != null;
                var area = AreaM2(se);
                if (!placed || area <= 0) return GeometricOutcomes.TurningCircleGe(placed, area, null, 0, a);

                var loops = BoundaryLoops(se);
                if (loops.Count == 0) return GeometricOutcomes.TurningCircleGe(true, area, null, 0, a);
                var obstacles = se is Room room ? Obstacles(s.Source.Doc!, room) : new List<ClearCircle.Box>();
                return GeometricOutcomes.TurningCircleGe(true, area, ClearCircle.Find(loops, obstacles), obstacles.Count, a);
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

        /// <summary>Plan boxes of fixtures and furniture whose centre is inside the room.</summary>
        private static List<ClearCircle.Box> Obstacles(Document doc, Room room)
        {
            var boxes = new List<ClearCircle.Box>();
            var bb = room.get_BoundingBox(null);
            if (bb == null) return boxes;
            var z = (room.Location as LocationPoint)?.Point.Z ?? bb.Min.Z;
            var near = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(ObstacleCategories))
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max)))
                .WhereElementIsNotElementType();
            foreach (var e in near)
            {
                var eb = e.get_BoundingBox(null);
                if (eb == null) continue;
                var centre = new XYZ((eb.Min.X + eb.Max.X) / 2, (eb.Min.Y + eb.Max.Y) / 2, z + 1.0);
                bool inside;
                try
                {
                    inside = room.IsPointInRoom(centre);
                }
                catch
                {
                    inside = false;
                }
                if (inside) boxes.Add(new ClearCircle.Box(Mm(eb.Min.X), Mm(eb.Min.Y), Mm(eb.Max.X), Mm(eb.Max.Y)));
            }
            return boxes;
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
                curve = g.Curve;
            }
            catch
            {
                return null;   // multi-segment grid: no single curve
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
