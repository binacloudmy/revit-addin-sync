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
                    List<CadSegment> drawn = BuildSizing.HostLines(planWalls, here);

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

                    List<(long Id, DoorSwing Swing)> toTurn;
                    // Which drawn opening each placed door or window is, so a doorway whose
                    // opening did not survive can be reopened (ReopenDoorways).
                    var placedOf = new Dictionary<long, CadOpening>();
                    var drawnOf = new Dictionary<CadWall, CadSegment>();
                    for (int i = 0; i < planWalls.Count; i++) drawnOf[planWalls[i]] = drawn[i];
                    using (var tx = new Transaction(doc, "CAD to BIM: openings and rooms"))
                    {
                        tx.Start();
                        Silence(tx, ledger);

                        // A door needs the wall's geometry to exist - joins included - before it
                        // can cut it.
                        doc.Regenerate();

                        toTurn = CreateOpenings(doc, here, hosts, drawnOf, level, placement.OriginX, placement.OriginY, req,
                            openingTypes, report, ledger, openingNotes, placedOf);

                        List<Space> rooms = spaces
                            .Where(s => OwnerOf(s, owner) == placement)
                            .ToList();
                        CreateRooms(doc, rooms, level, placement.OriginX, placement.OriginY, report, ledger);

                        tx.Commit();
                    }

                    // A stock door whose masonry frame cannot be built in a thin wall ("Can't
                    // make type") is placed once more without it, before it would be listed as
                    // removed. Never throws.
                    PlaceWithoutMasonryFrame(doc, placedOf, hosts, drawnOf, level, placement.OriginX, placement.OriginY, req,
                        openingTypes, report, ledger, openingNotes, toTurn);

                    // An opening Revit could not cut is deleted by the failure resolution as the
                    // transaction commits; list it now, with Revit's reason.
                    ledger.Settle(report, Present, Removed);

                    // Turning a door can leave Revit unable to cut it, and the resolution deletes
                    // it. Its own step, so a turn that costs a door is rolled back and retried
                    // weaker instead. Never throws.
                    TurnDoorsSafely(doc, toTurn, report, ledger);
                    ledger.Settle(report, Present, Removed);

                    // A host lengthened over a door or window that is not there walls its
                    // doorway shut: shorten it back to its drawn end. Never throws.
                    ReopenDoorways(doc, planWalls, drawn, here, placedOf, hosts, placement.OriginX, placement.OriginY, report);

                    // Revit can keep a door hosted without cutting its wall, and says nothing
                    // (seen in Revit 2027; the cause in Revit is not known, the masonry frame is
                    // not it). Check each door for wall in its doorway, and flip an uncut one
                    // there and back, which makes Revit cut it. Windows are checked and logged
                    // only. A recut that costs a door is rolled back. Never throws.
                    RecutDoorsSafely(doc, placedOf, hosts, drawnOf, level, placement.OriginX, placement.OriginY, req, report, ledger);
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
                report.DoorwaysReopened = 0;
                report.DoorsWithoutMasonryFrame = 0;
                report.DoorsNotCut = 0;
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
        ///
        /// Returns the doors placed, with their drawn swing, for TurnDoorsSafely to turn once
        /// this transaction has committed.
        /// </summary>
        private static List<(long Id, DoorSwing Swing)> CreateOpenings(
            Document doc, List<CadOpening> openings,
            Dictionary<CadWall, RevitWall> hosts, Dictionary<CadWall, CadSegment> drawnOf,
            Level level, double originX, double originY, BuildRequest req,
            OpeningTypeCache types, BuildReport report, PlacedLedger ledger, Dictionary<long, string> notes,
            Dictionary<long, CadOpening> placedOf)
        {
            // Doors to turn to their drawn swing once all are placed and committed.
            var toTurn = new List<(long Id, DoorSwing Swing)>();

            foreach (CadOpening opening in openings)
            {
                BuildCategory category = opening.IsDoor ? BuildCategory.Door : BuildCategory.Window;
                double x = opening.Position.x, y = opening.Position.y;

                if (!hosts.TryGetValue(opening.Wall, out RevitWall host)) { report.Skip(category, SkipReason.HostNotBuilt, x, y); continue; }
                // A failure resolution in the joins step can delete a wall.
                if (!host.IsValidObject) { report.Skip(category, SkipReason.HostNotBuilt, x, y); continue; }

                double widthMm = BuildSizing.OpeningWidthMm(opening);
                if (widthMm < MinOpeningWidthMm) { report.Skip(category, SkipReason.TooNarrow, x, y); continue; }

                // Rounded to 10 mm, but never wider than the drawn host piece it is cut from:
                // an insert 2950 wide in a 2946 piece is one Revit cannot cut, and it deletes it.
                drawnOf.TryGetValue(opening.Wall, out CadSegment drawnHost);
                (double w, double h) = BuildSizing.OpeningSize(opening, drawnHost, req.DoorHeightMm, req.WindowHeightMm);

                try
                {
                    FamilyInstance placed = PlaceOpening(doc, opening, host, level, originX, originY, req, types, w, h,
                        null, out SkipReason refused, out bool unsized);
                    if (placed == null) { report.Skip(category, refused, x, y); continue; }
                    if (unsized) report.UnsizedOpenings++;

                    RecordPlaced(placed, opening, host, drawnHost, w, h, report, ledger, notes, placedOf, toTurn);
                }
                catch
                {
                    // A single opening that will not host should not cost the rest.
                    report.Skip(category, SkipReason.RevitRefused, x, y);
                }
            }

            return toTurn;
        }

        /// <summary>
        /// Places one door or window of size w x h (mm) on its host, at its centre, and sizes
        /// it. adjust (optional) runs on the new instance right after it is created, before it
        /// is sized; returning false deletes it again. Returns null with the reason when there
        /// is no family, Revit returns nothing, or adjust refuses it; unsized is true when the
        /// family's size could not be set. Revit errors are thrown to the caller.
        /// </summary>
        private static FamilyInstance PlaceOpening(
            Document doc, CadOpening opening, RevitWall host, Level level, double originX, double originY,
            BuildRequest req, OpeningTypeCache types, double w, double h, Func<FamilyInstance, bool> adjust,
            out SkipReason refused, out bool unsized)
        {
            refused = SkipReason.RevitRefused;
            unsized = false;
            OpeningKind kind = BuildSizing.KindOf(opening);

            FamilySymbol symbol = types.For(kind, w, h, out bool sized);
            if (symbol == null) { refused = SkipReason.NoFamily; return null; }
            if (!symbol.IsActive) symbol.Activate();

            // A window sits at sill height; a door starts at the floor.
            double sill = kind == OpeningKind.Window ? FromMm(req.WindowSillMm) : 0;
            XYZ where = ToRevit(BuildSizing.OpeningCentre(opening), originX, originY);

            FamilyInstance placed = doc.Create.NewFamilyInstance(
                new XYZ(where.X, where.Y, level.Elevation + sill), symbol, host, level,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

            if (placed == null) return null;

            if (adjust != null && !adjust(placed))
            {
                doc.Delete(placed.Id);
                return null;
            }

            // The point's height is not always honoured for hosted windows; the sill
            // parameter is.
            if (kind == OpeningKind.Window) TrySet(placed.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM), sill);

            // A family whose size lives on the instance is sized here instead.
            if (!sized && !SetSize(placed, kind, w, h)) unsized = true;

            return placed;
        }

        /// <summary>Writes a placed opening down: its id in the report, the ledger (at its
        /// drawing position, the point a skip of it would be marked at), which drawn opening it
        /// is, its log note (read only if Revit removes it) and, for a door, its swing to turn
        /// to once committed.</summary>
        private static long RecordPlaced(
            FamilyInstance placed, CadOpening opening, RevitWall host, CadSegment drawnHost, double w, double h,
            BuildReport report, PlacedLedger ledger, Dictionary<long, string> notes,
            Dictionary<long, CadOpening> placedOf, List<(long Id, DoorSwing Swing)> toTurn)
        {
            bool door = BuildSizing.KindOf(opening) == OpeningKind.Door;
            long placedId = ElementIdCompat.ToLong(placed.Id);

            // Counts are taken from these lists once the group is assimilated.
            if (door) report.DoorIds.Add(placedId);
            else report.WindowIds.Add(placedId);

            ledger.Add(placedId, door ? BuildCategory.Door : BuildCategory.Window, opening.Position.x, opening.Position.y);
            placedOf[placedId] = opening;
            notes[placedId] = OpeningNote(
                door ? "door" : "window", placed.Symbol, w, h, BuildSizing.OpeningWidthMm(opening), opening,
                drawnHost, SafeWidthMm(host));

            // Each leaf of a double door is its own instance on its own leaf, turned
            // to its own arc. A door with no clear arc is logged and left as placed.
            if (door) toTurn.Add((placedId, BuildSizing.SwingOf(opening)));
            return placedId;
        }

        /// <summary>Autodesk's metric door content: a frame for masonry walls, on by default, and
        /// one for dry walls. Their names are the families' own instance parameters.</summary>
        private const string MasonryFrameParameter = "Masonry Frame";
        private const string DryWallFrameParameter = "Dry Wall Frame";

        /// <summary>
        /// Stock Autodesk doors fail "Can't make type" in a thin wall (Revit 2027, Generic -
        /// 100mm): the masonry frame cannot be built in it, and the failure resolution deletes
        /// the door. With "Masonry Frame" off ("Dry Wall Frame" on) the same door places fine.
        /// So each door removed with that failure (PlacedLedger.GoneWith CannotMakeType) is
        /// placed once more, same type, host, level and position, with the masonry frame off,
        /// in a transaction of its own ("CAD to BIM: doors (no masonry frame)") inside the
        /// build's group. A door that survives replaces the first attempt everywhere (ledger,
        /// report, placedOf, toTurn, notes), with no skip, and counts in
        /// DoorsWithoutMasonryFrame. A family without a writable "Masonry Frame", a door Revit
        /// removes again, or a step that will not commit leaves the first attempt to be
        /// reported as removed, as before. Never throws.
        /// </summary>
        private static void PlaceWithoutMasonryFrame(
            Document doc, Dictionary<long, CadOpening> placedOf, Dictionary<CadWall, RevitWall> hosts,
            Dictionary<CadWall, CadSegment> drawnOf, Level level, double originX, double originY, BuildRequest req,
            OpeningTypeCache types, BuildReport report, PlacedLedger ledger, Dictionary<long, string> notes,
            List<(long Id, DoorSwing Swing)> toTurn)
        {
            var lines = new List<string>();
            try
            {
                bool Present(long id) => doc.GetElement(ElementIdCompat.ToElementId(id)) != null;

                var lost = new List<(long OldId, CadOpening Opening, RevitWall Host)>();
                foreach (long id in ledger.GoneWith(FailureKind.CannotMakeType, BuildCategory.Door, Present))
                {
                    if (!placedOf.TryGetValue(id, out CadOpening opening)) continue;
                    if (!hosts.TryGetValue(opening.Wall, out RevitWall host) || host == null || !host.IsValidObject) continue;
                    lost.Add((id, opening, host));
                }
                if (lost.Count == 0) return;

                var placed = new List<(long OldId, CadOpening Opening, RevitWall Host, CadSegment DrawnHost, double W, double H, long NewId)>();
                bool committed;
                using (var tx = new Transaction(doc, "CAD to BIM: doors (no masonry frame)"))
                {
                    if (tx.Start() != TransactionStatus.Started) return;
                    Silence(tx, ledger);

                    foreach ((long oldId, CadOpening opening, RevitWall host) in lost)
                    {
                        string at = "door at (" + Mm(opening.Position.x) + "," + Mm(opening.Position.y) + ")";
                        try
                        {
                            drawnOf.TryGetValue(opening.Wall, out CadSegment drawnHost);
                            (double w, double h) = BuildSizing.OpeningSize(opening, drawnHost, req.DoorHeightMm, req.WindowHeightMm);
                            FamilyInstance again = PlaceOpening(doc, opening, host, level, originX, originY, req, types, w, h,
                                WithoutMasonryFrame, out _, out _);
                            if (again == null)
                            {
                                lines.Add(at + " | not placed again: the family has no masonry frame to turn off");
                                continue;
                            }
                            placed.Add((oldId, opening, host, drawnHost, w, h, ElementIdCompat.ToLong(again.Id)));
                        }
                        catch (Exception ex)
                        {
                            lines.Add(at + " | not placed again: " + ex.Message);
                        }
                    }

                    if (placed.Count == 0)
                    {
                        tx.RollBack();
                        WriteLog("doors without a masonry frame, 0 door(s):", lines);
                        return;
                    }
                    committed = tx.Commit() == TransactionStatus.Committed;
                }

                if (!committed)
                {
                    lines.Add("rolled back: Revit would not commit the doors placed again");
                    WriteLog("doors without a masonry frame, 0 door(s):", lines);
                    return;
                }

                int rescued = 0;
                foreach (var p in placed)
                {
                    string at = "door at (" + Mm(p.Opening.Position.x) + "," + Mm(p.Opening.Position.y) + ")";
                    if (!Present(p.NewId))
                    {
                        // Removed again: the first attempt stays in the ledger and is reported
                        // with its own reason, and its doorway is reopened.
                        string text = ledger.FailureText(p.NewId);
                        lines.Add(at + " | removed again" + (string.IsNullOrEmpty(text) ? "" : ": " + text));
                        continue;
                    }

                    ledger.Forget(p.OldId);
                    report.DoorIds.Remove(p.OldId);
                    placedOf.Remove(p.OldId);
                    toTurn.RemoveAll(d => d.Id == p.OldId);
                    notes.Remove(p.OldId);

                    if (doc.GetElement(ElementIdCompat.ToElementId(p.NewId)) is FamilyInstance again)
                        RecordPlaced(again, p.Opening, p.Host, p.DrawnHost, p.W, p.H, report, ledger, notes, placedOf, toTurn);
                    rescued++;
                    report.DoorsWithoutMasonryFrame++;
                    lines.Add(at + " id " + p.NewId + " | placed without its masonry frame (was id " + p.OldId + ")");
                }

                WriteLog("doors without a masonry frame, " + rescued + " door(s):", lines);
            }
            catch (Exception ex)
            {
                lines.Add("placing doors again stopped: " + ex.Message);
                WriteLog("doors without a masonry frame, 0 door(s):", lines);
            }
        }

        /// <summary>Turns the door's masonry frame off and its dry wall frame on, where the
        /// family has them and they can be written. False when there is no writable masonry
        /// frame to turn off (placing the door again would fail the same way).</summary>
        private static bool WithoutMasonryFrame(FamilyInstance door)
        {
            Parameter masonry = door.LookupParameter(MasonryFrameParameter);
            if (!WritableFlag(masonry) || !masonry.Set(0)) return false;

            Parameter dryWall = door.LookupParameter(DryWallFrameParameter);
            if (WritableFlag(dryWall)) dryWall.Set(1);
            return true;
        }

        private static bool WritableFlag(Parameter parameter) =>
            parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Integer;

        /// <summary>
        /// Turns the doors just placed and committed to their drawn swing, in a step of its own
        /// (TurnDoors does the turning). Turning a door at a corner or the end of a short wall
        /// can leave Revit unable to cut it out of its host, and the failure resolution deletes
        /// it. So each attempt runs in a nested TransactionGroup: if any door is gone after it
        /// commits, the attempt is rolled back and tried again with a weaker turn for the doors
        /// that went (Full, facing only, none). At most three attempts; after that every door
        /// stays as placed. A door not fully turned counts in DoorsNotTurned and its log line
        /// says why. Only the attempt that is kept writes its lines and counts.
        ///
        /// Never throws and never fails the build: if a group or transaction will not start or
        /// misbehaves, it is rolled back, the doors stay as placed and count as not turned. The
        /// nested group is assimilated into the build's own, so the build is still one undo.
        /// </summary>
        private static void TurnDoorsSafely(
            Document doc, List<(long Id, DoorSwing Swing)> doors, BuildReport report, PlacedLedger ledger)
        {
            if (doors == null || doors.Count == 0) return;

            bool Present(long id) => doc.GetElement(ElementIdCompat.ToElementId(id)) != null;

            List<(long Id, DoorSwing Swing)> live;
            try { live = doors.Where(d => Present(d.Id)).ToList(); }
            catch { return; }
            if (live.Count == 0) return;

            const int attempts = 3;
            var turn = live.ToDictionary(d => d.Id, d => DoorTurn.Full);
            TransactionGroup inner = null;

            try
            {
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    inner = new TransactionGroup(doc, "CAD to BIM: door swings");
                    if (inner.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("the door swing step would not start");

                    List<string> lines;
                    int notTurned;
                    bool committed;
                    using (var tx = new Transaction(doc, "CAD to BIM: door swings"))
                    {
                        if (tx.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("the door swing step would not start");
                        Silence(tx, ledger);

                        var batch = new List<(FamilyInstance Door, DoorSwing Swing, DoorTurn Turn)>();
                        foreach ((long id, DoorSwing swing) in live)
                            if (doc.GetElement(ElementIdCompat.ToElementId(id)) is FamilyInstance door)
                                batch.Add((door, swing, turn[id]));

                        (lines, notTurned) = TurnDoors(doc, batch);
                        committed = tx.Commit() == TransactionStatus.Committed;
                    }

                    List<long> lost = committed
                        ? live.Where(d => !Present(d.Id)).Select(d => d.Id).ToList()
                        : live.Select(d => d.Id).ToList();

                    if (lost.Count == 0)
                    {
                        inner.Assimilate();
                        inner.Dispose();
                        inner = null;
                        report.DoorsNotTurned += notTurned;
                        WriteLog("door swing, " + lines.Count + " door(s)" +
                                 (attempt > 1 ? ", attempt " + attempt + " (turned less where Revit removed a door)" : "") + ":",
                                 lines);
                        return;
                    }

                    // This turn cost a door: undo it all and turn the doors that went less.
                    inner.RollBack();
                    inner.Dispose();
                    inner = null;
                    foreach (long id in lost) turn[id] = BuildSizing.Weaker(turn[id]);
                }

                LeaveAsPlaced(doc, live, report, "Revit could not cut the turned door out of its wall");
            }
            catch (Exception ex)
            {
                if (inner != null)
                {
                    try { if (inner.HasStarted() && !inner.HasEnded()) inner.RollBack(); } catch { /* already closed */ }
                    try { inner.Dispose(); } catch { /* nothing left to free */ }
                }
                LeaveAsPlaced(doc, live, report, "door turning stopped: " + ex.Message);
            }
        }

        /// <summary>
        /// Shortens back the host walls that were lengthened over a door or window that is not
        /// in the model: never placed (too narrow, no family, refused) or placed and then removed
        /// by Revit. Which walls and to where is BuildSizing.ReopenLines, over the openings still
        /// present. Only walls built in this Confirm are touched (hosts), so walls from earlier
        /// Confirms stay as they are.
        ///
        /// Runs in a nested TransactionGroup assimilated into the build's own, so the build is
        /// still one undo. If moving the walls costs a door or window that was still there, the
        /// whole step is rolled back and the walls stay lengthened. Never throws and never fails
        /// the build. Each wall moved counts in DoorwaysReopened and gets one log line.
        /// </summary>
        private static void ReopenDoorways(
            Document doc, List<CadWall> planWalls, List<CadSegment> drawn, List<CadOpening> here,
            Dictionary<long, CadOpening> placedOf, Dictionary<CadWall, RevitWall> hosts,
            double originX, double originY, BuildReport report)
        {
            var lines = new List<string>();
            TransactionGroup inner = null;
            try
            {
                bool Present(long id) => doc.GetElement(ElementIdCompat.ToElementId(id)) != null;

                // Openings still in the model; everything else no longer justifies a stretch.
                var keptIds = placedOf.Keys.Where(Present).ToList();
                var kept = new HashSet<CadOpening>(keptIds.Select(id => placedOf[id]));

                var todo = new List<(RevitWall Wall, CadSegment From, CadSegment To)>();
                foreach ((int index, CadSegment line) in BuildSizing.ReopenLines(planWalls, drawn, here.Where(kept.Contains)))
                {
                    if (!hosts.TryGetValue(planWalls[index], out RevitWall wall) || wall == null || !wall.IsValidObject) continue;
                    todo.Add((wall, drawn[index], line));
                }
                if (todo.Count == 0) return;

                inner = new TransactionGroup(doc, "CAD to BIM: reopen doorways");
                if (inner.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("the reopen step would not start");

                int moved = 0;
                bool committed;
                using (var tx = new Transaction(doc, "CAD to BIM: reopen doorways"))
                {
                    if (tx.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("the reopen step would not start");
                    Silence(tx);

                    foreach ((RevitWall wall, CadSegment from, CadSegment to) in todo)
                    {
                        string id = ElementIdCompat.ToLong(wall.Id).ToString(CultureInfo.InvariantCulture);
                        try
                        {
                            var location = (LocationCurve)wall.Location;
                            double z = location.Curve.GetEndPoint(0).Z;
                            XYZ a = ToRevit(to.P1, originX, originY);
                            XYZ b = ToRevit(to.P2, originX, originY);
                            location.Curve = Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
                            moved++;
                            lines.Add("wall id " + id + " | " + Mm(from.Length) + " -> " + Mm(to.Length) +
                                      " mm: back to its drawn ends (" + Mm(to.P1.x) + "," + Mm(to.P1.y) + ") - (" +
                                      Mm(to.P2.x) + "," + Mm(to.P2.y) + ")");
                        }
                        catch (Exception ex)
                        {
                            lines.Add("wall id " + id + " | left lengthened: " + ex.Message);
                        }
                    }

                    committed = moved > 0 && tx.Commit() == TransactionStatus.Committed;
                    if (moved == 0 && tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }

                if (!committed || keptIds.Any(id => !Present(id)))
                {
                    inner.RollBack();
                    inner.Dispose();
                    inner = null;
                    if (moved > 0)
                        lines.Add(committed
                            ? "rolled back: shortening the walls cost a door or window, so they stay lengthened"
                            : "rolled back: Revit would not commit the shortened walls");
                    WriteLog("doorways reopened, 0 wall(s):", lines);
                    return;
                }

                inner.Assimilate();
                inner.Dispose();
                inner = null;
                report.DoorwaysReopened += moved;
                WriteLog("doorways reopened, " + moved + " wall(s):", lines);
            }
            catch (Exception ex)
            {
                if (inner != null)
                {
                    try { if (inner.HasStarted() && !inner.HasEnded()) inner.RollBack(); } catch { /* already closed */ }
                    try { inner.Dispose(); } catch { /* nothing left to free */ }
                }
                lines.Add("reopening stopped, walls left lengthened: " + ex.Message);
                WriteLog("doorways reopened, 0 wall(s):", lines);
            }
        }

        /// <summary>
        /// Revit (2027, sample-1) can keep a door hosted without cutting its wall, and raise no
        /// failure; the masonry frame is not the cause, and one flip there and back makes Revit
        /// cut it. This step finds those doors (CheckCuts: wall solid in the doorway) and, for
        /// each, flips its hand twice, else its facing twice, else leaves it. The two flips are
        /// two transactions (flip, commit; flip back, commit) inside one nested TransactionGroup
        /// assimilated into the build's own, so the build is still one undo.
        ///
        /// The group is rolled back if a commit fails, if any door or window still in the model
        /// before it is gone after it, or if a flipped door does not end up facing and handed as
        /// it was; the doors involved are then tried one step weaker (DoorRecut: Hand, Facing,
        /// None), at most three attempts. Windows are checked and logged only. Doors still not
        /// cut afterwards count in DoorsNotCut. Every check and door goes to cad-to-bim.log.
        /// Never throws and never fails the build.
        /// </summary>
        private static void RecutDoorsSafely(
            Document doc, Dictionary<long, CadOpening> placedOf, Dictionary<CadWall, RevitWall> hosts,
            Dictionary<CadWall, CadSegment> drawnOf, Level level, double originX, double originY,
            BuildRequest req, BuildReport report, PlacedLedger ledger)
        {
            int asPlaced = 0;
            bool counted = false;
            var lines = new List<string>();
            TransactionGroup inner = null;
            try
            {
                bool Present(long id) => doc.GetElement(ElementIdCompat.ToElementId(id)) != null;

                (List<long> uncut, int checkedDoors, List<string> doorLines) =
                    CheckCuts(doc, placedOf, hosts, level, originX, originY, req, OpeningKind.Door);
                WriteLog("door cut check (as placed), " + uncut.Count + " of " + checkedDoors + " door(s) do not cut their wall:", doorLines);

                (List<long> uncutWindows, int checkedWindows, List<string> windowLines) =
                    CheckCuts(doc, placedOf, hosts, level, originX, originY, req, OpeningKind.Window);
                WriteLog("window cut check, " + uncutWindows.Count + " of " + checkedWindows + " window(s) do not cut their wall:", windowLines);

                asPlaced = uncut.Count;
                if (asPlaced == 0) return;

                // Everything still here before the recut; any of it gone after = the recut cost it.
                var keptIds = placedOf.Keys.Where(Present).ToList();

                var doors = new List<(long Id, XYZ Facing, XYZ Hand, bool CanHand, bool CanFacing, string Type)>();
                var nudge = new Dictionary<long, DoorRecut>();
                var outcome = new Dictionary<long, string>();
                foreach (long id in uncut)
                {
                    if (!(doc.GetElement(ElementIdCompat.ToElementId(id)) is FamilyInstance door)) continue;
                    bool canHand = door.CanFlipHand, canFacing = door.CanFlipFacing;
                    doors.Add((id, door.FacingOrientation, door.HandOrientation, canHand, canFacing,
                        door.Symbol?.FamilyName + ": " + door.Symbol?.Name));
                    nudge[id] = BuildSizing.RecutBy(canHand, canFacing, DoorRecut.Hand);
                    if (nudge[id] == DoorRecut.None) outcome[id] = "cannot be flipped, left as it is";
                }

                // The recut each door got in the attempt that was kept (None if none was kept or
                // Revit refused its flip).
                var used = doors.ToDictionary(d => d.Id, d => DoorRecut.None);
                const int attempts = 3;
                bool keptAttempt = false;
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    var todo = doors.Where(d => nudge[d.Id] != DoorRecut.None).ToList();
                    if (todo.Count == 0) break;

                    inner = new TransactionGroup(doc, "CAD to BIM: recut doors");
                    if (inner.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("the recut step would not start");

                    var refused = new HashSet<long>();
                    bool committed = FlipForRecut(doc, todo.Select(d => d.Id), nudge, refused, ledger, "CAD to BIM: recut doors (flip)")
                        && FlipForRecut(doc, todo.Select(d => d.Id).Where(id => !refused.Contains(id)), nudge, refused, ledger,
                            "CAD to BIM: recut doors (flip back)");

                    var hit = new HashSet<long>();
                    if (committed)
                    {
                        foreach (long id in keptIds)
                            if (!Present(id)) hit.Add(id);
                        foreach (var d in todo)
                        {
                            if (hit.Contains(d.Id)) continue;
                            if (!(doc.GetElement(ElementIdCompat.ToElementId(d.Id)) is FamilyInstance door) ||
                                !door.FacingOrientation.IsAlmostEqualTo(d.Facing) || !door.HandOrientation.IsAlmostEqualTo(d.Hand))
                                hit.Add(d.Id);
                        }
                    }

                    if (committed && hit.Count == 0)
                    {
                        inner.Assimilate();
                        inner.Dispose();
                        inner = null;
                        foreach (var d in todo)
                            if (!refused.Contains(d.Id)) used[d.Id] = nudge[d.Id];
                        foreach (long id in refused) outcome[id] = "Revit refused the flip, left as it is";
                        if (attempt > 1) lines.Add("kept attempt " + attempt + " (flipped less where a recut cost a door)");
                        keptAttempt = true;
                        break;
                    }

                    // The recut cost a door (or would not commit): undo it all and flip the doors
                    // involved one step weaker; all of them when the one lost was not flipped.
                    inner.RollBack();
                    inner.Dispose();
                    inner = null;
                    var nudged = new HashSet<long>(todo.Select(d => d.Id));
                    IEnumerable<long> weaken = !committed || hit.Any(id => !nudged.Contains(id))
                        ? nudged
                        : hit;
                    foreach (long id in weaken.ToList())
                    {
                        var d = doors.First(x => x.Id == id);
                        nudge[id] = BuildSizing.RecutBy(d.CanHand, d.CanFacing, BuildSizing.Weaker(nudge[id]));
                        if (nudge[id] == DoorRecut.None) outcome[id] = "rolled back: flipping it cost a door";
                    }
                }

                if (!keptAttempt)
                    foreach (var d in doors)
                        if (!outcome.ContainsKey(d.Id)) outcome[d.Id] = "rolled back: flipping it cost a door";

                (List<long> after, int checkedAfter, List<string> afterLines) =
                    CheckCuts(doc, placedOf, hosts, level, originX, originY, req, OpeningKind.Door);
                var stillUncut = new HashSet<long>(after);

                int recut = 0;
                foreach (var d in doors)
                {
                    string at = "door id " + d.Id;
                    double start = double.NaN, end = double.NaN;
                    try
                    {
                        if (doc.GetElement(ElementIdCompat.ToElementId(d.Id)) is FamilyInstance door) at = At(door, "door", d.Id, originX, originY);
                        CadOpening opening = placedOf[d.Id];
                        drawnOf.TryGetValue(opening.Wall, out CadSegment drawnHost);
                        double width = BuildSizing.OpeningSize(opening, drawnHost, req.DoorHeightMm, req.WindowHeightMm).WidthMm;
                        (start, end) = BuildSizing.WallPastJambsMm(opening, drawnHost, width);
                    }
                    catch { /* the line still goes out with what is known */ }

                    bool cuts = Present(d.Id) && !stillUncut.Contains(d.Id);
                    if (cuts) recut++;
                    string result = cuts ? "now cuts its wall" : !Present(d.Id) ? "removed by Revit" : "still does not cut";
                    if (outcome.TryGetValue(d.Id, out string why)) result = why + (cuts ? "; now cuts its wall" : "");
                    lines.Add(BuildSizing.DoorRecutLine(at, d.Type, start, end, used[d.Id], result));
                }
                WriteLog("doors recut, " + recut + " of " + asPlaced + " door(s):", lines);
                WriteLog("door cut check (after recut), " + after.Count + " of " + checkedAfter + " door(s) do not cut their wall:", afterLines);

                report.DoorsNotCut += after.Count;
                counted = true;
            }
            catch (Exception ex)
            {
                if (inner != null)
                {
                    try { if (inner.HasStarted() && !inner.HasEnded()) inner.RollBack(); } catch { /* already closed */ }
                    try { inner.Dispose(); } catch { /* nothing left to free */ }
                }
                lines.Add("recut stopped, doors left as placed: " + ex.Message);
                WriteLog("doors recut, 0 of " + asPlaced + " door(s):", lines);
                if (!counted) report.DoorsNotCut += asPlaced;
            }
        }

        /// <summary>
        /// One transaction of the recut: flips each door by its DoorRecut (hand or facing),
        /// regenerates and commits. A door whose flip is refused (or throws) goes in refused.
        /// True when the transaction committed. Throws only if it will not start.
        /// </summary>
        private static bool FlipForRecut(
            Document doc, IEnumerable<long> ids, Dictionary<long, DoorRecut> nudge, HashSet<long> refused,
            PlacedLedger ledger, string name)
        {
            using (var tx = new Transaction(doc, name))
            {
                if (tx.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("the recut step would not start");
                Silence(tx, ledger);

                foreach (long id in ids.ToList())
                {
                    try
                    {
                        if (!(doc.GetElement(ElementIdCompat.ToElementId(id)) is FamilyInstance door)) { refused.Add(id); continue; }
                        bool ok = nudge[id] == DoorRecut.Hand ? door.flipHand()
                                : nudge[id] == DoorRecut.Facing && door.flipFacing();
                        if (!ok) refused.Add(id);
                    }
                    catch
                    {
                        refused.Add(id);
                    }
                }

                doc.Regenerate();
                return tx.Commit() == TransactionStatus.Committed;
            }
        }

        /// <summary>
        /// Openings of one kind still in the model whose host wall is solid in the opening
        /// (DoesNotCut), at BuildSizing.CutProbeHeightMm above the level. Checked counts those
        /// that could be checked; Lines has one line per uncut opening and per opening that
        /// could not be checked (not counted as uncut), or one "every ... cuts its wall" line.
        /// Never throws for one opening.
        /// </summary>
        private static (List<long> Uncut, int Checked, List<string> Lines) CheckCuts(
            Document doc, Dictionary<long, CadOpening> placedOf, Dictionary<CadWall, RevitWall> hosts,
            Level level, double originX, double originY, BuildRequest req, OpeningKind kind)
        {
            var uncut = new List<long>();
            var lines = new List<string>();
            int checkedCount = 0;
            string name = kind == OpeningKind.Door ? "door" : "window";
            double z = level.Elevation + FromMm(BuildSizing.CutProbeHeightMm(kind, req.DoorHeightMm, req.WindowSillMm, req.WindowHeightMm));

            foreach (KeyValuePair<long, CadOpening> pair in placedOf)
            {
                CadOpening opening = pair.Value;
                if (opening == null || BuildSizing.KindOf(opening) != kind) continue;
                string at = name + " id " + pair.Key;
                try
                {
                    if (!(doc.GetElement(ElementIdCompat.ToElementId(pair.Key)) is FamilyInstance inst) || !inst.IsValidObject)
                        continue;   // removed: reported as removed after placing
                    at = At(inst, name, pair.Key, originX, originY);

                    RevitWall host = inst.Host as RevitWall;
                    if ((host == null || !host.IsValidObject) && hosts.TryGetValue(opening.Wall, out RevitWall drawnHost))
                        host = drawnHost;

                    bool? notCut = host == null || !host.IsValidObject
                        ? null
                        : DoesNotCut(inst, host, z, FromMm(BuildSizing.OpeningWidthMm(opening) / 4.0));
                    if (notCut == null)
                    {
                        lines.Add(at + " | not checked: no host wall or location to probe");
                        continue;
                    }
                    checkedCount++;
                    if (notCut == true)
                    {
                        uncut.Add(pair.Key);
                        lines.Add(at + " | wall solid in the " + (kind == OpeningKind.Door ? "doorway" : "window opening") +
                                  ": Revit did not cut this " + name);
                    }
                }
                catch (Exception ex)
                {
                    lines.Add(at + " | not checked: " + ex.Message);
                }
            }

            if (checkedCount > 0 && lines.Count == 0) lines.Add("every " + name + " checked cuts its wall");
            return (uncut, checkedCount, lines);
        }

        /// <summary>
        /// Whether the host wall is solid where the opening should be: a short line along the
        /// wall through the instance's location point, halfLength (feet) either side, at height
        /// z. True = it meets the wall's solid (not cut), false = cut, null = no location point
        /// or host line to probe with.
        /// </summary>
        private static bool? DoesNotCut(FamilyInstance inst, RevitWall host, double z, double halfLength)
        {
            if (!(inst.Location is LocationPoint point) || !(host.Location is LocationCurve curve)) return null;

            XYZ dir = (curve.Curve.GetEndPoint(1) - curve.Curve.GetEndPoint(0)).Normalize();
            var mid = new XYZ(point.Point.X, point.Point.Y, z);
            Line probe = Line.CreateBound(mid - (dir * halfLength), mid + (dir * halfLength));

            var geometry = host.get_Geometry(new Options());
            if (geometry == null) return false;
            foreach (GeometryObject obj in geometry)
            {
                if (!(obj is Solid solid) || solid.Volume <= 0) continue;
                SolidCurveIntersection hit = solid.IntersectWithCurve(probe, new SolidCurveIntersectionOptions());
                if (hit != null && hit.SegmentCount > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Where a placed door or window really is, for the log: its location point in drawing
        /// millimetres (the inverse of ToRevit), plus the model position when the plan was
        /// moved to the origin. "door id 123456 at (4650,100)". Never throws.
        /// </summary>
        private static string At(FamilyInstance inst, string kind, long id, double originX, double originY)
        {
            string head = kind + " id " + id;
            try
            {
                if (!(inst.Location is LocationPoint point)) return head + " at ?";
                double x = ToMm(point.Point.X), y = ToMm(point.Point.Y);
                string line = head + " at (" + Mm(x + originX) + "," + Mm(y + originY) + ")";
                if (originX != 0 || originY != 0) line += " | model (" + Mm(x) + "," + Mm(y) + ")";
                return line;
            }
            catch
            {
                return head + " at ?";
            }
        }

        private static string Mm(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>Every door with a drawn swing counts as not turned, one log line each. Never
        /// throws.</summary>
        private static void LeaveAsPlaced(Document doc, List<(long Id, DoorSwing Swing)> doors, BuildReport report, string why)
        {
            var lines = new List<string>();
            foreach ((long id, DoorSwing swing) in doors)
            {
                if (!swing.Known) continue;   // left as placed on purpose; never counted
                report.DoorsNotTurned++;
                string type = null;
                try
                {
                    if (doc.GetElement(ElementIdCompat.ToElementId(id)) is FamilyInstance door)
                        type = door.Symbol?.FamilyName + ": " + door.Symbol?.Name;
                }
                catch { /* the line still goes out without its type */ }
                lines.Add("door " + (type ?? "?") + " id " + id + " | NOT turned (left as placed): " + why);
            }
            WriteLog("door swing, " + lines.Count + " door(s), none turned:", lines);
        }

        /// <summary>
        /// Turns each door so it opens into the side the drawing's arc is on, hinged at the
        /// arc's centre, as far as its DoorTurn allows. Revit places a hosted door by the wall's
        /// own sides, whatever the drawing says. The doors are already committed, so their
        /// FacingOrientation / HandOrientation are valid: read each door's facing and hand once
        /// and decide both flips from that read (BuildSizing.DoorFlips, cut down by
        /// BuildSizing.Limit), apply them, regenerate once, and check each door once. Which way
        /// a family's leaf and hinge lie for those vectors is BuildSizing.SwingAlongFacing /
        /// HingeAlongHand (one place, with the reasoning). A refused flip, a weakened turn or a
        /// failed final check counts the door as not turned. Call inside an open transaction.
        /// Returns one line per door for cad-to-bim.log (BuildSizing.DoorTurnLine) and how many
        /// were not turned; it writes neither, so an attempt that is rolled back leaves no
        /// trace.
        /// </summary>
        private static (List<string> Lines, int NotTurned) TurnDoors(
            Document doc, List<(FamilyInstance Door, DoorSwing Swing, DoorTurn Turn)> doors)
        {
            var lines = new List<string>();
            int notTurned = 0;
            if (doors.Count == 0) return (lines, 0);
            int pending = 0;   // doors flipped (or not needing it) whose check has not run
            try
            {
                var applied = new List<(FamilyInstance Door, DoorSwing Swing, DoorTurn Turn, bool Refused, string Line)>();
                foreach ((FamilyInstance door, DoorSwing swing, DoorTurn turn) in doors)
                {
                    string type = null;
                    try
                    {
                        if (!door.IsValidObject)
                        {
                            lines.Add("door ? | removed by Revit before turning");
                            continue;   // reported as removed after placing (PlacedLedger)
                        }
                        type = door.Symbol?.FamilyName + ": " + door.Symbol?.Name;
                        XYZ facing = door.FacingOrientation, hand = door.HandOrientation;

                        if (!swing.Known)
                        {
                            lines.Add(BuildSizing.DoorTurnLine(type, facing.X, facing.Y, hand.X, hand.Y, swing, false, false,
                                "left as placed: " + swing.Why));
                            continue;
                        }

                        (bool flipFacing, bool flipHand) = BuildSizing.Limit(
                            BuildSizing.DoorFlips(swing, facing.X, facing.Y, hand.X, hand.Y), turn);
                        bool refused = false;
                        if (flipFacing) refused |= !door.CanFlipFacing || !door.flipFacing();
                        if (flipHand) refused |= !door.CanFlipHand || !door.flipHand();

                        applied.Add((door, swing, turn, refused,
                            BuildSizing.DoorTurnLine(type, facing.X, facing.Y, hand.X, hand.Y, swing, flipFacing, flipHand, "{0}")));
                    }
                    catch (Exception ex)
                    {
                        notTurned++;
                        lines.Add("door " + (type ?? "?") + " | not turned: " + ex.Message);
                    }
                }

                pending = applied.Count;
                doc.Regenerate();

                foreach ((FamilyInstance door, DoorSwing swing, DoorTurn turn, bool refused, string line) in applied)
                {
                    string result;
                    try
                    {
                        if (!door.IsValidObject)
                        {
                            notTurned++;
                            result = "NOT turned: removed by Revit";
                        }
                        else
                        {
                            (bool facingLeft, bool handLeft) = BuildSizing.DoorFlips(
                                swing, door.FacingOrientation.X, door.FacingOrientation.Y,
                                door.HandOrientation.X, door.HandOrientation.Y);
                            bool ok = !refused && !facingLeft && !handLeft;
                            if (!ok) notTurned++;
                            result = ok ? "matches drawing"
                                : turn != DoorTurn.Full
                                    ? "NOT turned (" + (turn == DoorTurn.FacingOnly ? "facing only" : "left as placed") +
                                      "): Revit could not cut the turned door out of its wall"
                                : refused ? "NOT turned: Revit refused a flip"
                                : "NOT turned: still " + (facingLeft ? "facing" : "") + (facingLeft && handLeft ? "+" : "") + (handLeft ? "hand" : "") + " wrong after flipping";
                        }
                    }
                    catch (Exception ex)
                    {
                        notTurned++;
                        result = "NOT turned: " + ex.Message;
                    }
                    pending--;
                    lines.Add(line.Replace("{0}", result));
                }
            }
            catch (Exception ex)
            {
                // The regenerate failed: whatever was not checked counts as not turned.
                notTurned += pending;
                lines.Add("door turning stopped: " + ex.Message);
            }

            return (lines, notTurned);
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
                SizeChoice choice = BuildSizing.Pick(candidates, widthMm, heightMm, BuildSizing.OpeningStepMm / 2.0,
                    neverWider: true);

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
