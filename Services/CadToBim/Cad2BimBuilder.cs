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
using System.Globalization;
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
        private const double MinOpeningWidthMm = BuildSizing.MinOpeningWidthMm;

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
            var report = new BuildReport { Kind = BuildJobKind.Build, Target = req?.Target ?? BuildTarget.AddToProject };
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

                // The pane's "already built" list goes stale when the drafter presses Ctrl+Z.
                // Check it here, on the Revit thread, before anything is created: if walls it
                // thinks are built are gone, stop and let the pane put them back in its list.
                if (!isNew)
                {
                    report.MissingIds.AddRange(Cad2BimModelOps.Missing(doc, req.BuiltWallIds));
                    if (report.MissingIds.Count > 0)
                    {
                        report.Stale = true;
                        return report;
                    }
                }

                WallType fallbackWallType = DefaultWallType(doc);
                if (fallbackWallType == null)
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

                // Every door, window and room placed, at its drawing position: one Revit deletes
                // when a transaction commits is reported as skipped, with Revit's reason, rather
                // than silently dropped from the counts. openingNotes describes each opening's
                // geometry for the log line written when one goes.
                var ledger = new PlacedLedger();
                var openingNotes = new Dictionary<long, string>();
                var removedLines = new List<string>();
                bool Present(long id) => doc.GetElement(ElementIdCompat.ToElementId(id)) != null;
                void Removed(long id, BuildCategory category, string revitText)
                {
                    if (category == BuildCategory.Room) return;
                    openingNotes.TryGetValue(id, out string note);
                    removedLines.Add((note ?? (category == BuildCategory.Door ? "door" : "window") + " ?") +
                                     " | Revit: " + (string.IsNullOrEmpty(revitText) ? "no failure text noted" : revitText));
                }

                // One type per drawn size, found or made once and reused across plans. A type
                // the drafter picked in the Build step's TYPES section wins over both.
                var wallTypes = new WallTypeCache(doc, fallbackWallType, req.TypeChoices, report);
                var openingTypes = new OpeningTypeCache(doc, req.TypeChoices, report);

                foreach (Placement placement in placements)
                {
                    Level level;
                    var hosts = new Dictionary<CadWall, RevitWall>();

                    List<CadOpening> here = openings
                        .Where(o => o.Wall != null && owner.TryGetValue(o.Wall, out Placement p) && p == placement)
                        .ToList();

                    // Where each wall is actually drawn from: ends met at corners, hosts lengthened
                    // over their openings. Worked out on the whole plan, so a wall added on a later
                    // Confirm still meets the ones built before it.
                    List<CadWall> planWalls = placement.Plan.Walls;
                    List<CadSegment> drawn = BuildSizing.CoverOpenings(
                        planWalls, BuildSizing.MeetAtCorners(planWalls, BuildSizing.CornerReachMm), here);

                    using (var tx = new Transaction(doc, "CAD to BIM: walls"))
                    {
                        tx.Start();
                        Silence(tx);

                        level = LevelFor(doc, baseLevel, placement.LevelIndex, req.HeightMm, report);
                        if (level == null)
                        {
                            tx.RollBack();
                            foreach (CadWall wall in placement.Plan.Walls.Where(toBuild.Contains))
                                SkipWall(report, wall, SkipReason.NoLevel);
                            continue;
                        }

                        for (int i = 0; i < planWalls.Count; i++)
                        {
                            CadWall wall = planWalls[i];
                            if (!toBuild.Contains(wall)) continue;   // built on an earlier Confirm

                            try
                            {
                                RevitWall made = CreateWall(
                                    doc, drawn[i], wallTypes.For(wall.Thickness), level, req.HeightMm,
                                    placement.OriginX, placement.OriginY);

                                if (made != null)
                                {
                                    createdIds[wall] = made.Id;
                                    hosts[wall] = made;
                                }
                                else
                                {
                                    SkipWall(report, wall, SkipReason.TooShort);
                                }
                            }
                            catch
                            {
                                // One bad centreline should not cost the other thousand.
                                SkipWall(report, wall, SkipReason.RevitRefused);
                            }
                        }

                        if (hosts.Count > 0 && !report.LevelNames.Contains(level.Name)) report.LevelNames.Add(level.Name);
                        tx.Commit();
                    }

                    if (req.JoinWalls) JoinWalls(doc, hosts.Values, report);

                    using (var tx = new Transaction(doc, "CAD to BIM: openings and rooms"))
                    {
                        tx.Start();
                        Silence(tx, ledger);

                        // A door needs the wall's geometry to exist - joins included - before it
                        // can cut it.
                        doc.Regenerate();

                        var drawnOf = new Dictionary<CadWall, CadSegment>();
                        for (int i = 0; i < planWalls.Count; i++) drawnOf[planWalls[i]] = drawn[i];

                        CreateOpenings(doc, here, hosts, drawnOf, level, placement.OriginX, placement.OriginY, req,
                            openingTypes, report, ledger, openingNotes);

                        List<Space> rooms = spaces
                            .Where(s => OwnerOf(s, owner) == placement)
                            .ToList();
                        CreateRooms(doc, rooms, level, placement.OriginX, placement.OriginY, report, ledger);

                        tx.Commit();
                    }

                    // An opening Revit could not cut is deleted by the failure resolution as the
                    // transaction commits; list it now, with Revit's reason.
                    ledger.Settle(report, Present, Removed);
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
                {
                    if (present.Contains(pair.Value)) report.WallIds[pair.Key] = ElementIdCompat.ToLong(pair.Value);
                    else SkipWall(report, pair.Key, SkipReason.RevitRefused);   // a failure handler removed it
                }

                report.Walls = report.WallIds.Count;

                // Same check for everything else: only ids still in the model are reported, so
                // "Select in model" and "Undo this build" never reach for a ghost. Doors, windows
                // and rooms that went are listed as skipped (already done after each commit;
                // this catches anything Assimilate took).
                ledger.Settle(report, Present, Removed);
                WriteLog("removed after placing, " + removedLines.Count + " opening(s):", removedLines);
                KeepPresent(doc, report.HelperIds);
                KeepPresent(doc, report.NewViewIds);
                KeepPresent(doc, report.NewLevelIds);
                report.Doors = report.DoorIds.Count;
                report.Windows = report.WindowIds.Count;
                report.Rooms = report.RoomIds.Count;
                report.DocumentKey = Cad2BimModelOps.KeyOf(doc);

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
                    report.DocumentKey = req.OutputPath;

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
                // Rolled back: nothing below is in the model any more.
                report.DoorIds.Clear();
                report.WindowIds.Clear();
                report.RoomIds.Clear();
                report.HelperIds.Clear();
                report.NewViewIds.Clear();
                report.NewLevelIds.Clear();
                report.NewLevelNames.Clear();
                report.NewWallTypes = 0;
                report.NewOpeningTypes = 0;
                report.DoorsNotTurned = 0;
                report.NewWallTypeNames.Clear();
                report.NewOpeningTypeNames.Clear();

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
        private static Level LevelFor(Document doc, Level baseLevel, int index, double heightMm, BuildReport report)
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

                report.NewLevelIds.Add(ElementIdCompat.ToLong(created.Id));
                report.NewLevelNames.Add(created.Name);

                // Room separation lines are view-hosted, so a level without a floor plan gets
                // no rooms. Made after the rename so the view takes the level's final name.
                ViewPlan plan = CreateFloorPlan(doc, created);
                if (plan != null) report.NewViewIds.Add(ElementIdCompat.ToLong(plan.Id));

                return created;
            }
            catch
            {
                return levels.LastOrDefault();
            }
        }

        /// <summary>A floor plan view for a level this build made. Null when the model has no
        /// floor plan view type or Revit refuses; rooms on that level are then skipped with
        /// the NoPlanView reason.</summary>
        private static ViewPlan CreateFloorPlan(Document doc, Level level)
        {
            try
            {
                ViewFamilyType type = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
                return type == null ? null : ViewPlan.Create(doc, type.Id, level.Id);
            }
            catch
            {
                return null;
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
        /// Revit raises a failure for every join it cannot make cleanly, and traced walls touch
        /// constantly. Each one is a dialog, so a thousand walls becomes a thousand
        /// interruptions; SilenceJoinFailures clears them so the build runs through.
        /// </summary>
        /// <param name="ledger">When given, errors are also noted against the elements they
        /// name (RecordingFailures), so a door or window Revit deletes can be reported with
        /// Revit's reason. Resolution is the same either way.</param>
        private static void Silence(Transaction tx, PlacedLedger ledger = null)
        {
            FailureHandlingOptions options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(ledger == null
                ? new SilenceJoinFailures()
                : (IFailuresPreprocessor)new RecordingFailures(ledger));
            options.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(options);
        }

        private static RevitWall CreateWall(
            Document doc, CadSegment centerline, WallType wallType, Level level, double heightMm,
            double originX, double originY)
        {
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
                // Not joined while the walls go in: each would join against a half-built plan,
                // and a join that fails would take the walls transaction with it. JoinWalls
                // allows the joins afterwards, in a step of its own.
                WallUtils.DisallowWallJoinAtEnd(created, 0);
                WallUtils.DisallowWallJoinAtEnd(created, 1);
            }

            return created;
        }

        /// <summary>
        /// Lets Revit join the walls just built, at both ends. Runs after every wall of the
        /// plan exists - ends were already moved to meet at corners (BuildSizing.MeetAtCorners),
        /// so Revit finds a clean corner or tee - and before any door or window is placed, so
        /// openings cut the joined geometry rather than having it change under them.
        ///
        /// A transaction of its own: if Revit refuses the joins, it rolls back alone and the
        /// walls stay as they were, unjoined, with the openings still to come.
        /// </summary>
        private static void JoinWalls(Document doc, IEnumerable<RevitWall> walls, BuildReport report)
        {
            List<RevitWall> built = walls.Where(wall => wall != null && wall.IsValidObject).ToList();
            if (built.Count == 0) return;

            try
            {
                using (var tx = new Transaction(doc, "CAD to BIM: wall joins"))
                {
                    tx.Start();
                    Silence(tx);

                    foreach (RevitWall wall in built)
                    {
                        try
                        {
                            WallUtils.AllowWallJoinAtEnd(wall, 0);
                            WallUtils.AllowWallJoinAtEnd(wall, 1);
                        }
                        catch
                        {
                            // One wall that will not join stays as drawn.
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed) report.JoinsSkipped = true;
                }
            }
            catch (Exception ex)
            {
                report.JoinsSkipped = true;
                Debug.WriteLine("[CadToBim] wall joins rolled back: " + ex.Message);
            }
        }

        /// <summary>
        /// Places each opening into the wall it belongs to.
        ///
        /// A door needs a host: Revit cuts the opening out of the wall it is placed in, which
        /// is why the classifier's wall-to-opening pairing matters more here than anywhere
        /// else. An opening whose host failed to build is skipped rather than dropped into
        /// space, since a door standing in a room is worse than a door that is missing.
        ///
        /// Each gets a type of its drawn width and its kind's height (OpeningTypeCache), and
        /// a door goes at the middle of its leaf rather than on the hinge.
        /// </summary>
        private static void CreateOpenings(
            Document doc, List<CadOpening> openings,
            Dictionary<CadWall, RevitWall> hosts, Dictionary<CadWall, CadSegment> drawnOf,
            Level level, double originX, double originY, BuildRequest req,
            OpeningTypeCache types, BuildReport report, PlacedLedger ledger, Dictionary<long, string> notes)
        {
            // Doors to turn to their drawn swing once all are placed (one regenerate for all).
            var toTurn = new List<(FamilyInstance Door, DoorSwing Swing)>();

            foreach (CadOpening opening in openings)
            {
                BuildCategory category = opening.IsDoor ? BuildCategory.Door : BuildCategory.Window;
                double x = opening.Position.x, y = opening.Position.y;

                if (!hosts.TryGetValue(opening.Wall, out RevitWall host)) { report.Skip(category, SkipReason.HostNotBuilt, x, y); continue; }
                // A failure resolution in the joins step can delete a wall.
                if (!host.IsValidObject) { report.Skip(category, SkipReason.HostNotBuilt, x, y); continue; }

                double widthMm = BuildSizing.OpeningWidthMm(opening);
                if (widthMm < MinOpeningWidthMm) { report.Skip(category, SkipReason.TooNarrow, x, y); continue; }

                OpeningKind kind = BuildSizing.KindOf(opening);
                (double w, double h) = BuildSizing.OpeningSize(kind, widthMm, req.DoorHeightMm, req.WindowHeightMm);

                try
                {
                    FamilySymbol symbol = types.For(kind, w, h, out bool sized);
                    if (symbol == null) { report.Skip(category, SkipReason.NoFamily, x, y); continue; }
                    if (!symbol.IsActive) symbol.Activate();

                    // A window sits at sill height; a door starts at the floor.
                    double sill = kind == OpeningKind.Window ? FromMm(req.WindowSillMm) : 0;
                    XYZ where = ToRevit(BuildSizing.OpeningCentre(opening), originX, originY);

                    FamilyInstance placed = doc.Create.NewFamilyInstance(
                        new XYZ(where.X, where.Y, level.Elevation + sill), symbol, host, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    if (placed == null) { report.Skip(category, SkipReason.RevitRefused, x, y); continue; }

                    // The point's height is not always honoured for hosted windows; the sill
                    // parameter is.
                    if (kind == OpeningKind.Window) TrySet(placed.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM), sill);

                    // A family whose size lives on the instance is sized here instead.
                    if (!sized && !SetSize(placed, kind, w, h)) report.UnsizedOpenings++;

                    // Counts are taken from these lists once the group is assimilated.
                    long placedId = ElementIdCompat.ToLong(placed.Id);
                    if (kind == OpeningKind.Door) report.DoorIds.Add(placedId);
                    else report.WindowIds.Add(placedId);

                    // Same point a skip of it would be marked at. The note is only read if
                    // Revit removes the opening again.
                    ledger.Add(placedId, category, x, y);
                    drawnOf.TryGetValue(opening.Wall, out CadSegment drawnHost);
                    notes[placedId] = OpeningNote(
                        kind == OpeningKind.Door ? "door" : "window", placed.Symbol, w, h, widthMm, opening,
                        drawnHost, SafeWidthMm(host));

                    // Each leaf of a double door is its own instance on its own leaf, turned
                    // to its own arc. A door with no clear arc is logged and left as placed.
                    if (kind == OpeningKind.Door) toTurn.Add((placed, BuildSizing.SwingOf(opening)));
                }
                catch
                {
                    // A single opening that will not host should not cost the rest.
                    report.Skip(category, SkipReason.RevitRefused, x, y);
                }
            }

            TurnDoors(doc, toTurn, report);
        }

        /// <summary>
        /// Turns each placed door so it opens into the side the drawing's arc is on, hinged
        /// at the arc's centre. Revit places a hosted door by the wall's own sides, whatever
        /// the drawing says. FacingOrientation / HandOrientation are only valid after a
        /// regenerate, so: place all doors, regenerate, read each door's facing and hand
        /// once and decide both flips from that read, apply them, regenerate once more, and
        /// check each door once. Which way a family's leaf and hinge lie for those vectors is
        /// BuildSizing.SwingAlongFacing / HingeAlongHand (one place, with the reasoning).
        /// A refused flip or a failed final check leaves the door as it ends up and counts it
        /// in DoorsNotTurned; it never fails the build. One line per door goes to
        /// cad-to-bim.log (BuildSizing.DoorTurnLine) so one Revit build settles the
        /// convention.
        /// </summary>
        private static void TurnDoors(Document doc, List<(FamilyInstance Door, DoorSwing Swing)> doors, BuildReport report)
        {
            if (doors.Count == 0) return;
            var lines = new List<string>();
            int pending = doors.Count;   // doors whose outcome is not settled yet
            try
            {
                doc.Regenerate();

                var applied = new List<(FamilyInstance Door, DoorSwing Swing, bool Refused, string Line)>();
                foreach ((FamilyInstance door, DoorSwing swing) in doors)
                {
                    string type = null;
                    try
                    {
                        if (!door.IsValidObject) continue;   // a failure resolution removed it; counted elsewhere
                        type = door.Symbol?.FamilyName + ": " + door.Symbol?.Name;
                        XYZ facing = door.FacingOrientation, hand = door.HandOrientation;

                        if (!swing.Known)
                        {
                            lines.Add(BuildSizing.DoorTurnLine(type, facing.X, facing.Y, hand.X, hand.Y, swing, false, false,
                                "left as placed: " + swing.Why));
                            continue;
                        }

                        (bool flipFacing, bool flipHand) = BuildSizing.DoorFlips(swing, facing.X, facing.Y, hand.X, hand.Y);
                        bool refused = false;
                        if (flipFacing) refused |= !door.CanFlipFacing || !door.flipFacing();
                        if (flipHand) refused |= !door.CanFlipHand || !door.flipHand();

                        applied.Add((door, swing, refused,
                            BuildSizing.DoorTurnLine(type, facing.X, facing.Y, hand.X, hand.Y, swing, flipFacing, flipHand, "{0}")));
                    }
                    catch (Exception ex)
                    {
                        report.DoorsNotTurned++;
                        lines.Add("door " + (type ?? "?") + " | not turned: " + ex.Message);
                    }
                }

                pending = applied.Count;   // flipped (or not needing it), not yet checked
                doc.Regenerate();

                foreach ((FamilyInstance door, DoorSwing swing, bool refused, string line) in applied)
                {
                    string result;
                    try
                    {
                        (bool facingLeft, bool handLeft) = door.IsValidObject
                            ? BuildSizing.DoorFlips(swing, door.FacingOrientation.X, door.FacingOrientation.Y,
                                                    door.HandOrientation.X, door.HandOrientation.Y)
                            : (false, false);
                        bool ok = !refused && !facingLeft && !handLeft;
                        if (!ok) report.DoorsNotTurned++;
                        result = ok ? "matches drawing"
                            : refused ? "NOT turned: Revit refused a flip"
                            : "NOT turned: still " + (facingLeft ? "facing" : "") + (facingLeft && handLeft ? "+" : "") + (handLeft ? "hand" : "") + " wrong after flipping";
                    }
                    catch (Exception ex)
                    {
                        report.DoorsNotTurned++;
                        result = "NOT turned: " + ex.Message;
                    }
                    lines.Add(line.Replace("{0}", result));
                }
                pending = 0;
            }
            catch (Exception ex)
            {
                // A regenerate failed: whatever was not checked counts as not turned.
                report.DoorsNotTurned += pending;
                lines.Add("door turning stopped: " + ex.Message);
            }

            WriteLog("door swing, " + lines.Count + " door(s):", lines);
        }

        /// <summary>
        /// One opening's geometry, for the log line written if Revit removes it: kind, type and
        /// size, drawing position, and against the host as drawn its length, where the opening's
        /// centre falls along it, the drawn width, the host's thickness in Revit, and how much
        /// wall is left past each jamb (negative = the opening runs off the wall). Never throws.
        /// </summary>
        private static string OpeningNote(
            string kind, FamilySymbol symbol, double typeWidthMm, double typeHeightMm, double drawnWidthMm,
            CadOpening opening, CadSegment host, double hostThicknessMm)
        {
            string M(double v) => double.IsNaN(v) ? "?" : Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
            try
            {
                string type = symbol == null ? "?" : symbol.FamilyName + ": " + symbol.Name;
                string line = kind + " " + type + " (" + M(typeWidthMm) + " x " + M(typeHeightMm) + " mm)" +
                              " at (" + M(opening.Position.x) + "," + M(opening.Position.y) + ")";
                if (host == null || host.Length <= 0) return line + " | host as drawn unknown";

                CadPoint centre = BuildSizing.OpeningCentre(opening);
                double len = host.Length;
                double ux = (host.P2.x - host.P1.x) / len, uy = (host.P2.y - host.P1.y) / len;
                double dx = centre.x - host.P1.x, dy = centre.y - host.P1.y;
                double along = dx * ux + dy * uy;
                double off = Math.Abs(dx * uy - dy * ux);
                double half = typeWidthMm / 2;

                return line +
                       " | host " + M(len) + " mm long, " + M(hostThicknessMm) + " mm thick" +
                       " | centre " + M(along) + " mm along it, " + M(off) + " mm off its line" +
                       " | drawn width " + M(drawnWidthMm) + " mm" +
                       " | wall past each jamb " + M(along - half) + " / " + M(len - along - half) + " mm";
            }
            catch (Exception ex)
            {
                return kind + " (geometry not readable: " + ex.Message + ")";
            }
        }

        /// <summary>A wall's thickness in mm, NaN when Revit will not say.</summary>
        private static double SafeWidthMm(RevitWall wall)
        {
            try { return ToMm(wall.Width); }
            catch { return double.NaN; }
        }

        /// <summary>Appends to %LOCALAPPDATA%\Bina\RevitSync\logs\cad-to-bim.log, the file the
        /// pane already logs to, under one stamped header line. A log that cannot be written
        /// never touches the build.</summary>
        private static void WriteLog(string header, List<string> lines)
        {
            if (lines.Count == 0) return;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "logs");
                Directory.CreateDirectory(dir);
                string stamp = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] ";
                File.AppendAllText(Path.Combine(dir, "cad-to-bim.log"),
                    stamp + header + Environment.NewLine +
                    string.Join(Environment.NewLine, lines.Select(l => "  " + l)) + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }

        // ─── types by size ───────────────────────────────────────────────

        /// <summary>
        /// The wall type for each drawn thickness: a basic type of that width when the model
        /// has one, else a new "CAD Wall N mm" duplicated from the closest one and given a
        /// single structural layer of exactly that width. One lookup per width per build.
        /// </summary>
        private sealed class WallTypeCache
        {
            private readonly Document _doc;
            private readonly WallType _fallback;
            private readonly BuildReport _report;
            private readonly List<TypeChoice> _choices;
            private readonly Dictionary<double, WallType> _byWidth = new Dictionary<double, WallType>();
            private List<WallType> _basic;

            public WallTypeCache(Document doc, WallType fallback, List<TypeChoice> choices, BuildReport report)
            {
                _doc = doc;
                _fallback = fallback;
                _choices = choices;
                _report = report;
            }

            /// <summary>Call inside an open transaction: it may create a type.</summary>
            public WallType For(double thicknessMm)
            {
                double width = BuildSizing.WallWidthMm(thicknessMm);
                if (width <= 0) return _fallback;
                if (_byWidth.TryGetValue(width, out WallType known)) return known;

                // The drafter's pick, used as it is. Gone since the pane read the types: fall
                // through to the builder's own choice.
                long picked = TypeChoice.Find(_choices, TypeSlot.Wall, width, double.NaN);
                if (picked > 0 && _doc.GetElement(ElementIdCompat.ToElementId(picked)) is WallType pickedType)
                {
                    _byWidth[width] = pickedType;
                    return pickedType;
                }

                _basic ??= BasicWallTypes(_doc);

                var candidates = _basic
                    .Select(type => new SizeCandidate(type.Name, ToMm(type.Width), double.NaN, true))
                    .ToList();

                SizeChoice choice = BuildSizing.Pick(candidates, width, double.NaN, 0.5);
                WallType chosen = _fallback;

                if (choice.Found && choice.Exact)
                {
                    chosen = _basic[choice.Index];
                }
                else
                {
                    WallType source = choice.Found ? _basic[choice.Index] : _fallback;
                    WallType made = Make(source, width);
                    if (made != null)
                    {
                        chosen = made;
                        _basic.Add(made);
                        _report.NewWallTypes++;
                        _report.NewWallTypeNames.Add(made.Name);
                    }
                }

                _byWidth[width] = chosen;
                return chosen;
            }

            private WallType Make(WallType source, double widthMm)
            {
                try
                {
                    var taken = new HashSet<string>(
                        new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Select(type => type.Name));
                    string name = BuildSizing.UniqueName(BuildSizing.WallTypeName(widthMm), taken);

                    if (!(source.Duplicate(name) is WallType made)) return null;

                    // One layer of the drawn width, in the material of the source's core.
                    CompoundStructure structure = source.GetCompoundStructure();
                    ElementId material = ElementId.InvalidElementId;
                    if (structure != null && structure.LayerCount > 0)
                    {
                        int core = Math.Max(0, structure.GetFirstCoreLayerIndex());
                        material = structure.GetLayers()[Math.Min(core, structure.LayerCount - 1)].MaterialId;
                    }

                    made.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(
                        MaterialFunctionAssignment.Structure, FromMm(widthMm), material));
                    return made;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[CadToBim] could not make a " + widthMm + " mm wall type: " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// The door or window type for each drawn size. A type already that size (within half
        /// a rounding step) is used as it is; otherwise the closest resizable type is
        /// duplicated as "CAD W x H mm" and resized. When nothing in the model can be resized,
        /// the closest type is used and the instance sized if it can be.
        /// </summary>
        private sealed class OpeningTypeCache
        {
            private readonly Document _doc;
            private readonly BuildReport _report;
            private readonly Dictionary<(OpeningKind, double, double), (FamilySymbol Symbol, bool Sized)> _bySize =
                new Dictionary<(OpeningKind, double, double), (FamilySymbol, bool)>();
            private readonly Dictionary<OpeningKind, List<FamilySymbol>> _symbols = new Dictionary<OpeningKind, List<FamilySymbol>>();
            private readonly List<TypeChoice> _choices;

            public OpeningTypeCache(Document doc, List<TypeChoice> choices, BuildReport report)
            {
                _doc = doc;
                _choices = choices;
                _report = report;
            }

            /// <summary>Call inside an open transaction: it may create a type. null = the model
            /// has no family of this kind at all. sized = the type is the wanted size, or the
            /// drafter picked it (used as it is, never resized).</summary>
            public FamilySymbol For(OpeningKind kind, double widthMm, double heightMm, out bool sized)
            {
                if (_bySize.TryGetValue((kind, widthMm, heightMm), out var known))
                {
                    sized = known.Sized;
                    return known.Symbol;
                }

                TypeSlot slot = kind == OpeningKind.Door ? TypeSlot.Door : TypeSlot.Window;
                long picked = TypeChoice.Find(_choices, slot, widthMm, heightMm);
                if (picked > 0 && _doc.GetElement(ElementIdCompat.ToElementId(picked)) is FamilySymbol pickedSymbol)
                {
                    sized = true;
                    _bySize[(kind, widthMm, heightMm)] = (pickedSymbol, true);
                    return pickedSymbol;
                }

                List<FamilySymbol> symbols = Symbols(kind);
                var candidates = symbols.Select(symbol => Candidate(symbol, kind)).ToList();
                SizeChoice choice = BuildSizing.Pick(candidates, widthMm, heightMm, BuildSizing.OpeningStepMm / 2.0);

                FamilySymbol chosen = null;
                sized = false;

                if (choice.Found)
                {
                    chosen = symbols[choice.Index];
                    sized = choice.Exact;

                    if (!choice.Exact && candidates[choice.Index].Resizable)
                    {
                        FamilySymbol made = Make(chosen, kind, widthMm, heightMm);
                        if (made != null)
                        {
                            chosen = made;
                            sized = true;
                            symbols.Add(made);
                            _report.NewOpeningTypes++;
                            _report.NewOpeningTypeNames.Add(made.Name);
                        }
                    }
                }

                _bySize[(kind, widthMm, heightMm)] = (chosen, sized);
                return chosen;
            }

            private List<FamilySymbol> Symbols(OpeningKind kind)
            {
                if (_symbols.TryGetValue(kind, out List<FamilySymbol> list)) return list;

                list = OpeningSymbols(_doc, kind);
                _symbols[kind] = list;
                return list;
            }

            private FamilySymbol Make(FamilySymbol source, OpeningKind kind, double widthMm, double heightMm)
            {
                try
                {
                    // Type names are unique within a family, not across the model.
                    var taken = new HashSet<string>(
                        Symbols(kind).Where(symbol => symbol.Family.Id == source.Family.Id).Select(symbol => symbol.Name));
                    string name = BuildSizing.UniqueName(BuildSizing.OpeningTypeName(widthMm, heightMm), taken);

                    if (!(source.Duplicate(name) is FamilySymbol made)) return null;
                    if (SetSize(made, kind, widthMm, heightMm)) return made;

                    _doc.Delete(made.Id);
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[CadToBim] could not make a " + widthMm + " x " + heightMm + " type: " + ex.Message);
                    return null;
                }
            }
        }

        // ─── the model's types, for the builder and the pane ─────────────

        /// <summary>Basic wall types with a layer structure, the ones a width can be read from
        /// and copied.</summary>
        private static List<WallType> BasicWallTypes(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(type => type.Kind == WallKind.Basic && type.GetCompoundStructure() != null)
                .ToList();

        private static List<FamilySymbol> OpeningSymbols(Document doc, OpeningKind kind) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(kind == OpeningKind.Door ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows)
                .Cast<FamilySymbol>()
                .OrderBy(symbol => symbol.FamilyName + ":" + symbol.Name)
                .ToList();

        private static SizeCandidate Candidate(FamilySymbol symbol, OpeningKind kind)
        {
            Parameter width = WidthParameter(symbol, kind);
            Parameter height = HeightParameter(symbol, kind);
            return new SizeCandidate(
                symbol.FamilyName + ":" + symbol.Name,
                width != null && width.StorageType == StorageType.Double ? ToMm(width.AsDouble()) : double.NaN,
                height != null && height.StorageType == StorageType.Double ? ToMm(height.AsDouble()) : double.NaN,
                Writable(width) && Writable(height));
        }

        /// <summary>
        /// The model's wall, door and window types as the builder sees them (same lists, same
        /// order, same sizes), for the Build step's TYPES section. Reads the Document, so call it
        /// in a valid API context (CadToBimPanel.RefreshLevels). Never throws: a failed read is
        /// TypeCatalog.None and the pane leaves the choice to the builder.
        /// </summary>
        public static TypeCatalog ReadTypes(Document doc)
        {
            if (doc == null || doc.IsFamilyDocument) return TypeCatalog.None;
            try
            {
                var catalog = new TypeCatalog();
                foreach (WallType type in BasicWallTypes(doc))
                {
                    catalog.Walls.Add(new TypeOption
                    {
                        Name = type.Name,
                        Id = ElementIdCompat.ToLong(type.Id),
                        WidthMm = ToMm(type.Width),
                        Resizable = true,
                    });
                }

                foreach (OpeningKind kind in new[] { OpeningKind.Door, OpeningKind.Window })
                {
                    List<TypeOption> into = kind == OpeningKind.Door ? catalog.Doors : catalog.Windows;
                    foreach (FamilySymbol symbol in OpeningSymbols(doc, kind))
                    {
                        SizeCandidate c = Candidate(symbol, kind);
                        into.Add(new TypeOption
                        {
                            Name = c.Name,
                            Id = ElementIdCompat.ToLong(symbol.Id),
                            WidthMm = c.WidthMm,
                            HeightMm = c.HeightMm,
                            Resizable = c.Resizable,
                        });
                    }
                }
                return catalog;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CadToBim] could not read the model's types: " + ex.Message);
                return TypeCatalog.None;
            }
        }

        /// <summary>Writes width and height onto a type or an instance; false when either
        /// parameter is missing or read-only there.</summary>
        private static bool SetSize(Element element, OpeningKind kind, double widthMm, double heightMm)
        {
            Parameter width = WidthParameter(element, kind);
            Parameter height = HeightParameter(element, kind);
            if (!Writable(width) || !Writable(height)) return false;

            return TrySet(width, FromMm(widthMm)) && TrySet(height, FromMm(heightMm));
        }

        // Doors and windows each have their own built-in width/height; generic families use the
        // family-wide ones, and a few loadable families only a plain "Width"/"Height".
        // The first writable one wins, so a read-only reporting parameter does not hide a real one.
        private static Parameter WidthParameter(Element element, OpeningKind kind) => FirstOf(
            element.get_Parameter(kind == OpeningKind.Door ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH),
            element.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM),
            element.LookupParameter("Width"));

        private static Parameter HeightParameter(Element element, OpeningKind kind) => FirstOf(
            element.get_Parameter(kind == OpeningKind.Door ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT),
            element.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM),
            element.LookupParameter("Height"));

        private static Parameter FirstOf(params Parameter[] parameters) =>
            parameters.FirstOrDefault(Writable) ?? parameters.FirstOrDefault(parameter => parameter != null);

        private static bool Writable(Parameter parameter) =>
            parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Double;

        private static bool TrySet(Parameter parameter, double internalValue)
        {
            if (!Writable(parameter)) return false;
            try { return parameter.Set(internalValue); }
            catch { return false; }
        }

        /// <summary>
        /// Places each room the classifier found, bounded by its own outline.
        ///
        /// Revit works rooms out from enclosed wall boundaries, and traced walls are not reliably
        /// continuous - even joined, a drafting gap or a corner past reach leaves them open. Ask
        /// Revit to find rooms among those and it returns a model full of "not enclosed".
        ///
        /// So the boundary the classifier already computed is drawn as room separation lines
        /// and the room placed inside it. The enclosure is then ours rather than a side effect
        /// of wall geometry, and a room appears wherever a loop closed - regardless of how
        /// ragged the walls around it are.
        /// </summary>
        private static void CreateRooms(
            Document doc, List<Space> spaces, Level level, double originX, double originY, BuildReport report,
            PlacedLedger ledger)
        {
            if (spaces.Count == 0) return;

            ViewPlan view = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(plan => !plan.IsTemplate && plan.GenLevel != null &&
                                        plan.GenLevel.Id == level.Id);

            // Room separation lines are view-hosted; a level with no plan view gets its walls
            // and openings but no rooms - each one reported, not silent. LevelFor makes a plan
            // for every level it creates, so this is now the rare case.
            if (view == null)
            {
                foreach (Space space in spaces)
                {
                    Centre(space, out double sx, out double sy);
                    report.Skip(BuildCategory.Room, SkipReason.NoPlanView, sx, sy, level.Name);
                }
                return;
            }

            SketchPlane sketch = SketchPlane.Create(
                doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));
            report.HelperIds.Add(ElementIdCompat.ToLong(sketch.Id));

            foreach (Space space in spaces)
            {
                Centre(space, out double cx, out double cy);
                if (space.Boundary.Count < 3) { report.Skip(BuildCategory.Room, SkipReason.OutlineTooShort, cx, cy); continue; }

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

                    if (curves.Size < 3) { report.Skip(BuildCategory.Room, SkipReason.OutlineTooShort, cx, cy); continue; }

                    ModelCurveArray lines = doc.Create.NewRoomBoundaryLines(sketch, curves, view);
                    if (lines != null)
                        foreach (ModelCurve line in lines) report.HelperIds.Add(ElementIdCompat.ToLong(line.Id));

                    // Placed at the centre of the outline, which for the loops a floor plan
                    // produces is inside them.
                    XYZ centre = ToRevit(new CadPoint(cx, cy), originX, originY);

                    Autodesk.Revit.DB.Architecture.Room room =
                        doc.Create.NewRoom(level, new UV(centre.X, centre.Y));

                    if (room == null) { report.Skip(BuildCategory.Room, SkipReason.RevitRefused, cx, cy); continue; }

                    if (space.Name != null) room.Name = space.Name;
                    long roomId = ElementIdCompat.ToLong(room.Id);
                    report.RoomIds.Add(roomId);
                    ledger.Add(roomId, BuildCategory.Room, cx, cy);
                }
                catch
                {
                    // A room that will not place should not cost the rest of them.
                    report.Skip(BuildCategory.Room, SkipReason.RevitRefused, cx, cy);
                }
            }
        }

        private static void Centre(Space space, out double x, out double y)
        {
            if (space.Boundary.Count == 0) { x = 0; y = 0; return; }
            x = space.Boundary.Average(point => point.x);
            y = space.Boundary.Average(point => point.y);
        }

        /// <summary>A wall left out, marked at its centreline's midpoint.</summary>
        private static void SkipWall(BuildReport report, CadWall wall, SkipReason reason)
        {
            CadSegment c = wall.Centerline;
            report.Skip(BuildCategory.Wall, reason, (c.P1.x + c.P2.x) / 2, (c.P1.y + c.P2.y) / 2);
        }

        /// <summary>Drops ids whose element is no longer in the document.</summary>
        private static void KeepPresent(Document doc, List<long> ids) =>
            ids.RemoveAll(id => doc.GetElement(ElementIdCompat.ToElementId(id)) == null);

        // ─── units + errors ──────────────────────────────────────────────

        /// <summary>The classifier works in millimetres; the Revit API works in feet.
        /// UnitTypeId exists from Revit 2021, so this is the same call on 2023-2027.</summary>
        private static double FromMm(double millimetres) =>
            UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

        private static double ToMm(double feet) =>
            UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

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
