using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class AiServiceStreamParseTests
    {
        [Fact]
        public void Tool_event_parses_new_fields()
        {
            var raw = "{\"tool\":\"create_wall\",\"step_id\":\"tc_1\",\"phase\":\"executing\"," +
                      "\"label\":\"Creating wall on Level 1\",\"detail\":\"Level 1\",\"state\":\"running\"}";
            var chunk = AIServiceStreamExtensions.ParseEvent("tool", raw);
            Assert.Equal(StreamChunkKind.Tool, chunk.Kind);
            Assert.Equal("tc_1", chunk.StepId);
            Assert.Equal("executing", chunk.Phase);
            Assert.Equal("Creating wall on Level 1", chunk.StatusLabel);
            Assert.Equal("Level 1", chunk.Detail);
            Assert.Equal("running", chunk.State);
        }

        [Fact]
        public void Tool_event_done_state_parses()
        {
            var raw = "{\"tool\":\"create_wall\",\"step_id\":\"tc_1\",\"state\":\"done\"}";
            var chunk = AIServiceStreamExtensions.ParseEvent("tool", raw);
            Assert.Equal("done", chunk.State);
            Assert.Equal("tc_1", chunk.StepId);
        }

        [Fact]
        public void Tool_event_without_state_defaults_to_running()
        {
            // Old backend: just a tool name, no step_id/state.
            var raw = "{\"tool\":\"open_view\"}";
            var chunk = AIServiceStreamExtensions.ParseEvent("tool", raw);
            Assert.Equal("running", chunk.State);              // compat default
            Assert.False(string.IsNullOrEmpty(chunk.StepId));  // synthesized id (tool name)
        }

        [Fact]
        public void Status_event_parses_phase_step_and_state()
        {
            var raw = "{\"step_id\":\"write\",\"phase\":\"writing\"," +
                      "\"label\":\"Writing code\",\"state\":\"done\"}";
            var chunk = AIServiceStreamExtensions.ParseEvent("status", raw);
            Assert.Equal(StreamChunkKind.Status, chunk.Kind);
            Assert.Equal("write", chunk.StepId);
            Assert.Equal("writing", chunk.Phase);
            Assert.Equal("Writing code", chunk.StatusLabel);
            Assert.Equal("done", chunk.State);
        }

        [Fact]
        public void Status_event_without_step_id_synthesizes_one()
        {
            // Old backend: bare { "label" }.
            var raw = "{\"label\":\"Generating code…\"}";
            var chunk = AIServiceStreamExtensions.ParseEvent("status", raw);
            Assert.Equal("Generating code…", chunk.StatusLabel);
            Assert.Equal("running", chunk.State);              // compat default
            Assert.False(string.IsNullOrEmpty(chunk.StepId));  // synthesized guid
        }
    }
}
