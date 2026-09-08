using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;
using CadWall = Cad2Bim.Wall;
using CadSegment = Cad2Bim.Segment;

namespace Tests
{
    // The view model is driven here without Revit: the build seam is two interfaces, detection is
    // a delegate, and "marshal to the UI thread" runs inline because no WPF Application exists.
    [Collection("Cad2Bim")]
    public class CadToBimViewModelTests
    {
        private sealed class FakeSink : IBuildRequestSink
        {
            public BuildRequest Request { get; set; }
            public event Action<BuildReport> Completed;
            public void Fire(BuildReport report) => Completed?.Invoke(report);
        }

        private sealed class FakeRaiser : IBuildEventRaiser
        {
            public int Calls;
            public bool Accept = true;
            public bool Raise() { Calls++; return Accept; }
        }

        private const string Dwg = "/tmp/taman-desa/A-101 Unit Plan.dwg";

        private static DetectResult TwoWalls(string path)
        {
            var session = new CadToBimSession
            {
                DrawingPath = path,
                Scale = 1.0,
                Model = new Cad2Bim.CadModel(),
                MedianThicknessMm = 100,
            };
            session.Walls.Add(CadOverlayViewportTests.WallAt(0, 0, 9000, 0));
            session.Walls.Add(CadOverlayViewportTests.WallAt(0, 4300, 4200, 4300));
            return new DetectResult
            {
                Session = session,
                LayerShapes = new Dictionary<string, List<object>>(),
                Census = new Dictionary<string, int> { ["A-WALL"] = 4 },
                Bounds = new System.Windows.Rect(0, 0, 9000, 4300),
                PlanCount = 1,
                Entities = 4,
                ReadSeconds = 0.01,
                UnitsText = "1 unit = 1 mm · header",
            };
        }

        private static (CadToBimViewModel vm, FakeSink sink, FakeRaiser raiser) Make()
        {
            var sink = new FakeSink();
            var raiser = new FakeRaiser();
            var vm = new CadToBimViewModel(sink, raiser, new CadToBimSettings(),
                (path, settings, sMin, sMax, roles, progress, ct) => TwoWalls(path));
            return (vm, sink, raiser);
        }

        [Fact]
        public void Confirm_without_a_session_asks_for_a_dwg()
        {
            var (vm, _, raiser) = Make();
            vm.Confirm();
            Assert.Equal("Open a DWG first", vm.Status);
            Assert.Equal(0, raiser.Calls);
            Assert.False(vm.Busy);
        }

        [Fact]
        public async Task Open_fills_counts_and_the_confirm_label()
        {
            var (vm, _, _) = Make();
            await vm.OpenAsync(Dwg);
            Assert.True(vm.HasSession);
            Assert.False(vm.Busy);
            Assert.Equal(2, vm.WallCount);
            Assert.Equal("A-101 Unit Plan.dwg", vm.DrawingName);
            Assert.Equal("Confirm — build 2 walls", vm.ConfirmLabel);
            Assert.True(vm.CanConfirm);
            Assert.Single(vm.Layers);
        }

        [Fact]
        public async Task Confirm_with_pending_walls_raises_once_and_locks_the_pane()
        {
            var (vm, sink, raiser) = Make();
            await vm.OpenAsync(Dwg);

            vm.Confirm();

            Assert.Equal(1, raiser.Calls);
            Assert.True(vm.Busy);
            Assert.NotNull(sink.Request);
            Assert.Equal(2, sink.Request.Walls.Count);
            Assert.Equal(BuildTarget.AddToProject, sink.Request.Target);
            Assert.Null(sink.Request.OutputPath);
            Assert.Equal(Dwg, sink.Request.DrawingPath);

            vm.Confirm();                       // refused while a build is in flight
            Assert.Equal(1, raiser.Calls);
        }

        [Fact]
        public async Task Build_error_lands_in_status_and_unlocks()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();

            sink.Fire(new BuildReport { Error = "This model has no basic wall type to use." });

            Assert.False(vm.Busy);
            Assert.Contains("no basic wall type", vm.Status);
            Assert.Equal(2, vm.WallCount);      // nothing was marked built
        }

        [Fact]
        public async Task Refused_raise_says_revit_is_busy()
        {
            var (vm, _, raiser) = Make();
            raiser.Accept = false;
            await vm.OpenAsync(Dwg);

            vm.Confirm();

            Assert.Equal("Revit is busy, try again", vm.Status);
            Assert.False(vm.Busy);
        }

        [Fact]
        public async Task New_file_target_puts_the_rvt_beside_the_dwg()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Target = BuildTarget.NewFile;

            vm.Confirm();

            Assert.Equal(Path.ChangeExtension(Dwg, ".rvt"), sink.Request.OutputPath);
            Assert.Equal(BuildTarget.NewFile, sink.Request.Target);
            Assert.Null(sink.Request.LevelId);
        }

        // finding F2: a successful NewFile build saves + activates the new document, so further
        // Confirms in the same session build into it rather than trying to save a second new file.
        [Fact]
        public async Task Successful_new_file_build_flips_target_to_add_to_project()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Target = BuildTarget.NewFile;
            vm.Confirm();

            sink.Fire(new BuildReport { OutputPath = @"C:\taman-desa\A-101 Unit Plan.rvt" });

            Assert.Equal(BuildTarget.AddToProject, vm.Target);
            Assert.Contains("further Confirms add to this project", vm.Status);
        }

        // finding F2, negative: a failed NewFile build leaves Target alone (nothing was saved,
        // there is nothing to switch into).
        [Fact]
        public async Task Failed_new_file_build_leaves_target_on_new_file()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Target = BuildTarget.NewFile;
            vm.Confirm();

            sink.Fire(new BuildReport { Error = "This model has no basic wall type to use." });

            Assert.Equal(BuildTarget.NewFile, vm.Target);
        }

        // finding F5: the builder's NewFile save-failure message is already the drafter's next
        // move; the VM must show it verbatim rather than burying it under "Build failed: ".
        [Fact]
        public async Task New_file_save_failure_surfaces_the_builder_message_verbatim()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Target = BuildTarget.NewFile;
            vm.Confirm();

            const string error = "could not save C:\\taman-desa\\A-101 Unit Plan.rvt: The process cannot " +
                                  "access the file because it is being used by another process. — build " +
                                  "into the open project instead, or free the file and Confirm again";
            sink.Fire(new BuildReport { Error = error });

            Assert.Equal(error, vm.Status);           // not "Build failed: " + error
            Assert.Equal(BuildTarget.NewFile, vm.Target);
        }

        // finding F1: a second Confirm (after a build) must cluster/place against the FULL
        // layout (LayoutWalls == Active()), not just the pending subset, so appended walls land
        // on the plan the first build already placed instead of shifting to the origin. Walls
        // stays the pending-only creation list.
        [Fact]
        public async Task Second_confirm_carries_the_full_layout_and_only_the_pending_walls()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();
            sink.Fire(new BuildReport
            {
                WallIds = new Dictionary<CadWall, long>
                {
                    [vm.Session.Walls[0]] = 101,
                    [vm.Session.Walls[1]] = 102,
                },
            });

            CadWall brushed = CadOverlayViewportTests.WallAt(0, 8000, 2000, 8000);
            vm.Session.AddForced(new[] { brushed });

            vm.Confirm();

            Assert.Equal(3, sink.Request.LayoutWalls.Count);
            Assert.Equal(vm.Session.Active().Count, sink.Request.LayoutWalls.Count);
            Assert.Single(sink.Request.Walls);
            Assert.Same(brushed, sink.Request.Walls[0]);
            Assert.Equal(vm.Session.Pending().Count, sink.Request.Walls.Count);
        }

        // finding F3: the request carries the settings' window sill, not a hardcoded 900 mm.
        [Fact]
        public async Task Confirm_request_carries_the_settings_window_sill()
        {
            var settings = new CadToBimSettings { WindowSillMm = 950 };
            var sink = new FakeSink();
            var vm = new CadToBimViewModel(sink, new FakeRaiser(), settings,
                (path, s, sMin, sMax, roles, progress, ct) => TwoWalls(path));
            await vm.OpenAsync(Dwg);

            vm.Confirm();

            Assert.Equal(950, sink.Request.WindowSillMm);
        }

        // finding F3: the same critical section that sets Wall.SMin/SMax also sets the engine's
        // door-swing radius band from CadToBimSettings.DoorMinRadiusMm/DoorMaxRadiusMm, so the
        // settings window's door-radius fields (previously dead) actually reach the classifier.
        [Fact]
        public void ApplyEngineThresholds_sets_the_wall_and_door_swing_statics_from_settings()
        {
            double savedSMin = CadWall.SMin, savedSMax = CadWall.SMax;
            double savedDoorMin = Cad2Bim.CadClassifier.SwingMinRadiusMm;
            double savedDoorMax = Cad2Bim.CadClassifier.SwingMaxRadiusMm;
            try
            {
                var settings = new CadToBimSettings { DoorMinRadiusMm = 444, DoorMaxRadiusMm = 1666 };

                CadToBimViewModel.ApplyEngineThresholds(settings, 60, 500);

                Assert.Equal(60, CadWall.SMin);
                Assert.Equal(500, CadWall.SMax);
                Assert.Equal(444, Cad2Bim.CadClassifier.SwingMinRadiusMm);
                Assert.Equal(1666, Cad2Bim.CadClassifier.SwingMaxRadiusMm);
            }
            finally
            {
                CadWall.SMin = savedSMin;
                CadWall.SMax = savedSMax;
                Cad2Bim.CadClassifier.SwingMinRadiusMm = savedDoorMin;
                Cad2Bim.CadClassifier.SwingMaxRadiusMm = savedDoorMax;
            }
        }

        // finding F6: a door-layer line crossing a wall must never become a window; only
        // WindowLayerHints layers (win/window/tingkap/glaz/wdw) may.
        [Fact]
        public void Elaborate_reads_window_marks_only_from_window_hinted_layers_not_door_layers()
        {
            var settings = new CadToBimSettings();
            CadWall wall = CadOverlayViewportTests.WallAt(0, 0, 4000, 0);
            var walls = new List<CadWall> { wall };

            var model = new Cad2Bim.CadModel();
            model.Segments.Add(new CadSegment(new Cad2Bim.Point(1700, -300), new Cad2Bim.Point(1700, 300)) { Layer = "A-DOOR" });
            model.Segments.Add(new CadSegment(new Cad2Bim.Point(2300, -300), new Cad2Bim.Point(2300, 300)) { Layer = "A-DOOR" });

            var (doorLayerOpenings, _) = CadToBimViewModel.Elaborate(model, walls, settings, new Dictionary<string, LayerRole>());
            Assert.Empty(doorLayerOpenings);

            model.Segments.Clear();
            model.Segments.Add(new CadSegment(new Cad2Bim.Point(1700, -300), new Cad2Bim.Point(1700, 300)) { Layer = "A-GLAZ" });
            model.Segments.Add(new CadSegment(new Cad2Bim.Point(2300, -300), new Cad2Bim.Point(2300, 300)) { Layer = "A-GLAZ" });

            var (glazLayerOpenings, _) = CadToBimViewModel.Elaborate(model, walls, settings, new Dictionary<string, LayerRole>());
            Assert.Single(glazLayerOpenings);
            Assert.False(glazLayerOpenings[0].IsDoor);
        }

        [Fact]
        public async Task Erase_drops_the_wall_count_by_one_and_restores_on_second_click()
        {
            var (vm, _, _) = Make();
            await vm.OpenAsync(Dwg);
            CadWall first = vm.Session.Walls[0];

            vm.ToggleErase(first);
            Assert.Equal(1, vm.WallCount);
            Assert.Equal(1, vm.ErasedCount);
            Assert.Single(vm.Overlay.Erased);
            Assert.Equal("Confirm — build 1 wall", vm.ConfirmLabel);

            vm.ToggleErase(first);
            Assert.Equal(2, vm.WallCount);
            Assert.Equal(0, vm.ErasedCount);
        }

        [Fact]
        public async Task Erased_walls_are_left_out_of_the_build_request()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.ToggleErase(vm.Session.Walls[1]);

            vm.Confirm();

            Assert.Single(sink.Request.Walls);
            Assert.Same(vm.Session.Walls[0], sink.Request.Walls[0]);
        }

        // finding F7: a room that gained one brushed wall while its other wall was already
        // built must not go into the next Confirm's Spaces (it would be created twice) - only a
        // space whose walls are ALL pending qualifies.
        [Fact]
        public async Task Space_with_one_already_built_wall_is_excluded_from_the_next_confirm()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);

            var report = new BuildReport();
            report.WallIds[vm.Session.Walls[0]] = 5;
            vm.Session.MarkBuilt(report);      // Walls[0] built; Walls[1] still pending

            var mixedSpace = new Cad2Bim.Space(new List<Cad2Bim.Point>(), new List<CadWall> { vm.Session.Walls[0], vm.Session.Walls[1] });
            vm.Session.Spaces.Add(mixedSpace);

            vm.Confirm();

            Assert.DoesNotContain(mixedSpace, sink.Request.Spaces);
        }

        [Fact]
        public async Task Detect_failure_puts_the_innermost_message_in_status()
        {
            var sink = new FakeSink();
            var vm = new CadToBimViewModel(sink, new FakeRaiser(), new CadToBimSettings(),
                (path, settings, sMin, sMax, roles, progress, ct) =>
                    throw new InvalidOperationException("outer", new IOException("Header is not a DWG")));

            await vm.OpenAsync(Dwg);

            Assert.False(vm.Busy);
            Assert.False(vm.HasSession);
            Assert.StartsWith("Header is not a DWG", vm.Status);
        }

        [Fact]
        public void Role_defaults_come_from_the_settings_hints()
        {
            var settings = new CadToBimSettings();
            var none = new Dictionary<string, LayerRole>();
            Assert.Equal(LayerRole.Wall, CadToBimViewModel.RoleOf("A-WALL", settings, none));
            Assert.Equal(LayerRole.Wall, CadToBimViewModel.RoleOf("Dinding-Bata", settings, none));
            Assert.Equal(LayerRole.Opening, CadToBimViewModel.RoleOf("A-GLAZ", settings, none));
            Assert.Equal(LayerRole.Ignore, CadToBimViewModel.RoleOf("PERABUT", settings, none));
            Assert.Equal(LayerRole.Ignore, CadToBimViewModel.RoleOf("A-DIMS", settings, none));
            Assert.Equal(LayerRole.Other, CadToBimViewModel.RoleOf("0", settings, none));
            var forced = new Dictionary<string, LayerRole> { ["0"] = LayerRole.Wall };
            Assert.Equal(LayerRole.Wall, CadToBimViewModel.RoleOf("0", settings, forced));
        }

        [Fact]
        public void Civil3d_and_aec_classes_are_refused_with_the_exporttoautocad_text()
        {
            Assert.Null(CadToBimViewModel.UnsupportedSource(null));
            Assert.Null(CadToBimViewModel.UnsupportedSource(new[] { "ACDBPLACEHOLDER", "SCALE", "TABLESTYLE" }));
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, CadToBimViewModel.UnsupportedSource(new[] { "SCALE", "AECC_PIPE" }));
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, CadToBimViewModel.UnsupportedSource(new[] { "aec_wall" }));   // case-insensitive, like CadFileReader
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, CadToBimViewModel.UnsupportedSource(new[] { "AECB_DUCT" }));
            Assert.Contains("EXPORTTOAUTOCAD", CadToBimViewModel.UnsupportedSourceMessage);
        }

        [Fact]
        public async Task Unsupported_source_lands_in_status_verbatim_and_leaves_no_session()
        {
            // Detect throws exactly what the real Detect throws for a Civil 3D file.
            var vm = new CadToBimViewModel(new FakeSink(), new FakeRaiser(), new CadToBimSettings(),
                (path, settings, sMin, sMax, roles, progress, ct) =>
                    throw new NotSupportedException(CadToBimViewModel.UnsupportedSourceMessage));

            await vm.OpenAsync(Dwg);

            Assert.False(vm.Busy);
            Assert.False(vm.HasSession);
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, vm.Status);   // not doubled by Describe
            Assert.False(vm.CanConfirm);
        }

        [Fact]
        public async Task A_second_open_never_runs_the_engine_while_the_first_is_still_inside_it()
        {
            // Detect and Brush both set Cad2Bim.Wall's process-wide static thresholds before
            // reading a drawing. OpenAsync cancels the previous open's token but does not wait
            // for its Task.Run to finish, so without a gate two engine calls could run on
            // different thread-pool threads at once. This fake engine call blocks the FIRST
            // invocation until the test releases it, and counts how many invocations are ever
            // inside the delegate at the same time.
            int concurrent = 0, maxConcurrent = 0, calls = 0;
            var maxLock = new object();
            var firstEntered = new ManualResetEventSlim(false);
            var releaseFirst = new ManualResetEventSlim(false);

            DetectResult SlowDetect(string path)
            {
                int running = Interlocked.Increment(ref concurrent);
                lock (maxLock)
                {
                    if (running > maxConcurrent) maxConcurrent = running;
                }
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstEntered.Set();
                    Assert.True(releaseFirst.Wait(2000), "test never released the first call");
                }
                Interlocked.Decrement(ref concurrent);
                return TwoWalls(path);
            }

            var sink = new FakeSink();
            var vm = new CadToBimViewModel(sink, new FakeRaiser(), new CadToBimSettings(),
                (path, settings, sMin, sMax, roles, progress, ct) => SlowDetect(path));

            Task first = vm.OpenAsync(Dwg);
            Assert.True(firstEntered.Wait(2000), "first open never reached the engine call");

            Task second = vm.OpenAsync(Dwg);          // supersedes the first; must wait its turn
            await Task.Delay(50);                     // give the second call a chance to queue up

            releaseFirst.Set();
            await Task.WhenAll(first, second);

            Assert.Equal(1, maxConcurrent);            // never both inside the engine call at once
            Assert.Equal(2, calls);
            Assert.True(vm.HasSession);
            Assert.False(vm.Busy);
        }
    }
}
