#if !REVIT2023_24
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Cad2Bim;
using Cad2Bim.Services;

// Revit has its own Segment and Wall; the classifier's are always spelled through these.
using CadSegment = Cad2Bim.Segment;
using CadWall = Cad2Bim.Wall;
using CadOpening = Cad2Bim.Opening;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Reads a DWG and builds native Revit walls from it.
    ///
    /// This is what "CAD to RVT" has to mean. RVT is a closed format that nothing outside
    /// Revit can write, so the only way to produce one is to create the elements inside a
    /// running Revit and let the user save. The IFC export covers the exchange case; this
    /// covers the case where the deliverable is a Revit model.
    ///
    /// The classification is the same code the standalone viewer runs - the Cad2Bim sources
    /// carry no Revit dependency, which is what makes them usable from both.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Cad2BimConvertCommand : IExternalCommand
    {
        // Millimetres. A plan carries no height, so this is an assumption until a section is
        // read; 3 m is the ordinary storey.
        private const double DefaultWallHeightMm = 3000.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIDocument uiDocument = commandData.Application.ActiveUIDocument;
                Document document = uiDocument?.Document;

                if (document == null)
                {
                    TaskDialog.Show("BINA CAD to BIM", "Open a Revit model first.");
                    return Result.Cancelled;
                }

                string path = PickDrawing();
                if (path == null) return Result.Cancelled;

                ACadSharp.CadDocument drawing = CadRenderSource.Read(path);

                // Read once to see what layers the drawing has, then again through a filter
                // built from them. A drawing that keeps its walls on a wall layer is telling
                // us where they are, and no threshold beats being told.
                CadModel survey = ModelSource.Read(drawing, BuildFilter());
                List<string> wallLayers = WallLayers(survey.LayerCensus.Keys);

                CadModel model = survey;
                if (wallLayers.Count > 0)
                {
                    LayerFilter focused = BuildFilter();
                    focused.Include.AddRange(wallLayers);

                    // Door and window linework has to reach the classifier for the openings,
                    // even though walls are taken only from the wall layers below.
                    focused.Include.AddRange(survey.LayerCensus.Keys.Where(IsOpeningLayer));
                    model = ModelSource.Read(drawing, focused);
                }
                // Walls come only from the wall layers even though the read is wider: door and
                // window linework pairs into false walls if allowed to stand in for fabric.
                List<CadSegment> wallSegments = model.Segments
                    .Where(seg => seg.Layer.Length == 0 || IsWallLayer(seg.Layer))
                    .ToList();

                if (wallSegments.Count == 0) wallSegments = model.Segments.ToList();

                List<CadWall> walls = CadClassifier.ClassifyWalls(wallSegments);

                if (walls.Count == 0)
                {
                    TaskDialog.Show("BINA CAD to BIM",
                        "No walls found in " + System.IO.Path.GetFileName(path) + ".\n\n" +
                        model.Segments.Count + " segments were read. If the drawing keeps its walls " +
                        "on a layer of their own, filtering to that layer usually finds them.");
                    return Result.Cancelled;
                }

                // Doors are found by geometry, not by layer. In these drawings the swings sit
                // on Hatch-wall and C-door while A-DOOR holds only small fillets, so a layer
                // named for doors is no guide; a quarter-turn arc of leaf-width radius is a
                // door swing wherever it was filed. Windows are the opposite - their linework
                // is on a window layer and has no shape that identifies it.
                List<CadSegment> windowLines = model.Segments.Where(seg => IsWindowLayer(seg.Layer)).ToList();
                List<Cad2Bim.Arc> arcs = model.Arcs.ToList();
                // One storey per floor plan. Read literally the sheet is a single level 350
                // metres across with every floor lying flat beside the others - a carpet, not a
                // building. Each plan gets its own level, and each is shifted so its own corner
                // meets the origin, which stacks them the way the building actually is.
                List<PlanCluster> plans = CadClassifier.ClusterPlans(walls, model.Texts);
                if (plans.Count == 0)
                {
                    TaskDialog.Show("BINA CAD to BIM", "No floor plans could be separated out.");
                    return Result.Cancelled;
                }

                WallType wallType = DefaultWallType(document);
                if (wallType == null)
                {
                    TaskDialog.Show("BINA CAD to BIM", "This model has no basic wall type to use.");
                    return Result.Cancelled;
                }

                int created = 0;
                int rooms = 0;
                int doors = 0;
                int windows = 0;
                int spacesFound = 0;
                var createdIds = new List<ElementId>();

                using (var transaction = new Transaction(document, "CAD to BIM"))
                {
                    transaction.Start();

                    // Revit tries to join every new wall to whatever it touches, and traced
                    // walls touch constantly. Each failed join raises a dialog, so a thousand
                    // walls becomes a thousand interruptions; the joins are not wanted anyway.
                    FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(new SilenceJoinFailures());
                    options.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(options);

                    for (int i = 0; i < plans.Count; i++)
                    {
                        PlanCluster plan = plans[i];
                        Level level = LevelFor(document, i);
                        if (level == null) continue;

                        var hosts = new Dictionary<CadWall, Autodesk.Revit.DB.Wall>();

                        foreach (CadWall wall in plan.Walls)
                        {
                            try
                            {
                                Autodesk.Revit.DB.Wall made =
                                    CreateWall(document, wall, wallType, level, plan.MinX, plan.MinY);

                                if (made != null)
                                {
                                    created++;
                                    createdIds.Add(made.Id);
                                    hosts[wall] = made;
                                }
                            }
                            catch
                            {
                                // One bad centreline should not cost the other thousand.
                            }
                        }

                        // Everything after the walls is computed per plan, so a room cannot
                        // span two floors and an opening cannot host itself on the wrong one.
                        WallGraph graph = CadClassifier.CreateTopologicalPoints(plan.Walls);
                        List<Space> spaces = CadClassifier.ClassifySpaces(graph, model.Texts);
                        CadClassifier.SplitWalls(plan.Walls, spaces);
                        spacesFound += spaces.Count;

                        List<CadOpening> openings =
                            CadClassifier.ClassifyOpeningsFromSymbols(plan.Walls, arcs, windowLines);

                        document.Regenerate();
                        CreateOpenings(document, openings, hosts, level, plan.MinX, plan.MinY,
                                       ref doors, ref windows);

                        rooms += CreateRooms(document, spaces, level, plan.MinX, plan.MinY);
                    }

                    transaction.Commit();
                }

                // Ask the model what is actually in it rather than trusting the loop's own
                // tally: a transaction that rolls back, or elements a failure handler quietly
                // deletes, leave the counter saying one thing and the model saying another.
                var survivors = new FilteredElementCollector(document)
                    .OfClass(typeof(Autodesk.Revit.DB.Wall))
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Where(id => createdIds.Contains(id))
                    .ToList();

                // Select and zoom to them. Nothing else answers "where did they go" as
                // directly, and a plan view will not show what sits outside its crop.
                if (survivors.Count > 0)
                {
                    uiDocument.Selection.SetElementIds(survivors);
                    uiDocument.ShowElements(survivors);
                }

                // Counts are reported against what was found, not just what worked: a
                // conversion that quietly drops a third of the walls looks identical to one
                // that succeeded unless the shortfall is stated.
                var report = new System.Text.StringBuilder();
                report.AppendLine(created + " of " + walls.Count + " walls created, " +
                                  survivors.Count + " present in the model afterwards.");

                if (survivors.Count == 0 && created > 0)
                {
                    report.AppendLine();
                    report.AppendLine("They were created and are no longer there, which means the " +
                                      "transaction rolled back rather than the view hiding them.");
                }
                else if (survivors.Count > 0)
                {
                    report.AppendLine("They are selected and the view has been zoomed to them.");
                }
                report.AppendLine();
                report.AppendLine("Read from " + System.IO.Path.GetFileName(path) + ":");
                report.AppendLine(wallLayers.Count > 0
                    ? "  walls taken from " + string.Join(", ", wallLayers)
                    : "  no wall layer found by name, so every layer was read");
                report.AppendLine("  " + model.Segments.Count + " segments, " + model.Texts.Count + " labels");
                report.AppendLine("  " + plans.Count + " floor plans separated onto their own levels");
                report.AppendLine("  " + spacesFound + " rooms found");
                report.AppendLine("  " + rooms + " placed as Revit rooms");
                report.AppendLine("  placed " + doors + " doors and " + windows + " windows");
                report.AppendLine();
                report.AppendLine("Each plan is placed at the project origin rather than where it " +
                                  "sat on the sheet, and stacked one level above the last.");
                report.AppendLine();
                report.AppendLine("Wall height is " + DefaultWallHeightMm + " mm throughout - a plan " +
                                  "carries no height, so it is assumed until a section is read.");

                if (created < walls.Count)
                {
                    report.AppendLine();
                    report.AppendLine((walls.Count - created) + " walls could not be created - " +
                                      "usually a centreline shorter than Revit will accept.");
                }

                TaskDialog.Show("BINA CAD to BIM", report.ToString());
                return created > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BINA CAD to BIM - Error", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Clears the join complaints that bulk wall creation raises. Warnings are deleted
        /// outright; errors are resolved the way the dialog's own default button would, so the
        /// run continues instead of stopping on the first of a thousand.
        /// </summary>
        private sealed class SilenceJoinFailures : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
                if (failures.Count == 0) return FailureProcessingResult.Continue;

                bool resolved = false;

                foreach (FailureMessageAccessor failure in failures)
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        accessor.DeleteWarning(failure);
                        continue;
                    }

                    if (failure.HasResolutions())
                    {
                        accessor.ResolveFailure(failure);
                        resolved = true;
                    }
                }

                return resolved
                    ? FailureProcessingResult.ProceedWithCommit
                    : FailureProcessingResult.Continue;
            }
        }

        /// <summary>
        /// Places each room the classifier found, bounded by its own outline.
        ///
        /// Revit works rooms out from enclosed wall boundaries, and traced walls are neither
        /// continuous nor joined - they are placed as the drawing drew them, deliberately. Ask
        /// Revit to find rooms among those and it returns a model full of "not enclosed".
        ///
        /// So the boundary the classifier already computed is drawn as room separation lines
        /// and the room placed inside it. The enclosure is then ours rather than a side effect
        /// of wall geometry, and a room appears wherever a loop closed - regardless of how
        /// ragged the walls around it are.
        /// </summary>
        /// <summary>
        /// Places each opening into the wall it belongs to.
        ///
        /// A door needs a host: Revit cuts the opening out of the wall it is placed in, which
        /// is why the classifier's wall-to-opening pairing matters more here than anywhere
        /// else. An opening whose host failed to build is skipped rather than dropped into
        /// space, since a door standing in a room is worse than a door that is missing.
        /// </summary>
        private static void CreateOpenings(
            Document document, List<CadOpening> openings,
            Dictionary<CadWall, Autodesk.Revit.DB.Wall> hosts,
            Level level, double originX, double originY, ref int doors, ref int windows)
        {
            FamilySymbol doorType = FirstSymbol(document, BuiltInCategory.OST_Doors);
            FamilySymbol windowType = FirstSymbol(document, BuiltInCategory.OST_Windows);

            foreach (CadOpening opening in openings)
            {
                FamilySymbol symbol = opening.IsDoor ? doorType : windowType;
                if (symbol == null) continue;

                if (!hosts.TryGetValue(opening.Wall, out Autodesk.Revit.DB.Wall host)) continue;
                if (opening.Width < 300) continue;   // narrower than any real opening

                try
                {
                    if (!symbol.IsActive) symbol.Activate();

                    // A window sits at sill height; a door starts at the floor.
                    double z = level.Elevation + (opening.IsDoor ? 0 : FromMm(WindowSillMm));
                    XYZ where = ToRevit(opening.Position, originX, originY);

                    FamilyInstance placed = document.Create.NewFamilyInstance(
                        new XYZ(where.X, where.Y, z), symbol, host, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    if (placed == null) continue;

                    if (opening.IsDoor) doors++;
                    else windows++;
                }
                catch
                {
                    // A single opening that will not host should not cost the rest.
                }
            }
        }

        /// <summary>Ordinary sill height, in millimetres. The plan does not say - that is a
        /// section - so it is assumed, like the wall height.</summary>
        private const double WindowSillMm = 900.0;

        private static FamilySymbol FirstSymbol(Document document, BuiltInCategory category) =>
            new FilteredElementCollector(document)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(category)
                .Cast<FamilySymbol>()
                .OrderBy(symbol => symbol.Name)
                .FirstOrDefault();

        private static bool IsWallLayer(string layer) =>
            Mentions(layer, "wall", "dinding", "tembok", "partition", "bata");

        private static bool IsWindowLayer(string layer) =>
            Mentions(layer, "win", "tingkap", "glaz");

        private static bool IsOpeningLayer(string layer) =>
            IsWindowLayer(layer) || Mentions(layer, "door", "pintu");

        private static bool Mentions(string layer, params string[] words) =>
            words.Any(word => layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);

        private static int CreateRooms(
            Document document, List<Space> spaces, Level level, double originX, double originY)
        {
            ViewPlan view = new FilteredElementCollector(document)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(plan => !plan.IsTemplate && plan.GenLevel != null &&
                                        plan.GenLevel.Id == level.Id);

            if (view == null) return 0;

            SketchPlane sketch = SketchPlane.Create(
                document, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));

            int placed = 0;

            foreach (Space space in spaces)
            {
                if (space.Boundary.Count < 3) continue;

                try
                {
                    var curves = new CurveArray();
                    for (int i = 0; i < space.Boundary.Count; i++)
                    {
                        XYZ from = ToRevit(space.Boundary[i], originX, originY);
                        XYZ to = ToRevit(space.Boundary[(i + 1) % space.Boundary.Count], originX, originY);

                        if (from.DistanceTo(to) < document.Application.ShortCurveTolerance) continue;
                        curves.Append(Line.CreateBound(from, to));
                    }

                    if (curves.Size < 3) continue;

                    document.Create.NewRoomBoundaryLines(sketch, curves, view);

                    // Placed at the centre of the outline, which for the loops a floor plan
                    // produces is inside them.
                    double x = space.Boundary.Average(point => point.x);
                    double y = space.Boundary.Average(point => point.y);
                    XYZ centre = ToRevit(new Cad2Bim.Point(x, y), originX, originY);

                    Autodesk.Revit.DB.Architecture.Room room =
                        document.Create.NewRoom(level, new UV(centre.X, centre.Y));

                    if (room == null) continue;

                    if (space.Name != null) room.Name = space.Name;
                    placed++;
                }
                catch
                {
                    // A room that will not place should not cost the rest of them.
                }
            }

            return placed;
        }

        private static Autodesk.Revit.DB.Wall CreateWall(
            Document document, CadWall wall, WallType wallType, Level level,
            double originX, double originY)
        {
            CadSegment centerline = wall.Centerline;
            if (centerline.Length < 1.0) return null;   // shorter than a millimetre

            XYZ start = ToRevit(centerline.P1, originX, originY);
            XYZ end = ToRevit(centerline.P2, originX, originY);
            if (start.DistanceTo(end) < document.Application.ShortCurveTolerance) return null;

            Curve curve = Line.CreateBound(start, end);
            double height = FromMm(DefaultWallHeightMm);

            Autodesk.Revit.DB.Wall created = Autodesk.Revit.DB.Wall.Create(
                document, curve, wallType.Id, level.Id, height, 0.0, false, false);

            if (created != null)
            {
                // Traced walls meet at whatever angle the drawing had them; letting Revit
                // resolve those joins produces geometry nobody asked for and a warning for
                // each one. They are placed as drawn and joined later, deliberately, if at all.
                WallUtils.DisallowWallJoinAtEnd(created, 0);
                WallUtils.DisallowWallJoinAtEnd(created, 1);
            }

            return created;
        }

        /// <summary>The classifier works in millimetres; the Revit API works in feet.</summary>
        private static double FromMm(double millimetres) =>
            UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

        private static XYZ ToRevit(Cad2Bim.Point point, double originX, double originY) =>
            new XYZ(FromMm(point.x - originX), FromMm(point.y - originY), 0);

        /// <summary>
        /// Everything on a drawing that is plainly not building fabric. A starting point, not
        /// a substitute for saying which layers hold the walls: layer names vary by
        /// consultant, and this list is the one drawn from the files seen so far.
        /// </summary>
        /// <summary>
        /// Layers whose name says they hold walls. Reading only those is the single largest
        /// improvement available on a drawing that names them: on the test plan it takes the
        /// input from 188,000 segments to 4,000, and the walls that come out sit at a 114 mm
        /// median - JKR brickwork - instead of a smear of furniture and fittings.
        ///
        /// Names are matched in English and Malay, since both turn up in the same set of
        /// drawings. When nothing matches, the caller keeps the broad filter: a drawing that
        /// does not name its layers is not thereby unconvertible.
        /// </summary>
        private static List<string> WallLayers(IEnumerable<string> layers)
        {
            string[] words = { "wall", "dinding", "tembok", "partition", "bata" };

            return layers
                .Where(layer => words.Any(word =>
                    layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(layer => layer, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static LayerFilter BuildFilter()
        {
            var filter = new LayerFilter();
            filter.Exclude.AddRange(new[]
            {
                "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting",
                "*-DIM*", "DEFPOINTS", "G-bubble", "GRID*",
            });
            return filter;
        }

        private static string PickDrawing()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the drawing to convert",
                Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|All files (*.*)|*.*",
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        /// <summary>
        /// The level a given plan belongs on. Existing levels are used in order of height
        /// first - a template usually ships with a couple - and further ones are created above
        /// them as the drawing needs. Storey height is the wall height, since a plan says
        /// nothing about either.
        /// </summary>
        private static Level LevelFor(Document document, int index)
        {
            List<Level> levels = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToList();

            if (index < levels.Count) return levels[index];

            try
            {
                Level created = Level.Create(document, FromMm(DefaultWallHeightMm * index));
                if (created != null) created.Name = "CAD Level " + (index + 1);
                return created;
            }
            catch
            {
                return levels.LastOrDefault();
            }
        }

        private static Level LowestLevel(Document document) =>
            new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .FirstOrDefault();

        private static WallType DefaultWallType(Document document) =>
            new FilteredElementCollector(document)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(type => type.Kind == WallKind.Basic);
    }
}
#endif
