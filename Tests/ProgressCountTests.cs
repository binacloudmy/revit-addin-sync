using System.Collections.ObjectModel;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The determinate-scan pipeline (PRD A1/A2): the "progress" wire
    /// event through AIServiceStreamExtensions.ParseEvent, and count frames
    /// through ProgressReducer.ApplyCount — clamp rules, freeze-on-done, and
    /// the legacy fallbacks.</summary>
    public class ProgressCountTests
    {
        // ─── Parser (A1) ─────────────────────────────────────────────────────

        [Fact]
        public void Parse_FullProgressEvent()
        {
            var c = AIServiceStreamExtensions.ParseEvent("progress",
                "{\"step_id\":\"t2\",\"tool\":\"find_elements_by_filter\",\"current\":36,\"total\":62,\"unit\":\"elements\",\"label\":\"Scanning elements…\",\"segment\":\"leg-1\"}");
            Assert.Equal(StreamChunkKind.Progress, c.Kind);
            Assert.Equal("t2", c.StepId);
            Assert.Equal("find_elements_by_filter", c.ToolName);
            Assert.Equal(36, c.Current);
            Assert.Equal(62, c.Total);
            Assert.Equal("elements", c.Unit);
            Assert.Equal("Scanning elements…", c.StatusLabel);
            Assert.Equal("leg-1", c.Segment);
        }

        [Fact]
        public void Parse_MinimalProgressEvent_NoTotal()
        {
            var c = AIServiceStreamExtensions.ParseEvent("progress", "{\"step_id\":\"t1\",\"current\":5}");
            Assert.Equal(StreamChunkKind.Progress, c.Kind);
            Assert.Equal(5, c.Current);
            Assert.Equal(-1, c.Total);      // counter-only mode
            Assert.Equal("", c.Unit);
        }

        [Fact]
        public void Parse_ProgressFallsBackToToolAsStepId()
        {
            var c = AIServiceStreamExtensions.ParseEvent("progress", "{\"tool\":\"count_by\",\"current\":3}");
            Assert.Equal(StreamChunkKind.Progress, c.Kind);
            Assert.Equal("count_by", c.StepId);
        }

        [Fact]
        public void Parse_ProgressWithoutAnyId_IsUnknown()
        {
            var c = AIServiceStreamExtensions.ParseEvent("progress", "{\"current\":3}");
            Assert.Equal(StreamChunkKind.Unknown, c.Kind);
        }

        [Fact]
        public void Parse_GarbageProgress_IsUnknownNotThrow()
        {
            var c = AIServiceStreamExtensions.ParseEvent("progress", "{not json");
            Assert.Equal(StreamChunkKind.Unknown, c.Kind);
        }

        // ─── Reducer (A2) ────────────────────────────────────────────────────

        private static ObservableCollection<ProgressStep> Trail() => new ObservableCollection<ProgressStep>();

        [Fact]
        public void ApplyCount_OnExistingRow_SetsCountAndBar()
        {
            var t = Trail();
            ProgressReducer.Apply(t, "t2", "executing", "Scanning…", "", StepState.Running);
            ProgressReducer.ApplyCount(t, "t2", 36, 62, "elements", "");
            Assert.Single(t);
            Assert.Equal(36, t[0].Current);
            Assert.Equal(62, t[0].Total);
            Assert.Equal("36 / 62 elements", t[0].CountText);
            Assert.Equal(36.0 / 62.0, t[0].Fraction, 5);
            Assert.Equal("Scanning…", t[0].Label);   // empty label preserves the richer one
        }

        [Fact]
        public void ApplyCount_UnknownStepId_OpensRunningRow()
        {
            var t = Trail();
            ProgressReducer.ApplyCount(t, "t9", 1, 10, "", "Scanning elements…");
            Assert.Single(t);
            Assert.Equal(StepState.Running, t[0].State);
            Assert.Equal("Scanning elements…", t[0].Label);
        }

        [Fact]
        public void ApplyCount_NeverRegresses_AndTotalSticks()
        {
            var t = Trail();
            ProgressReducer.ApplyCount(t, "t2", 40, 62, "elements", "");
            ProgressReducer.ApplyCount(t, "t2", 36, -1, "", "");     // late/out-of-order frame
            Assert.Equal(40, t[0].Current);
            Assert.Equal(62, t[0].Total);
            Assert.Equal("elements", t[0].Unit);
        }

        [Fact]
        public void ApplyCount_CounterOnly_NoBarFraction()
        {
            var t = Trail();
            ProgressReducer.ApplyCount(t, "t2", 17, -1, "elements", "");
            Assert.True(t[0].HasCount);
            Assert.False(t[0].HasTotal);
            Assert.Equal("17 elements", t[0].CountText);
            Assert.Equal(0, t[0].Fraction);
        }

        [Fact]
        public void Done_FreezesBarAtFull()
        {
            var t = Trail();
            ProgressReducer.Apply(t, "t2", "executing", "Scanning…", "", StepState.Running);
            ProgressReducer.ApplyCount(t, "t2", 36, 62, "elements", "");
            ProgressReducer.Apply(t, "t2", "executing", "", "", StepState.Done);
            Assert.Equal(62, t[0].Current);            // terminal frame is authoritative
            Assert.Equal(1.0, t[0].Fraction, 5);
        }

        [Fact]
        public void NoCountFrames_RowRendersLegacy()
        {
            var t = Trail();
            ProgressReducer.Apply(t, "t2", "executing", "Scanning…", "", StepState.Running);
            Assert.False(t[0].HasCount);
            Assert.Equal("", t[0].CountText);          // byte-identical legacy path
        }
    }
}
