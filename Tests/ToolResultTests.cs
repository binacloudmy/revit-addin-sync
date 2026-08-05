// ToolResult — the failure envelope every tool returns.
//
// The load-bearing property is that "ok" is a real bool: BatchExecutor tests
// `ok is bool b && !b` to decide whether to roll a batch group back, so an
// "ok" that is a string or an int silently disarms that check.

using System.Collections.Generic;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ToolResultTests
    {
        [Fact]
        public void Fail_reports_ok_as_a_real_bool_not_a_string()
        {
            var r = ToolResult.Fail("boom");

            Assert.True(r.TryGetValue("ok", out var ok));
            Assert.IsType<bool>(ok);
            Assert.False((bool)ok!);
            Assert.Equal("boom", r["error"]);
        }

        [Fact]
        public void Fail_without_extras_carries_exactly_ok_and_error()
        {
            Assert.Equal(new[] { "ok", "error" }, ToolResult.Fail("boom").Keys);
        }

        [Fact]
        public void Extras_are_merged_after_the_envelope()
        {
            var r = ToolResult.Fail("boom", new Dictionary<string, object?>
            {
                ["candidates"] = new List<object> { "a", "b" },
                ["panel_id"] = 42L,
            });

            Assert.False((bool)r["ok"]!);
            Assert.Equal("boom", r["error"]);
            Assert.Equal(42L, r["panel_id"]);
            Assert.Equal(new[] { "ok", "error", "candidates", "panel_id" }, r.Keys);
        }

        [Fact]
        public void An_extra_may_deliberately_override_the_error()
        {
            // Merged last wins. A caller that wants Revit's own refusal text in
            // place of its own summary relies on this.
            var r = ToolResult.Fail("summary", new Dictionary<string, object?>
            {
                ["error"] = "Revit says no",
            });

            Assert.Equal("Revit says no", r["error"]);
            Assert.False((bool)r["ok"]!);
        }

        [Fact]
        public void An_extra_cannot_turn_a_failure_into_a_success_by_accident()
        {
            // Not a guard — a documented consequence of merge-last-wins. Pinned
            // so that changing the merge order is a deliberate decision.
            var r = ToolResult.Fail("boom", new Dictionary<string, object?> { ["ok"] = true });
            Assert.True((bool)r["ok"]!);
        }

        [Fact]
        public void A_null_extra_dict_is_the_same_as_none()
        {
            Assert.Equal(ToolResult.Fail("boom").Keys, ToolResult.Fail("boom", null).Keys);
        }

        [Fact]
        public void FailMissingArgs_renders_the_standards_refusal_verbatim()
        {
            // suggest_circuits / suggest_circuit_routes. This text reaches the
            // agent, which is why the wording is pinned rather than paraphrased.
            var r = ToolResult.FailMissingArgs(
                new[] { "voltage_v", "va_per_socket" }, "standards args",
                "electrical design standards", "electrical_circuiting");

            Assert.False((bool)r["ok"]!);
            Assert.Equal(
                "missing required standards args: voltage_v, va_per_socket. " +
                "These are electrical design standards, not defaults the addin may assume — " +
                "take the values from the electrical_circuiting recipe and pass them explicitly.",
                r["error"]);
        }

        [Fact]
        public void FailMissingArgs_renders_the_validator_refusal_verbatim()
        {
            // The three validators. Deliberately different wording: thresholds
            // are jurisdiction-dependent, not design standards.
            var r = ToolResult.FailMissingArgs(
                new[] { "max_panel_utilization_pct" }, "rule args",
                "jurisdiction-dependent thresholds", "electrical_validation");

            Assert.Equal(
                "missing required rule args: max_panel_utilization_pct. " +
                "These are jurisdiction-dependent thresholds, not defaults the addin may assume — " +
                "take the values from the electrical_validation recipe and pass them explicitly.",
                r["error"]);
        }

        [Fact]
        public void FailMissingArgs_names_every_missing_arg_comma_separated()
        {
            var r = ToolResult.FailMissingArgs(
                new[] { "a", "b", "c" }, "rule args", "thresholds", "some_recipe");

            Assert.Contains("missing required rule args: a, b, c.", (string)r["error"]!);
        }
    }
}
