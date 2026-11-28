// AI-642 step 1: Build (screens 8-9) and Review (10, 12) on the one CheckState. Real ASWARA snapshot;
// the reader's walls are synthetic (the DWG is not in the repo), the counts the pane shows come from the report.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim.Check;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class BuildReviewTests
    {
        private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", "cad2bim", name);

        private sealed class FakeSink : IBuildRequestSink
        {
            public BuildRequest? Request { get; set; }
            public event Action<BuildReport>? Completed;
            public void Fire(BuildReport report) { Request = null; Completed?.Invoke(report); }
        }

        private sealed class FakeRaiser : IBuildEventRaiser
        {
            public int Calls;
            public bool Accept = true;
            public bool Raise() { Calls++; return Accept; }
        }

        private sealed class FakeView : IRevitView
        {
            public readonly List<string> Calls = new List<string>();
            public List<long>? Watched;
            public void ZoomTo(BoxMm box) => Calls.Add("zoom");
            public void ShowUnderlay(string dwgPath, double mmPerUnit, BoxMm mainPlan, BoxMm? drawingExtents) => Calls.Add("link");
            public void RemoveUnderlay() => Calls.Add("unlink");
            public void Show(IEnumerable<long> elementIds) => Calls.Add("show " + string.Join(",", elementIds));
            public void Watch(IEnumerable<long>? elementIds) => Watched = elementIds?.ToList();
            public event Action<string>? Failed;
            public event Action? Linked;
            public event Action<string>? Notice;
            public event Action<double>? OutsideCrop;
            public event Action<bool>? RunPresent;
            public void Present(bool present) => RunPresent?.Invoke(present);
            public void Unused() { Failed?.Invoke(""); Linked?.Invoke(); Notice?.Invoke(""); OutsideCrop?.Invoke(0); }
        }

        private static Wall WallAt(double x, double y, double length = 3000) =>
            new(new Segment(new Point(x, y), new Point(x + length, y)), new Segment(new Point(x, y + 100), new Point(x + length, y + 100)));

        /// <summary>ASWARA's snapshot with a reader sheet behind it: the main plan (ARAS 1, N 2) holds 3 walls, ARAS 2 (N 1) holds 2.</summary>
        private static SheetSnapshot Aswara(out DwgPlan main, out DwgPlan other)
        {
            SheetSnapshot s = SheetSnapshot.FromJson(File.ReadAllText(Data("aswara-sheet.json")));
            main = new DwgPlan { N = 2, Cluster = new PlanCluster() };
            foreach (Wall w in new[] { WallAt(0, 0), WallAt(0, 4000), WallAt(0, 8000) }) main.Cluster.Add(w);
            other = new DwgPlan { N = 1, Cluster = new PlanCluster() };
            foreach (Wall w in new[] { WallAt(900000, 0), WallAt(900000, 4000) }) other.Cluster.Add(w);
            s.Source = new DwgPlanSheet { Plans = { other, main } };
            return s;
        }

        private static TargetInfo Target(bool workshared = false) => new TargetInfo
        {
            DocTitle = "ASWARA_ARCH", Workshared = workshared,
            Levels = { new LevelInfo { Name = "Level 1", ElevationMm = 0, Id = 311 }, new LevelInfo { Name = "Level 2", ElevationMm = 3500, Id = 312 } },
        };

        /// <summary>Through Check to the Rows screen (no fixture: 0 questions), with a fake builder wired.</summary>
        private static (CheckViewModel vm, FakeSink sink, FakeRaiser raiser, FakeView view) Ready(bool workshared = false)
        {
            var view = new FakeView();
            var vm = new CheckViewModel(_ => throw new InvalidOperationException("no read in this test"), view);
            vm.Show(Aswara(out _, out _), Target(workshared));
            for (int i = 0; i < 4; i++) vm.Next();
            Assert.Equal(CheckScreen.Rows, vm.Screen);
            var sink = new FakeSink();
            var raiser = new FakeRaiser();
            vm.UseBuilder(sink, raiser);
            return (vm, sink, raiser, view);
        }

        private static BuildReport Report(params long[] wallIds)
        {
            var r = new BuildReport { Doors = 21, Windows = 4, Rooms = 0 };
            foreach (long id in wallIds) r.WallIds[WallAt(id, 0)] = id;
            r.Walls = r.WallIds.Count;
            return r;
        }

        // ── the request (FRIDAY fix 4) ──

        [Fact]
        public void Build_sends_only_the_main_plan_as_walls_and_layout_kept_in_place_on_the_main_level()
        {
            var (vm, sink, raiser, view) = Ready();
            DwgPlan main = vm.State.Sheet.Source.Plans.Single(p => p.N == 2);

            vm.Build();

            BuildRequest r = Assert.IsType<BuildRequest>(sink.Request);
            Assert.Equal(1, raiser.Calls);
            Assert.Equal(main.Cluster.Walls, r.Walls);
            Assert.Same(r.Walls, r.LayoutWalls);                        // KeepPosition places by LayoutWalls ?? Walls: never the sheet
            Assert.Equal(StoreyMode.KeepPosition, r.Storeys);
            Assert.Equal(BuildTarget.AddToProject, r.Target);
            Assert.Equal((long?)311, r.LevelId);                                // the existing main level, by id
            Assert.Equal("CAD to BIM: Level 1", r.GroupName);           // the one undo step
            Assert.Equal(vm.State.WallHeightMm, r.HeightMm);
            Assert.Equal(CheckScreen.Building, vm.Screen);
            Assert.Equal("Level 1 · Building · 277 walls, 25 doors & windows", vm.ProgressText);
            Assert.Equal("Undo history: \"CAD to BIM: Level 1\".", vm.UndoLine);
            Assert.Contains("zoom", view.Calls);                         // Follow in view
            Assert.Equal("CAD to BIM · Build · building Level 1", vm.StatusLine);
            Assert.Equal(1, vm.Phase);
        }

        [Fact]
        public void KeepPosition_places_one_cluster_on_the_base_level_so_no_level_is_created()
        {
            var walls = new List<Wall> { WallAt(0, 0), WallAt(0, 4000), WallAt(900000, 0), WallAt(900000, 4000) };
            var keep = Cad2BimBuilder.ClusterAndPlace(new BuildRequest { Walls = walls, Storeys = StoreyMode.KeepPosition });
            var placement = Assert.Single(keep);
            Assert.Equal(0, placement.LevelIndex);                       // LevelFor(index 0) returns the base level: Level.Create is never reached
            Assert.Equal((0.0, 0.0), (placement.OriginX, placement.OriginY));
        }

        [Fact]
        public void Legacy_keeps_its_undo_name_and_the_check_pane_names_the_level()
        {
            Assert.Null(new BuildRequest().GroupName);
            Assert.Equal("CAD to BIM", Cad2BimBuilder.GroupNameFor(new BuildRequest()));
            Assert.Equal("CAD to BIM: Level 1", Cad2BimBuilder.GroupNameFor(new BuildRequest { GroupName = "CAD to BIM: Level 1" }));
        }

        // ── workshared (FRIDAY fix 3) ──

        [Fact]
        public void A_workshared_model_blocks_build_with_one_line()
        {
            var (vm, sink, raiser, _) = Ready(workshared: true);
            Assert.False(vm.CanBuild);
            Assert.Equal("Build isn't available in a workshared model yet (needs Remove this run)", vm.BuildBlockLine);
            vm.Build();
            Assert.Null(sink.Request);
            Assert.Equal(0, raiser.Calls);
            Assert.Equal(CheckScreen.Rows, vm.Screen);
        }

        // ── no double build (FRIDAY fix 1) ──

        [Fact]
        public void A_built_run_blocks_a_second_build_until_ctrl_z_removes_its_walls()
        {
            var (vm, sink, raiser, view) = Ready();
            vm.Build();
            sink.Fire(Report(101, 102, 103));

            Assert.Equal(CheckScreen.Built, vm.Screen);
            Assert.Equal(new long[] { 101, 102, 103 }, view.Watched!.OrderBy(x => x));
            Assert.Equal("Level 1 · done · 3 walls · 21 doors · 4 windows", vm.DoneText);

            vm.Back();                                                   // back on Rows: Build is off
            Assert.False(vm.CanBuild);
            Assert.Equal("Built · undo with Ctrl+Z to rebuild", vm.BuildButtonText);
            Assert.Null(vm.BuildBlockLine);
            vm.Build();
            Assert.Equal(1, raiser.Calls);

            view.Present(false);                                         // Ctrl+Z: every wall of the run is gone
            Assert.Equal(CheckScreen.Rows, vm.Screen);
            Assert.True(vm.CanBuild);
            Assert.StartsWith("Build Level 1", vm.BuildButtonText);

            view.Present(true);                                          // redo: built again
            Assert.False(vm.CanBuild);
        }

        [Fact]
        public void Reopening_the_file_ends_the_old_run()
        {
            var (vm, sink, _, _) = Ready();
            vm.Build();
            sink.Fire(Report(101));
            vm.Show(Aswara(out _, out _), Target());
            Assert.Null(vm.State.Run);
            Assert.Null(vm.State.BuildBlock);
        }

        [Fact]
        public void A_failed_build_is_rolled_back_and_build_stays_allowed()
        {
            var (vm, sink, _, view) = Ready();
            vm.Build();
            sink.Fire(new BuildReport { Error = "This model has no basic wall type to use." });
            Assert.Equal(CheckScreen.Built, vm.Screen);
            Assert.True(vm.RunFailed);
            Assert.Equal("This model has no basic wall type to use.", vm.RunError);
            Assert.Null(view.Watched);
            vm.Next();                                                   // no Review for a failed run
            Assert.Equal(CheckScreen.Built, vm.Screen);
            vm.Back();
            Assert.True(vm.CanBuild);
        }

        [Fact]
        public void A_refused_raise_adds_nothing_and_says_revit_is_busy()
        {
            var (vm, sink, raiser, _) = Ready();
            raiser.Accept = false;
            vm.Build();
            Assert.Null(sink.Request);
            Assert.True(vm.RunFailed);
            Assert.Contains("Revit is busy", vm.RunError);
        }

        [Fact]
        public void A_second_plan_mapped_onto_the_build_level_blocks_build()
        {
            var (vm, sink, _, _) = Ready();
            vm.Map(vm.State.Plans.Single(p => p.Plan.Level == "ARAS 2"), "Level 1");
            Assert.False(vm.CanBuild);
            Assert.StartsWith("Only the main plan is built for now", vm.BuildBlockLine);
            vm.Build();
            Assert.Null(sink.Request);
        }

        // ── honest warnings (FRIDAY fix 2) ──

        [Fact]
        public void The_failure_log_groups_by_definition_and_keeps_resolved_errors_apart()
        {
            var log = new FailureLog();
            Guid join = Guid.NewGuid(), overlap = Guid.NewGuid();
            log.Add(join, "Walls cannot be joined", join: true, error: false, new long[] { 1, 2 });
            log.Add(join, "Walls cannot be joined", join: true, error: false, new long[] { 2, 3 });
            log.Add(overlap, "Highlighted walls overlap", join: false, error: false, new long[] { 4 });
            log.Add(overlap, "Highlighted walls overlap", join: false, error: true, new long[] { 5, 6 });

            Assert.Equal(3, log.Groups.Count);
            Assert.Equal(2, log.JoinWarnings);
            Assert.Equal(1, log.OtherWarnings);
            Assert.Equal(1, log.ResolvedErrors);
            Assert.Equal(new long[] { 1, 2, 3 }, log.Groups[0].ElementIds);
            Assert.Equal(new long[] { 5, 6 }, log.ResolvedErrorIds);
        }

        [Fact]
        public void Warnings_reach_the_build_banner_and_review_with_the_same_counts()
        {
            var (vm, sink, _, view) = Ready();
            vm.Build();
            BuildReport r = Report(101, 102);
            for (int i = 0; i < 14; i++) r.Failures.Add(new Guid("11111111-1111-1111-1111-111111111111"), "Walls cannot be joined", true, false, new long[] { 101 });
            r.Failures.Add(new Guid("22222222-2222-2222-2222-222222222222"), "Room is not enclosed", false, false, new long[0]);
            r.Failures.Add(new Guid("33333333-3333-3333-3333-333333333333"), "Wall is too short", false, true, new long[] { 102 });
            r.SkippedOpenings = 2;
            sink.Fire(r);

            var b = vm.Banners;
            Assert.Equal(4, b.Count);
            Assert.Equal("14 join warnings", b[0].Bold);
            Assert.Equal(" → added to Review, not deleted.", b[0].Post);
            Assert.Equal("1 other warning", b[1].Bold);
            Assert.Equal("r", b[2].Tone);
            Assert.Contains("2 openings", b[3].Post);

            vm.Next();
            Assert.Equal(CheckScreen.Review, vm.Screen);
            Assert.Equal(2, vm.Phase);
            Assert.Contains("14 join warnings", vm.AlsoLine);
            Assert.Contains("1 other warning", vm.AlsoLine);
            Assert.Contains("1 resolved error", vm.AlsoLine);
            Assert.Equal("resolved error", vm.FailureRows[0].Kind);   // errors first
            Assert.Equal("14 × Walls cannot be joined", vm.FailureRows[1].Text);
            vm.Show(vm.FailureRows[1]);
            Assert.Contains("show 101", view.Calls);
            Assert.False(vm.FailureRows.Single(f => f.Group.Text == "Room is not enclosed").CanShow);
        }

        // ── review and accept from the one report ──

        [Fact]
        public void Review_and_accept_show_the_reports_counts_and_pending_parts_stay_neutral()
        {
            var (vm, sink, _, _) = Ready();
            vm.Build();
            sink.Fire(Report(101, 102, 103));
            vm.Next();
            Assert.Equal("3 walls · 21 doors · 4 windows · 0 rooms built", vm.TotalsLine);
            Assert.Equal("Also: 1 assumption from Check", vm.AlsoLine);   // scale from the header only
            Assert.Equal("CAD to BIM · Review", vm.StatusLine);

            vm.Next();
            Assert.Equal(CheckScreen.Accept, vm.Screen);
            Assert.Equal(new[] { "Walls 3", "Doors 21", "Windows 4", "Rooms 0" }, vm.AcceptRows.Select(a => a.Bold));
            Assert.Equal("none found on this plan", vm.AcceptRows[3].Note);
            Assert.Equal("Accept isn't available yet (needs-you queue)", BuildText.AcceptPending);
            Assert.Equal("on", vm.StepReview);
            Assert.Equal("✓ 2 Build", vm.StepBuildText);
            vm.Back();
            Assert.Equal(CheckScreen.Review, vm.Screen);
        }

        [Fact]
        public void Without_a_builder_build_only_raises_the_event()
        {
            var vm = new CheckViewModel(_ => throw new InvalidOperationException());
            vm.Show(Aswara(out _, out _), Target());
            for (int i = 0; i < 4; i++) vm.Next();
            CheckState? seen = null;
            vm.BuildRequested += s => seen = s;
            vm.Build();
            Assert.NotNull(seen);
            Assert.Null(vm.State.Run);
            Assert.Equal(CheckScreen.Rows, vm.Screen);
        }
    }
}
