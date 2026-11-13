// The pane's right (step) column: 280 px unless dragged, 260 px to 60% of the pane, the
// dragged width remembered per user in config.json ("cadToBim".StepColumnWidth).

using System;
using System.Collections.Generic;
using System.IO;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class CadToBimColumnTests : IDisposable
    {
        private readonly string _dir;

        public CadToBimColumnTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "bina_cadtobim_col_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            CadToBimSettings.ConfigPathOverride = Path.Combine(_dir, "config.json");
        }

        public void Dispose()
        {
            CadToBimSettings.ConfigPathOverride = null;
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private sealed class NullSink : IBuildRequestSink
        {
            public BuildRequest Request { get; set; }
            public event Action<BuildReport> Completed { add { } remove { } }
        }

        private sealed class NullRaiser : IBuildEventRaiser
        {
            public bool Raise() => true;
        }

        [Theory]
        [InlineData(null, 280)]
        [InlineData(320.0, 320)]
        [InlineData(200.0, 260)]
        [InlineData(double.NaN, 280)]
        [InlineData(0.0, 280)]
        public void Restore_opens_with_the_saved_width_or_280(double? saved, double expected)
        {
            Assert.Equal(expected, StepColumnLayout.Restore(saved));
        }

        [Theory]
        [InlineData(1000, 600)]
        [InlineData(520, 312)]
        [InlineData(300, 260)]   // never under the minimum
        public void Max_is_sixty_percent_of_the_pane(double pane, double expected)
        {
            Assert.Equal(expected, StepColumnLayout.MaxWidth(pane), 6);
        }

        [Fact]
        public void Max_is_unbounded_before_the_pane_has_a_size()
        {
            Assert.True(double.IsPositiveInfinity(StepColumnLayout.MaxWidth(0)));
            Assert.True(double.IsPositiveInfinity(StepColumnLayout.MaxWidth(double.NaN)));
        }

        [Fact]
        public void A_drag_is_saved_in_whole_pixels_and_restored()
        {
            var saves = new List<CadToBimSettings>();
            var settings = new CadToBimSettings();
            var vm = new CadToBimViewModel(new NullSink(), new NullRaiser(), settings, saveSettings: s => saves.Add(s));
            Assert.Equal(280, vm.StepColumnWidth);

            vm.RememberStepColumnWidth(341.6);
            Assert.Equal(342, settings.StepColumnWidth);
            Assert.Single(saves);
            Assert.Equal(342, vm.StepColumnWidth);

            vm.RememberStepColumnWidth(342.2);   // same whole width: no second write
            Assert.Single(saves);
        }

        [Fact]
        public void The_width_round_trips_through_config_json()
        {
            var settings = new CadToBimSettings { StepColumnWidth = 360 };
            settings.Save();
            Assert.Equal(360, CadToBimSettings.Load().StepColumnWidth);

            Assert.Null(new CadToBimSettings().StepColumnWidth);
        }
    }
}
