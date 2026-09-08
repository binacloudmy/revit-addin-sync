using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;
using CadWall = Cad2Bim.Wall;

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
    }
}
