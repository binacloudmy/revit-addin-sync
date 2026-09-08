// Builds native Revit elements from the classifier's output (spec 2026-09-08 §3 "Build").
//
// This is what "CAD to RVT" has to mean. RVT is a closed format that nothing outside
// Revit can write, so the only way to produce one is to create the elements inside a
// running Revit and let the user save. The classification is the same code the
// standalone viewer runs - the Cad2Bim sources carry no Revit dependency, which is what
// makes them usable from both.
//
// Runs only on Revit's main thread, inside CadToBimBuildHandler.Execute. Never throws:
// every failure is a BuildReport with Error set, the group rolled back, and (for a new
// file) the throwaway document closed - so no hidden document is left holding a lock.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Cad2Bim;

// Revit has its own Segment, Wall and Point; the classifier's are always spelled through these.
using CadSegment = Cad2Bim.Segment;
using CadWall = Cad2Bim.Wall;
using CadOpening = Cad2Bim.Opening;
using CadPoint = Cad2Bim.Point;
using RevitWall = Autodesk.Revit.DB.Wall;

namespace RevitWebAppSync.Services.CadToBim
{
    public static class Cad2BimBuilder
    {
        /// <summary>Narrower than any real opening.</summary>
        private const double MinOpeningWidthMm = 300.0;

        /// <summary>One plan cluster and where it goes: the shift that brings its corner to
        /// the origin (zero in KeepPosition mode) and which storey it is.</summary>
        private sealed class Placement
        {
            public PlanCluster Plan;
            public double OriginX;
            public double OriginY;
            public int LevelIndex;
        }

        public static BuildReport Build(UIApplication uiapp, BuildRequest req)
        {
            var report = new BuildReport();
            Stopwatch clock = Stopwatch.StartNew();

            Document doc = null;
            bool isNew = false;
            TransactionGroup group = null;

            try
            {
                if (uiapp == null) throw new ArgumentNullException(nameof(uiapp));
                if (req == null) throw new ArgumentNullException(nameof(req));
                if (req.Walls == null || req.Walls.Count == 0)
                    throw new InvalidOperationException("Nothing to build: every detected wall is erased or already built.");

                (doc, isNew) = ResolveDocument(uiapp, req);

                WallType wallType = DefaultWallType(doc);
                if (wallType == null)
                    throw new InvalidOperationException("This model has no basic wall type to use.");

                Level baseLevel = BaseLevel(doc, req);
                if (baseLevel == null)
                    throw new InvalidOperationException("This model has no level to build on.");

                List<Placement> placements = ClusterAndPlace(req);
                if (placements.Count == 0)
                    throw new InvalidOperationException("No floor plans could be separated out.");

                // Clustering/placement runs over the full layout (LayoutWalls ?? Walls) so plan
                // origins and level indices stay put across Confirms; only the walls the caller
                // actually asked for get created. toBuild is a reference set - Cad2Bim.Wall does
                // not override Equals, and identity is exactly what "already built" means here.
                var toBuild = new HashSet<CadWall>(req.Walls);

                // Which cluster each layout wall belongs to. Openings and rooms follow their
                // walls, so a room cannot span two floors and an opening cannot host itself on
                // the wrong one. Reference identity: Cad2Bim.Wall does not override Equals.
                var owner = new Dictionary<CadWall, Placement>();
                foreach (Placement placement in placements)
                    foreach (CadWall wall in placement.Plan.Walls)
                        owner[wall] = placement;

                List<CadOpening> openings = req.Openings ?? new List<CadOpening>();
                List<Space> spaces = req.Spaces ?? new List<Space>();

                // One undo step for the whole build, however many plans it holds.
                group = new TransactionGroup(doc, "CAD to BIM");
                group.Start();

                var createdIds = new Dictionary<CadWall, ElementId>();

                foreach (Placement placement in placements)
                {
                    Level level;
                    var hosts = new Dictionary<CadWall, RevitWall>();

                    using (var tx = new Transaction(doc, "CAD to BIM: walls"))
                    {
                        tx.Start();
                        Silence(tx);

                        level = LevelFor(doc, baseLevel, placement.LevelIndex, req.HeightMm);
                        if (level == null)
                        {
                            tx.RollBack();
                            report.SkippedWalls += placement.Plan.Walls.Count(toBuild.Contains);
                            continue;
                        }

                        foreach (CadWall wall in placement.Plan.Walls)
                        {
                            if (!toBuild.Contains(wall)) continue;   // built on an earlier Confirm

                            try
                            {
                                RevitWall made = CreateWall(
                                    doc, wall, wallType, level, req.HeightMm,
                                    placement.OriginX, placement.OriginY);

                                if (made != null)
                                {
                                    createdIds[wall] = made.Id;
                                    hosts[wall] = made;
                                }
                                else
                                {
                                    report.SkippedWalls++;
                                }
                            }
                            catch
                            {
                                // One bad centreline should not cost the other thousand.
                                report.SkippedWalls++;
                            }
                        }

                        tx.Commit();
                    }

                    using (var tx = new Transaction(doc, "CAD to BIM: openings and rooms"))
                    {
                        tx.Start();
                        Silence(tx);

                        // A door needs the wall's geometry to exist before it can cut it.
                        doc.Regenerate();

                        List<CadOpening> here = openings
                            .Where(o => o.Wall != null && owner.TryGetValue(o.Wall, out Placement p) && p == placement)
                            .ToList();
                        CreateOpenings(doc, here, hosts, level, placement.OriginX, placement.OriginY, req.WindowSillMm, report);

                        List<Space> rooms = spaces
                            .Where(s => OwnerOf(s, owner) == placement)
                            .ToList();
                        CreateRooms(doc, rooms, level, placement.OriginX, placement.OriginY, report);

                        tx.Commit();
                    }
                }

                group.Assimilate();

                // Ask the model what is actually in it rather than trusting the loop's own
                // tally: a transaction that rolls back, or elements a failure handler quietly
                // deletes, leave the counter saying one thing and the model saying another.
                var present = new HashSet<ElementId>(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitWall))
                        .WhereElementIsNotElementType()
                        .ToElementIds());

                foreach (KeyValuePair<CadWall, ElementId> pair in createdIds)
                    if (present.Contains(pair.Value)) report.WallIds[pair.Key] = ElementIdCompat.ToLong(pair.Value);

                report.Walls = report.WallIds.Count;

                if (isNew)
                {
                    // The pane has already asked about overwriting; here the answer was yes.
                    try
                    {
                        doc.SaveAs(req.OutputPath, new SaveAsOptions { OverwriteExistingFile = true });
                    }
                    catch (Exception ex)
                    {
                        // The generic catch below reports Innermost(ex).Message verbatim; craft
                        // that message here so it names the path and gives the drafter something
                        // to do about it, rather than a bare "The process cannot access the file".
                        throw new InvalidOperationException(
                            "could not save " + req.OutputPath + ": " + Innermost(ex).Message +
                            " — build into the open project instead, or free the file and Confirm again");
                    }
                    report.OutputPath = req.OutputPath;

                    // A document made by NewProjectDocument has no window. Close it and open the
                    // saved file the ordinary way so the drafter is looking at it.
                    doc.Close(false);
                    doc = null;

                    try
                    {
                        uiapp.OpenAndActivateDocument(req.OutputPath);
                    }
                    catch (Exception ex)
                    {
                        // Saved and closed cleanly; only the activation failed (Revit refuses
                        // while another document is mid-edit). The path is in the report.
                        Debug.WriteLine("[CadToBim] saved but could not activate " + req.OutputPath + ": " + ex.Message);
                    }
                }
                else
                {
                    report.OutputPath = doc.PathName;
                }
            }
            catch (Exception ex)
            {
                if (group != null && group.HasStarted())
                {
                    try { group.RollBack(); } catch { /* already closed by a failed transaction */ }
                }

                report.Error = Innermost(ex).Message;
                report.WallIds.Clear();
                report.Walls = 0;

                if (isNew && doc != null)
                {
                    // Nothing saved: drop the throwaway document so it holds no lock and
                    // shows up in no window list.
                    try { doc.Close(false); } catch { /* never opened properly */ }
                }
            }
            finally
            {
                group?.Dispose();
                report.Elapsed = clock.Elapsed;
            }

            return report;
        }

        // ─── document + level ────────────────────────────────────────────

        /// <summary>The document to build into. AddToProject = the active one; NewFile = a
        /// fresh project from the template, the metric default when the template is
        /// missing (same fallback as DwgScratchCache.TryOpenScratch).</summary>
        private static (Document doc, bool isNew) ResolveDocument(UIApplication uiapp, BuildRequest req)
        {
            if (req.Target == BuildTarget.AddToProject)
            {
                Document active = uiapp.ActiveUIDocument?.Document;
                if (active == null) throw new InvalidOperationException("Open a project first.");
                if (active.IsFamilyDocument) throw new InvalidOperationException("Open a project, not a family.");
                return (active, false);
            }

            if (string.IsNullOrEmpty(req.OutputPath))
                throw new InvalidOperationException("No output path for the new file.");

            Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;
            string template = string.IsNullOrEmpty(req.TemplatePath) ? app.DefaultProjectTemplate : req.TemplatePath;

            Document created = !string.IsNullOrEmpty(template) && File.Exists(template)
                ? app.NewProjectDocument(template)
                : app.NewProjectDocument(UnitSystem.Metric);

            if (created == null) throw new InvalidOperationException("Revit could not create a new project.");
            return (created, true);
        }

        /// <summary>The level the first plan goes on: the drafter's pick when it still
        /// resolves, else the lowest level in the document.</summary>
        private static Level BaseLevel(Document doc, BuildRequest req)
        {
            if (req.Target == BuildTarget.AddToProject && req.LevelId.HasValue && req.LevelId.Value > 0)
            {
                ElementId picked = ElementIdCompat.ToElementId(req.LevelId.Value);
                if (doc.GetElement(picked) is Level level) return level;
            }

            return LowestLevel(doc);
        }

        /// <summary>
        /// The level a given plan belongs on. Existing levels are used in order of height
        /// first - a template usually ships with a couple - and further ones are created above
        /// them as the drawing needs. Storey height is the wall height, since a plan says
        /// nothing about either. Counted from the base level, so "Add to project" on Level 2
        /// stacks upward from Level 2.
        /// </summary>
        private static Level LevelFor(Document doc, Level baseLevel, int index, double heightMm)
        {
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToList();

            int baseIndex = levels.FindIndex(level => level.Id == baseLevel.Id);
            if (baseIndex < 0) baseIndex = 0;

            int target = baseIndex + index;
            if (target < levels.Count) return levels[target];

            // A second Confirm (or a second build in the same session) asks for the same index
            // again; the level this build wants already exists under the name the first build
            // gave it, and Level.Create + rename would throw "name already in use". Reuse it.
            string wantedName = "CAD Level " + (index + 1);
            Level existing = levels.FirstOrDefault(level =>
                string.Equals(level.Name, wantedName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            try
            {
                Level created = Level.Create(doc, baseLevel.Elevation + FromMm(heightMm * index));
                if (created == null) return levels.LastOrDefault();

                try
                {
                    created.Name = wantedName;
                }
                catch
                {
                    // The name is taken by a level this pass did not find above (created earlier
                    // in this same build, or unrelated to CAD to BIM entirely). Keep the new
                    // level - it still occupies the right elevation - under a name Revit accepts.
                    for (int suffix = 2; suffix < 100; suffix++)
                    {
                        try { created.Name = wantedName + " (" + suffix + ")"; break; }
                        catch { /* still taken; try the next suffix */ }
                    }
                }

                return created;
            }
            catch
            {
                return levels.LastOrDefault();
            }
        }

        private static Level LowestLevel(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .FirstOrDefault();

        private static WallType DefaultWallType(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(type => type.Kind == WallKind.Basic);

        // ─── clustering ──────────────────────────────────────────────────

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
        private static List<Placement> ClusterAndPlace(BuildRequest req)
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

        /// <summary>The cluster a room belongs to: that of the first of its walls that is
        /// being built now. A room none of whose walls are pending was built last time.</summary>
        private static Placement OwnerOf(Space space, Dictionary<CadWall, Placement> owner)
        {
            foreach (CadWall wall in space.SubElements.OfType<CadWall>())
                if (owner.TryGetValue(wall, out Placement placement)) return placement;
            return null;
        }

        // ─── element creation ────────────────────────────────────────────

        /// <summary>
        /// Revit tries to join every new wall to whatever it touches, and traced walls touch
        /// constantly. Each failed join raises a dialog, so a thousand walls becomes a
        /// thousand interruptions; the joins are not wanted anyway.
        /// </summary>
        private static void Silence(Transaction tx)
        {
            FailureHandlingOptions options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new SilenceJoinFailures());
            options.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(options);
        }

        private static RevitWall CreateWall(
            Document doc, CadWall wall, WallType wallType, Level level, double heightMm,
            double originX, double originY)
        {
            CadSegment centerline = wall.Centerline;
            if (centerline.Length < 1.0) return null;   // shorter than a millimetre

            XYZ start = ToRevit(centerline.P1, originX, originY);
            XYZ end = ToRevit(centerline.P2, originX, originY);
            if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance) return null;

            Curve curve = Line.CreateBound(start, end);
            double height = FromMm(heightMm);

            RevitWall created = RevitWall.Create(
                doc, curve, wallType.Id, level.Id, height, 0.0, false, false);

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

        /// <summary>
        /// Places each opening into the wall it belongs to.
        ///
        /// A door needs a host: Revit cuts the opening out of the wall it is placed in, which
        /// is why the classifier's wall-to-opening pairing matters more here than anywhere
        /// else. An opening whose host failed to build is skipped rather than dropped into
        /// space, since a door standing in a room is worse than a door that is missing.
        /// </summary>
        private static void CreateOpenings(
            Document doc, List<CadOpening> openings,
            Dictionary<CadWall, RevitWall> hosts,
            Level level, double originX, double originY, double windowSillMm, BuildReport report)
        {
            FamilySymbol doorType = FirstSymbol(doc, BuiltInCategory.OST_Doors);
            FamilySymbol windowType = FirstSymbol(doc, BuiltInCategory.OST_Windows);

            foreach (CadOpening opening in openings)
            {
                FamilySymbol symbol = opening.IsDoor ? doorType : windowType;
                if (symbol == null) { report.SkippedOpenings++; continue; }   // template has no family

                if (!hosts.TryGetValue(opening.Wall, out RevitWall host)) { report.SkippedOpenings++; continue; }
                if (opening.Width < MinOpeningWidthMm) { report.SkippedOpenings++; continue; }

                try
                {
                    if (!symbol.IsActive) symbol.Activate();

                    // A window sits at sill height; a door starts at the floor.
                    double z = level.Elevation + (opening.IsDoor ? 0 : FromMm(windowSillMm));
                    XYZ where = ToRevit(opening.Position, originX, originY);

                    FamilyInstance placed = doc.Create.NewFamilyInstance(
                        new XYZ(where.X, where.Y, z), symbol, host, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    if (placed == null) { report.SkippedOpenings++; continue; }

                    if (opening.IsDoor) report.Doors++;
                    else report.Windows++;
                }
                catch
                {
                    // A single opening that will not host should not cost the rest.
                    report.SkippedOpenings++;
                }
            }
        }

        private static FamilySymbol FirstSymbol(Document doc, BuiltInCategory category) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(category)
                .Cast<FamilySymbol>()
                .OrderBy(symbol => symbol.Name)
                .FirstOrDefault();

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
        private static void CreateRooms(
            Document doc, List<Space> spaces, Level level, double originX, double originY, BuildReport report)
        {
            if (spaces.Count == 0) return;

            ViewPlan view = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(plan => !plan.IsTemplate && plan.GenLevel != null &&
                                        plan.GenLevel.Id == level.Id);

            // Room separation lines are view-hosted; a level with no plan view (one this build
            // just created) gets its walls and openings but no rooms - counted, not silent.
            if (view == null)
            {
                report.SkippedRooms += spaces.Count;
                return;
            }

            SketchPlane sketch = SketchPlane.Create(
                doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));

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

                        if (from.DistanceTo(to) < doc.Application.ShortCurveTolerance) continue;
                        curves.Append(Line.CreateBound(from, to));
                    }

                    if (curves.Size < 3) continue;

                    doc.Create.NewRoomBoundaryLines(sketch, curves, view);

                    // Placed at the centre of the outline, which for the loops a floor plan
                    // produces is inside them.
                    double x = space.Boundary.Average(point => point.x);
                    double y = space.Boundary.Average(point => point.y);
                    XYZ centre = ToRevit(new CadPoint(x, y), originX, originY);

                    Autodesk.Revit.DB.Architecture.Room room =
                        doc.Create.NewRoom(level, new UV(centre.X, centre.Y));

                    if (room == null) continue;

                    if (space.Name != null) room.Name = space.Name;
                    report.Rooms++;
                }
                catch
                {
                    // A room that will not place should not cost the rest of them.
                }
            }
        }

        // ─── units + errors ──────────────────────────────────────────────

        /// <summary>The classifier works in millimetres; the Revit API works in feet.
        /// UnitTypeId exists from Revit 2021, so this is the same call on 2023-2027.</summary>
        private static double FromMm(double millimetres) =>
            UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

        private static XYZ ToRevit(CadPoint point, double originX, double originY) =>
            new XYZ(FromMm(point.x - originX), FromMm(point.y - originY), 0);

        /// <summary>The message the drafter can act on is the innermost one; the wrappers
        /// above it say "Execution failed" and nothing else.</summary>
        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
