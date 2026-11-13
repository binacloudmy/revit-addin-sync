// AI-344 "a better flow": the decisions behind the four-step pane, checked without Revit.
// Pure rules (role guess + reason, need-a-look, steps, level forecast, skip grouping,
// pruning) first; then the view model driven through the same fakes CadToBimViewModelTests
// uses (detection is a delegate, the build seam is two interfaces).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;
using Xunit;
using CadWall = Cad2Bim.Wall;
using CadSegment = Cad2Bim.Segment;
using CadPoint = Cad2Bim.Point;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class CadToBimFlowTests
    {
        // ───────── layer roles ─────────

        [Theory]
        [InlineData("A-WALL", LayerRole.Wall, "name has \"wall\"")]
        [InlineData("Dinding-Bata", LayerRole.Wall, "name has \"dinding\"")]
        [InlineData("A-GLAZ", LayerRole.Window, "name has \"glaz\"")]
        [InlineData("A-WINDOW", LayerRole.Window, "name has \"win\"")]
        [InlineData("A-DOOR", LayerRole.Door, "name has \"door\"")]
        [InlineData("PINTU", LayerRole.Door, "name has \"pintu\"")]
        [InlineData("FURN-CHAIR", LayerRole.Ignore, "excluded by FURN*")]
        [InlineData("PERABUT", LayerRole.Ignore, "excluded by PERABUT")]
        [InlineData("0", LayerRole.Other, "no name hint matched")]
        public void Guess_comes_from_the_settings_hints_with_its_reason(string layer, LayerRole role, string reason)
        {
            RoleGuess guess = LayerRoleRules.Guess(layer, new CadToBimSettings(), new Dictionary<string, LayerRole>());
            Assert.Equal(role, guess.Role);
            Assert.Equal(reason, guess.Reason);
            Assert.False(guess.ByUser);
        }

        [Fact]
        public void A_choice_the_drafter_made_wins_and_says_set_by_you()
        {
            var overrides = new Dictionary<string, LayerRole>(StringComparer.OrdinalIgnoreCase) { ["a-door"] = LayerRole.Window };
            RoleGuess guess = LayerRoleRules.Guess("A-DOOR", new CadToBimSettings(), overrides);
            Assert.Equal(LayerRole.Window, guess.Role);
            Assert.Equal("set by you", guess.Reason);
            Assert.True(guess.ByUser);
        }

        [Fact]
        public void Dropdown_offers_wall_door_window_other_skip_in_that_order()
        {
            Assert.Equal(new[] { "Wall", "Door", "Window", "Other", "Skip" }, LayerRoles.Choices.Select(c => c.Label));
            Assert.Same(LayerRoles.Choices[4], LayerRoles.ChoiceFor(LayerRole.Ignore));
            Assert.Equal("Skip", LayerRoles.Label(LayerRole.Ignore));
        }

        [Theory]
        [InlineData("Window", LayerRole.Window)]
        [InlineData("skip", LayerRole.Ignore)]
        [InlineData("Ignore", LayerRole.Ignore)]
        [InlineData("Other", LayerRole.Other)]
        public void Saved_role_names_and_labels_parse(string text, LayerRole expected)
        {
            Assert.True(LayerRoles.TryParse(text, out LayerRole role));
            Assert.Equal(expected, role);
        }

        [Fact]
        public void An_unknown_saved_role_is_ignored_not_guessed()
        {
            Assert.False(LayerRoles.TryParse("Plumbing", out _));
            Assert.False(LayerRoles.TryParse("", out _));
        }

        [Fact]
        public void Need_a_look_flags_a_big_other_layer_and_a_wall_layer_with_no_walls()
        {
            Assert.Equal("903 entities, no name hint matched", LayerRoleRules.NeedsALook(LayerRole.Other, false, 903, null));
            Assert.Null(LayerRoleRules.NeedsALook(LayerRole.Other, false, 64, null));       // small: text layers etc.
            Assert.Null(LayerRoleRules.NeedsALook(LayerRole.Other, true, 903, null));       // the drafter chose Other
            Assert.Equal("wall layer, but no walls found", LayerRoleRules.NeedsALook(LayerRole.Wall, false, 0, 0));
            Assert.Equal("wall layer, but no walls found", LayerRoleRules.NeedsALook(LayerRole.Wall, true, 12, 0));
            Assert.Null(LayerRoleRules.NeedsALook(LayerRole.Wall, false, 412, 380));
            Assert.Null(LayerRoleRules.NeedsALook(LayerRole.Wall, false, 412, null));       // not measured
            Assert.Null(LayerRoleRules.NeedsALook(LayerRole.Ignore, false, 5000, 0));
        }

        [Fact]
        public void Flagged_layers_sort_to_the_top()
        {
            var rows = new[]
            {
                new CadLayerViewModel("A-DOOR", 38, LayerRole.Door, "name has \"door\""),
                new CadLayerViewModel("0", 903, LayerRole.Other, "no name hint matched", "903 entities, no name hint matched"),
                new CadLayerViewModel("A-WALL", 412, LayerRole.Wall, "name has \"wall\""),
                new CadLayerViewModel("A-WALL-EXT", 3, LayerRole.Wall, "name has \"wall\"", "wall layer, but no walls found"),
            };

            List<CadLayerViewModel> sorted = CadToBimViewModel.SortLayers(rows);

            Assert.Equal(new[] { "0", "A-WALL-EXT", "A-DOOR", "A-WALL" }, sorted.Select(l => l.Name));
            Assert.Equal("903 entities, no name hint matched", sorted[0].DetailText);
            Assert.Equal("name has \"door\"", sorted[2].DetailText);
        }

        [Fact]
        public void Wall_faces_are_counted_per_layer_and_a_layer_with_none_reads_zero()
        {
            CadWall wall = CadOverlayViewportTests.WallAt(0, 0, 4000, 0);   // faces at y = ±50
            var lines = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 50), new CadPoint(4000, 50)) { Layer = "A-WALL" },
                new CadSegment(new CadPoint(0, -50), new CadPoint(4000, -50)) { Layer = "A-WALL" },
                new CadSegment(new CadPoint(0, 3000), new CadPoint(4000, 3000)) { Layer = "A-WALL-EXT" },
            };

            Dictionary<string, int> faces = CadToBimViewModel.CountWallFaces(lines, new List<CadWall> { wall });

            Assert.Equal(2, faces["A-WALL"]);
            Assert.Equal(0, faces["A-WALL-EXT"]);
        }

        // ───────── steps ─────────

        [Fact]
        public void Without_a_drawing_only_layers_opens()
        {
            var nav = new StepNavigator();
            Assert.Equal(CadToBimStep.Layers, nav.Current);
            Assert.False(nav.GoTo(CadToBimStep.Preview));
            Assert.False(nav.GoTo(CadToBimStep.Review));
            Assert.False(nav.Next());
        }

        [Fact]
        public void Steps_are_ticked_once_passed_and_review_needs_a_build()
        {
            var nav = new StepNavigator { HasSession = true };
            Assert.True(nav.Next());
            Assert.True(nav.Next());
            Assert.Equal(CadToBimStep.Build, nav.Current);
            Assert.False(nav.Next());                         // Build's own button builds; no "next"
            Assert.False(nav.CanOpen(CadToBimStep.Review));
            Assert.True(nav.IsDone(CadToBimStep.Layers));
            Assert.True(nav.IsDone(CadToBimStep.Preview));
            Assert.False(nav.IsDone(CadToBimStep.Build));

            Assert.True(nav.Back());
            Assert.Equal(CadToBimStep.Preview, nav.Current);
            Assert.True(nav.IsDone(CadToBimStep.Layers));
            Assert.False(nav.IsDone(CadToBimStep.Preview));   // the open step is never ticked

            nav.Built();
            Assert.Equal(CadToBimStep.Review, nav.Current);
            Assert.True(nav.IsDone(CadToBimStep.Build));

            nav.ClearReview();
            Assert.Equal(CadToBimStep.Preview, nav.Current);
            Assert.False(nav.CanOpen(CadToBimStep.Review));

            nav.Reset();
            Assert.Equal(CadToBimStep.Layers, nav.Current);
            Assert.False(nav.IsDone(CadToBimStep.Preview));
        }

        // ───────── level forecast ─────────

        [Fact]
        public void New_levels_are_predicted_past_the_top_existing_level()
        {
            var levels = new[] { "Level 1", "Level 2" };
            Assert.Empty(LevelPlanner.NewLevels(levels, 0, 2));
            Assert.Equal(new[] { "CAD Level 3" }, LevelPlanner.NewLevels(levels, 0, 3));
            Assert.Equal(new[] { "CAD Level 2", "CAD Level 3" }, LevelPlanner.NewLevels(levels, 1, 3));
            Assert.Empty(LevelPlanner.NewLevels(levels, 0, 1));
        }

        [Fact]
        public void A_cad_level_left_by_an_earlier_build_is_reused_not_predicted()
        {
            var levels = new[] { "Level 1", "CAD Level 2" };
            Assert.Empty(LevelPlanner.NewLevels(levels, 1, 2));   // index 1 → CAD Level 2 exists by name
        }

        // ───────── build report ─────────

        [Fact]
        public void Skipped_items_group_by_real_reason_with_counts_and_text()
        {
            var report = new BuildReport();
            report.Skip(BuildCategory.Door, SkipReason.HostNotBuilt, 1, 1);
            report.Skip(BuildCategory.Door, SkipReason.HostNotBuilt, 2, 2);
            report.Skip(BuildCategory.Door, SkipReason.TooNarrow, 3, 3);
            report.Skip(BuildCategory.Window, SkipReason.NoFamily, 4, 4);
            report.Skip(BuildCategory.Wall, SkipReason.TooShort, 5, 5);
            report.Skip(BuildCategory.Room, SkipReason.NoPlanView, 6, 6, "CAD Level 2");
            report.Skip(BuildCategory.Room, SkipReason.OutlineTooShort, 7, 7);

            List<SkipGroup> groups = report.SkipGroups();

            Assert.Equal(new[]
            {
                "1 wall: centreline too short",
                "2 doors: host wall not built",
                "1 door: narrower than 300 mm",
                "1 window: no window family in the model",
                "1 room: CAD Level 2 has no plan view",
                "1 room: outline too short",
            }, groups.Select(g => g.CountText + ": " + g.ReasonText));
            Assert.Equal(new[] { 1.0, 2.0 }, groups[1].Items.Select(i => i.X));

            // The old counters stay in step for anything still reading them.
            Assert.Equal(1, report.SkippedWalls);
            Assert.Equal(4, report.SkippedOpenings);
            Assert.Equal(2, report.SkippedRooms);
        }

        [Fact]
        public void All_created_ids_come_in_delete_order_levels_last()
        {
            var report = new BuildReport();
            report.WallIds[CadOverlayViewportTests.WallAt(0, 0, 1000, 0)] = 10;
            report.DoorIds.Add(20);
            report.WindowIds.Add(30);
            report.RoomIds.Add(40);
            report.HelperIds.Add(41);
            report.NewViewIds.Add(50);
            report.NewLevelIds.Add(60);

            Assert.Equal(new long[] { 40, 41, 20, 30, 10, 50, 60 }, report.AllCreatedIds());
            Assert.Equal(new long[] { 10 }, report.IdsOf(BuildCategory.Wall));
            Assert.Equal(new long[] { 30 }, report.IdsOf(BuildCategory.Window));
        }

        // The Revit 2027 case: 167 walls, 7 doors, 17 windows, 3 rooms (194, what Select in
        // model selects), plus a sketch plane and 12 room separation lines (207 ids in all).
        // Deleting the sketch plane takes its lines, so the undo removes 207 but deletes 195 itself.
        private static BuildReport FloorPlanBuild(bool newLevel = false)
        {
            var report = new BuildReport();
            long id = 1000;
            for (int i = 0; i < 167; i++) report.WallIds[CadOverlayViewportTests.WallAt(i * 1000, 0, i * 1000 + 500, 0)] = id++;
            for (int i = 0; i < 7; i++) report.DoorIds.Add(id++);
            for (int i = 0; i < 17; i++) report.WindowIds.Add(id++);
            for (int i = 0; i < 3; i++) report.RoomIds.Add(id++);
            for (int i = 0; i < 13; i++) report.HelperIds.Add(id++);
            if (newLevel)
            {
                report.NewViewIds.Add(id++);
                report.NewLevelIds.Add(id++);
                report.NewLevelNames.Add("CAD Level 2");
            }
            return report;
        }

        [Fact]
        public void Undo_confirm_names_what_the_build_made_not_a_raw_id_count()
        {
            BuildReport build = FloorPlanBuild();
            Assert.Equal(207, build.AllCreatedIds().Count);

            Assert.Equal("Delete what this build made: 167 walls, 7 doors, 17 windows and 3 rooms? " +
                         "One Ctrl+Z in Revit brings them back.", UndoSummary.ConfirmText(build));

            BuildReport withLevel = FloorPlanBuild(newLevel: true);
            Assert.Equal("167 walls, 7 doors, 17 windows and 3 rooms, plus 1 new level and 1 floor plan",
                         UndoSummary.Describe(withLevel, null));
        }

        [Fact]
        public void Undo_done_text_matches_the_confirm_when_everything_went()
        {
            BuildReport build = FloorPlanBuild(newLevel: true);
            List<long> all = build.AllCreatedIds();
            var undo = new BuildReport { Kind = BuildJobKind.Undo, MissingIds = all, DeletedIds = all, Affected = all.Count };

            Assert.Equal("Build undone: deleted 167 walls, 7 doors, 17 windows and 3 rooms, plus 1 new level and " +
                         "1 floor plan (one Ctrl+Z brings them back). The walls are unbuilt again.",
                         UndoSummary.DoneText(build, undo));
        }

        [Fact]
        public void Undo_done_text_skips_ids_already_gone_and_names_what_was_left()
        {
            BuildReport build = FloorPlanBuild();
            List<long> all = build.AllCreatedIds();
            long goneByHand = build.DoorIds[0];
            long pinned = build.WindowIds[0];

            var undo = new BuildReport { Kind = BuildJobKind.Undo };
            undo.MissingIds.AddRange(all.Where(id => id != pinned));
            undo.DeletedIds.AddRange(all.Where(id => id != pinned && id != goneByHand));

            Assert.Equal("Build undone: deleted 167 walls, 6 doors, 16 windows and 3 rooms (one Ctrl+Z brings them back). " +
                         "1 element could not be deleted (pinned or protected). The walls are unbuilt again.",
                         UndoSummary.DoneText(build, undo));
        }

        [Fact]
        public void Undo_done_text_when_nothing_was_left_to_delete()
        {
            BuildReport build = FloorPlanBuild();
            var undo = new BuildReport { Kind = BuildJobKind.Undo, MissingIds = build.AllCreatedIds() };

            Assert.Equal("Build undone: nothing left to delete, those elements were already gone. The walls are unbuilt again.",
                         UndoSummary.DoneText(build, undo));
        }

        [Fact]
        public void Warnings_name_new_levels_erased_walls_and_picked_openings()
        {
            var report = new BuildReport();
            report.NewLevelNames.Add("CAD Level 2");
            report.NewViewIds.Add(7);

            List<string> warnings = CadToBimViewModel.BuildWarnings(report, erased: 3, picked: 1, unpaired: 0);

            Assert.Equal(new[]
            {
                "New level made: CAD Level 2 (with a floor plan)",
                "3 walls erased in preview",
                "1 door or window left out with Pick",
            }, warnings);
        }

        // ───────── session ─────────

        [Fact]
        public void Forgetting_built_ids_makes_those_walls_pending_again()
        {
            var session = new CadToBimSession();
            CadWall a = CadOverlayViewportTests.WallAt(0, 0, 1000, 0);
            CadWall b = CadOverlayViewportTests.WallAt(0, 2000, 1000, 2000);
            session.SetWalls(new List<CadWall> { a, b });
            var report = new BuildReport();
            report.WallIds[a] = 101;
            report.WallIds[b] = 102;
            session.MarkBuilt(report);
            Assert.Empty(session.Pending());

            Assert.Equal(1, session.ForgetBuilt(new long[] { 102, 999 }));

            Assert.Same(b, Assert.Single(session.Pending()));
            Assert.Equal(0, session.ForgetBuilt(null));
        }

        [Fact]
        public void A_picked_opening_survives_a_redetect_by_position()
        {
            CadWall host = CadOverlayViewportTests.WallAt(0, 0, 4000, 0);
            Cad2Bim.Opening window = WindowIn(host, 2000);
            var first = new CadToBimSession { DrawingPath = "a.dwg" };
            first.SetWalls(new List<CadWall> { host });
            first.TogglePick(window);
            Assert.True(first.IsPickedOut(window));

            var second = new CadToBimSession { DrawingPath = "a.dwg" };
            CadWall again = CadOverlayViewportTests.WallAt(0, 0, 4000, 0);
            second.SetWalls(new List<CadWall> { again });
            second.CarryForwardFrom(first);

            Assert.True(second.IsPickedOut(WindowIn(again, 2000)));
            Assert.False(second.IsPickedOut(WindowIn(again, 3000)));

            second.TogglePick(WindowIn(again, 2000));
            Assert.Empty(second.PickedOut);
        }

        [Fact]
        public void Pick_hit_test_finds_the_nearest_opening_within_reach()
        {
            CadWall host = CadOverlayViewportTests.WallAt(0, 0, 9000, 0);
            var openings = new List<Cad2Bim.Opening> { WindowIn(host, 2000), WindowIn(host, 6000) };

            Assert.Same(openings[0], CadOverlayViewport.NearestOpening(openings, 2100, 50, 300));
            Assert.Same(openings[1], CadOverlayViewport.NearestOpening(openings, 5800, 0, 300));
            Assert.Null(CadOverlayViewport.NearestOpening(openings, 4000, 0, 300));
        }

        // ───────── view model ─────────

        private sealed class FakeSink : IBuildRequestSink
        {
            public BuildRequest Request { get; set; }
            public event Action<BuildReport> Completed;
            public void Fire(BuildReport report) => Completed?.Invoke(report);
        }

        private sealed class FakeRaiser : IBuildEventRaiser
        {
            public int Calls;
            public bool Raise() { Calls++; return true; }
        }

        private const string Dwg = "/tmp/taman-desa/A-101 Unit Plan.dwg";

        // Two walls, and window marks on <markLayer> across the first wall at x = 1700/2300.
        private static DetectResult Plan(string path, string markLayer)
        {
            var model = new Cad2Bim.CadModel();
            model.Segments.Add(new CadSegment(new CadPoint(1700, -300), new CadPoint(1700, 300)) { Layer = markLayer });
            model.Segments.Add(new CadSegment(new CadPoint(2300, -300), new CadPoint(2300, 300)) { Layer = markLayer });
            var session = new CadToBimSession { DrawingPath = path, Scale = 1.0, Model = model, MedianThicknessMm = 100 };
            session.Walls.Add(CadOverlayViewportTests.WallAt(0, 0, 9000, 0));
            session.Walls.Add(CadOverlayViewportTests.WallAt(0, 4300, 4200, 4300));
            return new DetectResult
            {
                Session = session,
                LayerShapes = new Dictionary<string, List<object>>(),
                Census = new Dictionary<string, int> { ["A-WALL"] = 4, [markLayer] = 2, ["0"] = 903 },
                WallFacesByLayer = new Dictionary<string, int> { ["A-WALL"] = 4 },
                Bounds = new System.Windows.Rect(0, 0, 9000, 4300),
                PlanCount = 1,
                Entities = 909,
                ReadSeconds = 0.01,
                UnitsText = "1 unit = 1 mm · header",
            };
        }

        private static (CadToBimViewModel vm, FakeSink sink, FakeRaiser raiser) Make(
            string markLayer = "A-GLAZ", CadToBimSettings settings = null, List<CadToBimSettings> saves = null)
        {
            var sink = new FakeSink();
            var raiser = new FakeRaiser();
            var vm = new CadToBimViewModel(sink, raiser, settings ?? new CadToBimSettings(),
                (path, s, sMin, sMax, roles, progress, ct) => Plan(path, markLayer),
                saveSettings: s => saves?.Add(s));
            return (vm, sink, raiser);
        }

        private static BuildReport Built(CadToBimViewModel vm)
        {
            var report = new BuildReport { DocumentKey = @"C:\p\model.rvt" };
            report.WallIds[vm.Session.Walls[0]] = 101;
            report.WallIds[vm.Session.Walls[1]] = 102;
            report.WindowIds.Add(201);
            report.Walls = 2;
            report.Windows = 1;
            report.LevelNames.Add("Level 1");
            report.Skip(BuildCategory.Door, SkipReason.HostNotBuilt, 500, 600);
            return report;
        }

        [Fact]
        public async Task Layers_show_reasons_flags_and_the_summary()
        {
            var (vm, _, _) = Make();
            await vm.OpenAsync(Dwg);

            List<CadLayerViewModel> rows = vm.Layers.Cast<CadLayerViewModel>().ToList();
            Assert.Equal("0", rows[0].Name);                  // flagged first
            Assert.True(rows[0].NeedsLook);
            Assert.Equal("903 entities, no name hint matched", rows[0].DetailText);
            Assert.Equal("3 · 1 need a look", vm.LayerSummary);
            CadLayerViewModel glaz = rows.Single(r => r.Name == "A-GLAZ");
            Assert.Equal(LayerRole.Window, glaz.Role);
            Assert.Equal("name has \"glaz\"", glaz.Reason);
            Assert.Equal(1, vm.WindowCount);
        }

        [Fact]
        public async Task The_window_role_decides_window_marks_and_is_remembered_for_the_drawing()
        {
            var settings = new CadToBimSettings();
            var saves = new List<CadToBimSettings>();
            var (vm, _, _) = Make("A-DOOR", settings, saves);
            await vm.OpenAsync(Dwg);
            CadLayerViewModel door = vm.Layers.Cast<CadLayerViewModel>().Single(r => r.Name == "A-DOOR");
            Assert.Equal(LayerRole.Door, door.Role);
            Assert.Equal(0, vm.WindowCount);                 // a door layer's lines never become windows

            vm.SetRole(door, LayerRole.Window);              // one change = one re-detect
            await vm.OpenAsync(Dwg);

            Assert.Single(saves);
            Assert.Equal(1, vm.WindowCount);
            CadLayerViewModel after = vm.Layers.Cast<CadLayerViewModel>().Single(r => r.Name == "A-DOOR");
            Assert.Equal(LayerRole.Window, after.Role);
            Assert.Equal("set by you", after.Reason);

            // A fresh pane on the same drawing gets the choice back from the settings.
            var (again, _, _) = Make("A-DOOR", settings);
            await again.OpenAsync(Dwg);
            Assert.Equal(LayerRole.Window, again.Layers.Cast<CadLayerViewModel>().Single(r => r.Name == "A-DOOR").Role);
            Assert.Equal(1, again.WindowCount);
        }

        [Fact]
        public async Task Picking_a_window_leaves_it_out_of_counts_overlay_and_request()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            Cad2Bim.Opening window = Assert.Single(vm.Session.Openings);
            Assert.Equal("Build 2 walls + 1 more", vm.ConfirmLabel);

            vm.TogglePick(window);

            Assert.Equal(0, vm.WindowCount);
            Assert.Equal(1, vm.PickedCount);
            Assert.Single(vm.Overlay.PickedOut);
            Assert.Empty(vm.Overlay.Openings);
            Assert.Equal("Build 2 walls", vm.ConfirmLabel);
            vm.Confirm();
            Assert.Empty(sink.Request.Openings);
        }

        [Fact]
        public async Task Stepping_back_and_forth_keeps_erase_work()
        {
            var (vm, _, _) = Make();
            await vm.OpenAsync(Dwg);
            Assert.True(vm.Next());
            vm.ToggleErase(vm.Session.Walls[0]);
            Assert.True(vm.GoTo(CadToBimStep.Layers));
            Assert.True(vm.GoTo(CadToBimStep.Preview));

            Assert.Equal(1, vm.ErasedCount);
            Assert.Equal(1, vm.WallCount);
            Assert.True(vm.Steps[0].IsDone);
            Assert.Equal("1 Layers ✓", vm.Steps[0].Text);
            Assert.False(vm.Steps[3].IsEnabled);
        }

        [Fact]
        public async Task A_build_opens_review_with_created_rows_skip_groups_and_warnings()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.ToggleErase(vm.Session.Walls[1]);
            vm.ToggleErase(vm.Session.Walls[1]);             // restored: no erase warning
            vm.Confirm();

            sink.Fire(Built(vm));

            Assert.True(vm.IsReviewStep);
            Assert.True(vm.Steps[2].IsDone);
            Assert.StartsWith("Built in ", vm.ReviewHeadline);
            Assert.EndsWith(" on Level 1", vm.ReviewHeadline);
            Assert.Equal(new[] { 2, 0, 1, 0 }, vm.CreatedRows.Select(r => r.Count));
            SkipGroup group = Assert.Single(vm.SkipGroups);
            Assert.Equal("1 door", group.CountText);
            Assert.Equal("host wall not built", group.ReasonText);
            Assert.Equal("SKIPPED (1)", vm.SkippedTitle);
            Assert.False(vm.HasWarnings);

            vm.ShowSkipGroup(group);
            SkippedItem marked = Assert.Single(vm.Overlay.Highlights);
            Assert.Equal(500, marked.X);
            Assert.Contains("1 door skipped: host wall not built", vm.Status);

            vm.BuildMore();
            Assert.True(vm.IsPreviewStep);
            Assert.Empty(vm.Overlay.Highlights);
        }

        [Fact]
        public async Task The_build_request_carries_built_ids_and_a_stale_answer_puts_undone_walls_back()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));
            vm.Session.AddForced(new[] { CadOverlayViewportTests.WallAt(0, 8000, 2000, 8000) });
            vm.BuildMore();

            vm.Confirm();
            Assert.Equal(BuildJobKind.Build, sink.Request.Kind);
            Assert.Equal(new long[] { 101, 102 }, sink.Request.BuiltWallIds.OrderBy(i => i));

            // Revit: walls 101 and 102 are gone (Ctrl+Z). Nothing was built.
            sink.Fire(new BuildReport { Stale = true, MissingIds = new List<long> { 101, 102 } });

            Assert.False(vm.Busy);
            Assert.Equal(3, vm.WallCount);
            Assert.Equal(0, vm.BuiltCount);
            Assert.False(vm.IsReviewStep);
            Assert.Contains("no longer in the model", vm.Status);
        }

        [Fact]
        public async Task Opening_the_build_step_checks_the_model_for_undone_walls()
        {
            var (vm, sink, raiser) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));
            vm.BuildMore();
            Assert.Equal(0, vm.WallCount);
            Assert.Equal("Check the model again", vm.ConfirmLabel);
            Assert.True(vm.CanConfirm);
            int before = raiser.Calls;

            vm.GoTo(CadToBimStep.Build);

            Assert.Equal(before + 1, raiser.Calls);
            Assert.Equal(BuildJobKind.CheckBuilt, sink.Request.Kind);
            Assert.False(vm.Busy);                           // a check never locks the pane
            sink.Fire(new BuildReport { Kind = BuildJobKind.CheckBuilt, MissingIds = new List<long> { 102 } });

            Assert.Equal(1, vm.WallCount);
            Assert.Equal("Build 1 wall", vm.ConfirmLabel);
        }

        [Fact]
        public async Task A_check_still_waiting_on_revit_never_blocks_build_or_undo()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));
            vm.Session.AddForced(new[] { CadOverlayViewportTests.WallAt(0, 8000, 2000, 8000) });
            vm.BuildMore();
            vm.GoTo(CadToBimStep.Build);
            Assert.Equal(BuildJobKind.CheckBuilt, sink.Request.Kind);   // Revit has not answered yet

            Assert.True(vm.CanConfirm);
            vm.Confirm();

            Assert.Equal(BuildJobKind.Build, sink.Request.Kind);       // took the slot
            Assert.True(vm.Busy);
        }

        [Fact]
        public async Task Confirm_with_nothing_pending_but_built_walls_rechecks_the_model()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));

            vm.Confirm();

            Assert.Equal(BuildJobKind.CheckBuilt, sink.Request.Kind);
            Assert.Equal(new long[] { 101, 102 }, sink.Request.ElementIds.OrderBy(i => i));
        }

        [Fact]
        public async Task Undo_this_build_deletes_every_recorded_id_then_returns_to_preview_unbuilt()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));

            vm.UndoBuild();

            Assert.Equal(BuildJobKind.Undo, sink.Request.Kind);
            Assert.Equal(@"C:\p\model.rvt", sink.Request.DocumentKey);
            Assert.Equal(new long[] { 201, 101, 102 }, sink.Request.ElementIds);
            Assert.True(vm.Busy);

            sink.Fire(new BuildReport { Kind = BuildJobKind.Undo, Affected = 3, MissingIds = new List<long> { 201, 101, 102 }, DeletedIds = new List<long> { 201, 101, 102 } });

            Assert.False(vm.Busy);
            Assert.True(vm.IsPreviewStep);
            Assert.Null(vm.LastBuild);
            Assert.Equal(2, vm.WallCount);
            Assert.False(vm.Steps[3].IsEnabled);
            Assert.Equal("Build undone: deleted 2 walls and 1 window (one Ctrl+Z brings them back). The walls are unbuilt again.", vm.Status);
        }

        [Fact]
        public async Task Save_as_new_rvt_after_a_build_builds_everything_again()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));
            vm.BuildMore();
            Assert.Equal(0, vm.WallCount);
            Assert.Equal("Check the model again", vm.ConfirmLabel);

            vm.Target = BuildTarget.NewFile;

            Assert.Equal(2, vm.WallCount);
            Assert.Equal(0, vm.BuiltCount);
            Assert.False(vm.HasBuilt);
            Assert.Equal("Build 2 walls + 1 more", vm.ConfirmLabel);
            vm.Confirm();
            Assert.Equal(BuildJobKind.Build, sink.Request.Kind);
            Assert.Equal(2, sink.Request.Walls.Count);
            Assert.Single(sink.Request.Openings);
            Assert.Empty(sink.Request.BuiltWallIds);

            // The new file's walls are not recorded against the open project.
            var made = new BuildReport { DocumentKey = @"C:\taman-desa\A-101 Unit Plan.rvt", OutputPath = @"C:\taman-desa\A-101 Unit Plan.rvt" };
            made.WallIds[vm.Session.Walls[0]] = 7;
            sink.Fire(made);
            Assert.Equal(new long[] { 101, 102 }, vm.Session.BuiltWallIds.Values.OrderBy(i => i));

            // Revit now shows the new file, which holds none of model.rvt's ids: every wall counts.
            vm.BuildMore();
            vm.Target = BuildTarget.AddToProject;
            Assert.Equal(2, vm.WallCount);

            // Back in model.rvt (the Build step asks which project is active): its walls are built.
            vm.GoTo(CadToBimStep.Build);
            Assert.Equal(BuildJobKind.CheckBuilt, sink.Request.Kind);
            sink.Fire(new BuildReport { Kind = BuildJobKind.CheckBuilt, DocumentKey = @"C:\p\model.rvt" });
            Assert.Equal(0, vm.WallCount);
            Assert.Equal(2, vm.BuiltCount);
        }

        [Fact]
        public async Task Preview_counts_only_openings_the_next_build_will_place()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            Assert.Equal(1, vm.WindowCount);                 // on wall 0

            // Only wall 1 was built: wall 0 and its window are still to come.
            vm.Confirm();
            var report = new BuildReport { DocumentKey = @"C:\p\model.rvt" };
            report.WallIds[vm.Session.Walls[1]] = 102;
            report.Walls = 1;
            sink.Fire(report);
            vm.BuildMore();
            Assert.Equal(1, vm.WallCount);
            Assert.Equal(1, vm.WindowCount);

            // Now wall 0 too: its window went in with it, so nothing is left to count.
            vm.Confirm();
            var rest = new BuildReport { DocumentKey = @"C:\p\model.rvt" };
            rest.WallIds[vm.Session.Walls[0]] = 101;
            rest.WindowIds.Add(201);
            rest.Walls = 1;
            rest.Windows = 1;
            sink.Fire(rest);
            vm.BuildMore();

            Assert.Equal(0, vm.WallCount);
            Assert.Equal(0, vm.DoorCount);
            Assert.Equal(0, vm.WindowCount);
            Assert.Equal(0, vm.RoomCount);
            Assert.Single(vm.Session.Openings);              // still detected and drawn
            Assert.Single(vm.Overlay.Openings);

            vm.Target = BuildTarget.NewFile;                 // a new file gets it all again
            Assert.Equal(1, vm.WindowCount);
        }

        [Fact]
        public void Built_ids_from_another_project_are_replaced_not_mixed()
        {
            CadWall a = CadOverlayViewportTests.WallAt(0, 0, 4000, 0);
            CadWall b = CadOverlayViewportTests.WallAt(0, 3000, 4000, 3000);
            var session = new CadToBimSession { DrawingPath = "a.dwg" };
            session.SetWalls(new List<CadWall> { a, b });

            var first = new BuildReport { DocumentKey = @"C:\p\one.rvt" };
            first.WallIds[a] = 1;
            session.MarkBuilt(first);
            var second = new BuildReport { DocumentKey = @"C:\p\two.rvt" };
            second.WallIds[b] = 2;
            session.MarkBuilt(second);

            Assert.Equal(@"C:\p\two.rvt", session.BuiltDocumentKey);
            Assert.Equal(new[] { b }, session.BuiltWallIds.Keys);
        }

        [Fact]
        public async Task A_check_in_another_project_counts_every_wall_and_keeps_the_built_ids()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));
            vm.BuildMore();
            vm.GoTo(CadToBimStep.Build);
            Assert.Equal(BuildJobKind.CheckBuilt, sink.Request.Kind);

            sink.Fire(new BuildReport
            {
                Kind = BuildJobKind.CheckBuilt,
                DocumentKey = @"C:\p\other.rvt",
                MissingIds = new List<long> { 101, 102 },
            });

            Assert.Equal(2, vm.WallCount);
            Assert.Equal(2, vm.Session.BuiltWallIds.Count);   // still built in model.rvt
            Assert.Contains("model.rvt", vm.Status);
            vm.Confirm();
            Assert.Empty(sink.Request.BuiltWallIds);
        }

        [Fact]
        public async Task Undo_after_a_new_file_build_only_says_to_delete_the_file()
        {
            var (vm, sink, raiser) = Make();
            await vm.OpenAsync(Dwg);
            vm.Target = BuildTarget.NewFile;
            vm.Confirm();
            BuildReport report = Built(vm);
            report.OutputPath = @"C:\taman-desa\A-101 Unit Plan.rvt";
            sink.Fire(report);
            int before = raiser.Calls;

            vm.UndoBuild();

            Assert.True(vm.LastBuildWasNewFile);
            Assert.Equal(before, raiser.Calls);
            Assert.Contains("delete the file", vm.Status);
        }

        [Fact]
        public async Task Select_in_model_asks_revit_for_one_category_or_everything()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(Built(vm));

            vm.SelectCreated(BuildCategory.Window);
            Assert.Equal(BuildJobKind.Select, sink.Request.Kind);
            Assert.Equal(new long[] { 201 }, sink.Request.ElementIds);
            sink.Fire(new BuildReport { Kind = BuildJobKind.Select, Affected = 1 });
            Assert.Equal("Selected 1 in the model.", vm.Status);

            vm.SelectCreated(null);
            Assert.Equal(new long[] { 101, 102, 201 }, sink.Request.ElementIds.OrderBy(i => i));
        }

        [Fact]
        public async Task The_build_step_warns_before_making_new_levels()
        {
            var (vm, _, _) = Make();
            vm.SetLevels(new[] { new LevelChoice { Name = "Level 1", Id = 1 } });
            await vm.OpenAsync(Dwg);
            vm.SelectedLevel = vm.Levels[0];

            vm.GoTo(CadToBimStep.Build);
            Assert.Equal("", vm.LevelForecast);              // one plan, one level: nothing new

            vm.Session.AddForced(new[] { CadOverlayViewportTests.WallAt(90000, 0, 99000, 0) });   // a second plan far away
            vm.Storeys = StoreyMode.Stack;
            vm.Storeys = StoreyMode.KeepPosition;
            Assert.Equal("", vm.LevelForecast);
            vm.Storeys = StoreyMode.Stack;
            Assert.Equal("Will create 1 new level: CAD Level 2", vm.LevelForecast);
        }

        [Fact]
        public async Task Levels_a_build_made_join_the_picker_and_leave_it_on_undo()
        {
            var (vm, sink, _) = Make();
            vm.SetLevels(new[] { new LevelChoice { Name = "Level 1", Id = 1 } });
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            BuildReport report = Built(vm);
            report.NewLevelIds.Add(77);
            report.NewLevelNames.Add("CAD Level 2");
            sink.Fire(report);

            Assert.Equal(new[] { "Level 1", "CAD Level 2" }, vm.Levels.Select(l => l.Name));

            vm.UndoBuild();
            sink.Fire(new BuildReport { Kind = BuildJobKind.Undo, Affected = 4, MissingIds = new List<long> { 201, 101, 102, 77 } });

            Assert.Equal(new[] { "Level 1" }, vm.Levels.Select(l => l.Name));
            Assert.Equal("Level 1", vm.SelectedLevel.Name);
        }

        // ───────── types (AI-345) ─────────

        private static TypeCatalog Types() => new TypeCatalog
        {
            Walls =
            {
                new TypeOption { Name = "Generic - 200mm", Id = 11, WidthMm = 200, Resizable = true },
                new TypeOption { Name = "Partition 100", Id = 12, WidthMm = 100, Resizable = true },
            },
            Windows =
            {
                new TypeOption { Name = "Fixed:1200 x 1200", Id = 31, WidthMm = 1200, HeightMm = 1200, Resizable = true },
            },
        };

        [Fact]
        public async Task The_build_step_lists_a_type_per_size_defaulting_to_the_builders_choice()
        {
            var (vm, _, _) = Make();
            vm.SetTypeCatalog(Types());
            await vm.OpenAsync(Dwg);
            vm.GoTo(CadToBimStep.Build);

            Assert.Equal(new[] { "100 mm", "Window 600" }, vm.TypeRows.Select(r => r.Label));
            Assert.Equal("×2", vm.TypeRows[0].CountText);
            Assert.Equal("Partition 100", vm.TypeRows[0].Selected.Label);              // exact width
            Assert.Equal("New: CAD 600 x 1200 mm", vm.TypeRows[1].Selected.Label);     // no 600 window
            Assert.Equal("2 sizes", vm.TypesSummary);
            Assert.Equal("Missing sizes are made as new types.", vm.TypesNote);
        }

        [Fact]
        public async Task A_picked_type_goes_to_the_builder_and_survives_a_refresh()
        {
            var (vm, sink, _) = Make();
            vm.SetLevels(new[] { new LevelChoice { Name = "Level 1", Id = 1 } });
            vm.SetTypeCatalog(Types());
            await vm.OpenAsync(Dwg);
            vm.GoTo(CadToBimStep.Build);

            TypeRowViewModel walls = vm.TypeRows[0];
            walls.Selected = walls.Options.Single(o => o.Label == "Generic - 200mm");
            TypeRowViewModel window = vm.TypeRows[1];
            window.Selected = window.Options.Single(o => o.TypeId == 31);

            // Going away and back rebuilds the rows; the picks stay.
            vm.GoTo(CadToBimStep.Preview);
            vm.GoTo(CadToBimStep.Build);
            Assert.Equal("Generic - 200mm", vm.TypeRows[0].Selected.Label);

            vm.Confirm();

            List<TypeChoice> sent = sink.Request.TypeChoices;
            Assert.Equal(11, TypeChoice.Find(sent, TypeSlot.Wall, 100, double.NaN));
            Assert.Equal(31, TypeChoice.Find(sent, TypeSlot.Window, 600, 1200));
        }

        [Fact]
        public async Task A_new_file_build_sends_no_choices_and_says_why()
        {
            var (vm, sink, _) = Make();
            vm.SetTypeCatalog(Types());
            await vm.OpenAsync(Dwg);
            vm.GoTo(CadToBimStep.Build);
            vm.Target = BuildTarget.NewFile;

            Assert.Empty(vm.TypeRows);
            Assert.StartsWith("New file:", vm.TypesNote);

            vm.Confirm();
            Assert.Null(sink.Request.TypeChoices);
        }

        [Fact]
        public async Task Many_wall_widths_show_the_most_used_first_until_show_all()
        {
            var (vm, _, _) = Make();
            vm.SetTypeCatalog(Types());
            await vm.OpenAsync(Dwg);
            // Eight more widths, one wall each; 100 mm keeps its two.
            vm.Session.AddForced(Enumerable.Range(0, 8)
                .Select(i => CadOverlayViewportTests.WallAt(0, 10000 + i * 2000, 3000, 10000 + i * 2000, 150 + i * 10)));
            vm.GoTo(CadToBimStep.Build);

            int wallRows() => vm.TypeRows.Count(r => r.Plan.Slot == TypeSlot.Wall);
            Assert.Equal(BuildTypePlan.WallRowsShown, wallRows());
            Assert.Equal("100 mm", vm.TypeRows[0].Label);                 // most used, and thinnest
            Assert.Equal("Show 3 more wall widths", vm.MoreTypesText);
            Assert.Contains(vm.TypeRows, r => r.Label == "Window 600");     // openings always shown

            vm.ShowAllTypes = true;
            Assert.Equal(9, wallRows());
            Assert.Equal("Show fewer", vm.MoreTypesText);
        }

        [Fact]
        public void Review_warns_about_new_types_unsized_openings_and_skipped_joins()
        {
            var report = new BuildReport { UnsizedOpenings = 2, JoinsSkipped = true };
            report.NewWallTypeNames.AddRange(new[] { "CAD Wall 125 mm", "CAD Wall 230 mm" });
            report.NewOpeningTypeNames.Add("CAD 910 x 2100 mm");

            List<string> warnings = CadToBimViewModel.BuildWarnings(report, 0, 0, 0);

            Assert.Contains("New wall types made: CAD Wall 125 mm, CAD Wall 230 mm", warnings);
            Assert.Contains("New door/window types made: CAD 910 x 2100 mm", warnings);
            Assert.Contains("2 doors or windows keep their family's size (no type could be resized)", warnings);
            Assert.Contains("Wall joins rolled back: walls stand unjoined, as drawn", warnings);
        }

        // ───────── helpers ─────────

        // A window across the wall at x (two marks 600 mm apart).
        private static Cad2Bim.Opening WindowIn(CadWall host, double x) =>
            new Cad2Bim.Opening(
                new CadSegment(new CadPoint(x - 300, -60), new CadPoint(x - 300, 60)),
                new CadSegment(new CadPoint(x + 300, -60), new CadPoint(x + 300, 60)),
                host);
    }
}
