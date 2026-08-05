// CircuitBlockers — the two dead ends suggest_circuits can hit.
//
// The invariant every test here defends: a blocker is ok:TRUE. ok:false is the
// agent's self-heal-retry signal, and neither dead end is fixable by retrying.
// UAT 2026-08-04 watched the agent place and delete distribution boards in a
// loop because "every device is already circuited" came back ok:false.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CircuitBlockersTests
    {
        private static Dictionary<string, object?> Skip(long id, string reason) => new()
        {
            ["id"] = id, ["reason"] = reason,
        };

        private static CircuitedDevice OnCircuit(
            long deviceId, long circuitId, string number = "A1", long? panelId = 7) => new()
        {
            DeviceId = deviceId,
            CircuitId = circuitId,
            CircuitNumber = number,
            PanelId = panelId,
            PanelName = "DB-1",
        };

        private static PanelFacts Panel(long id, bool usable, string skipReason = "") => new()
        {
            Info = new PanelInfo { Id = id, Name = "DB-" + id },
            Usable = usable,
            SkipReason = skipReason,
        };

        private static Dictionary<string, object?> Blocker(Dictionary<string, object?> result) =>
            Assert.IsType<Dictionary<string, object?>>(result["blocker"]);

        // ─── the invariant ──────────────────────────────────────────────

        [Fact]
        public void NoPanel_is_ok_true_because_ok_false_would_trigger_a_retry_loop()
        {
            var r = CircuitBlockers.NoPanel(0, new List<object>());

            Assert.True((bool)r["ok"]!);
            Assert.Equal("no_panel", Blocker(r)["code"]);
        }

        [Theory]
        [InlineData("already_circuited")]
        [InlineData("level_mismatch:Level 2")]
        [InlineData("no_electrical_connector")]
        public void Every_NothingToGroup_branch_is_ok_true(string reason)
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { Skip(1, reason) },
                reason == "already_circuited"
                    ? new[] { OnCircuit(1, 900) }
                    : new CircuitedDevice[0]);

            Assert.True((bool)r["ok"]!);
            Assert.Equal(0, r["count"]);
            Assert.Empty((List<object>)r["circuits"]!);
        }

        // ─── no_panel ───────────────────────────────────────────────────

        [Fact]
        public void NoPanel_reports_how_many_panels_exist_and_why_each_was_skipped()
        {
            var skipped = CircuitBlockers.SkippedPanelRows(new[]
            {
                Panel(10, usable: false, skipReason: "no_distribution_system"),
                Panel(20, usable: true),
            });
            var r = CircuitBlockers.NoPanel(2, skipped);
            var b = Blocker(r);

            Assert.Equal(2, b["panels_found"]);
            var rows = Assert.IsType<List<object>>(b["skipped_panels"]);
            var row = Assert.IsType<Dictionary<string, object?>>(Assert.Single(rows));
            Assert.Equal(10L, row["id"]);
            Assert.Equal("no_distribution_system", row["reason"]);
        }

        [Fact]
        public void A_skipped_panel_row_names_the_setting_fix_not_a_replacement()
        {
            // The agent's instinct on "unusable panel" is to place another one.
            // This prose is the only thing that redirects it.
            var rows = CircuitBlockers.SkippedPanelRows(new[] { Panel(10, false, "no_distribution_system") });
            var fix = (string)((Dictionary<string, object?>)rows[0])["fix"]!;

            Assert.Contains("set_distribution_system", fix);
            Assert.Contains("re-placing or swapping", fix);
            Assert.Contains("set_connector_electrical_data", fix);
        }

        [Fact]
        public void NoPanel_tells_the_agent_not_to_loop_on_placing_panels()
        {
            var detail = (string)Blocker(CircuitBlockers.NoPanel(0, new List<object>()))["detail"]!;
            Assert.Contains("do not place, delete or swap panels", detail);
        }

        [Fact]
        public void Usable_panels_never_appear_in_the_skipped_rows()
        {
            Assert.Empty(CircuitBlockers.SkippedPanelRows(new[] { Panel(10, true), Panel(20, true) }));
        }

        // ─── all_devices_already_circuited ──────────────────────────────

        [Fact]
        public void All_already_circuited_fires_only_when_it_explains_every_skip()
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { Skip(1, "already_circuited"), Skip(2, "already_circuited") },
                new[] { OnCircuit(1, 900), OnCircuit(2, 900) });

            Assert.Equal("all_devices_already_circuited", Blocker(r)["code"]);
        }

        [Fact]
        public void One_device_skipped_for_another_reason_drops_it_to_the_generic_branch()
        {
            // Mixed reasons mean remove_from_circuit would not unblock the run,
            // so the specific advice would be wrong.
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { Skip(1, "already_circuited"), Skip(2, "no_electrical_connector") },
                new[] { OnCircuit(1, 900) });

            Assert.Equal("no_circuitable_devices", Blocker(r)["code"]);
        }

        [Fact]
        public void Existing_circuits_are_grouped_by_circuit_with_sorted_device_ids()
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { Skip(3, "already_circuited"), Skip(1, "already_circuited"),
                                   Skip(9, "already_circuited") },
                new[] { OnCircuit(3, 901, "A2"), OnCircuit(1, 900, "A1"), OnCircuit(9, 900, "A1") });

            var circuits = Assert.IsType<List<object>>(Blocker(r)["existing_circuits"]);
            Assert.Equal(2, circuits.Count);

            var first = (Dictionary<string, object?>)circuits[0];
            Assert.Equal(900L, first["circuit_id"]);
            Assert.Equal("A1", first["circuit_number"]);
            Assert.Equal(2, first["device_count"]);
            Assert.Equal(new object[] { 1L, 9L }, (List<object>)first["device_ids"]!);
        }

        [Fact]
        public void The_blocker_hands_over_the_ids_remove_from_circuit_needs()
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { Skip(2, "already_circuited"), Skip(1, "already_circuited") },
                new[] { OnCircuit(2, 900), OnCircuit(1, 900) });
            var b = Blocker(r);

            Assert.Equal("remove_from_circuit", b["next_tool"]);
            var hint = Assert.IsType<Dictionary<string, object?>>(b["next_args_hint"]);
            Assert.Equal(new object[] { 1L, 2L }, (List<object>)hint["device_ids"]!);
        }

        [Fact]
        public void Already_circuited_is_presented_as_a_possible_final_answer()
        {
            // The agent must be able to stop here. Without this the natural
            // reading of a blocker is "something went wrong, try again".
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { Skip(1, "already_circuited") }, new[] { OnCircuit(1, 900) });
            var detail = (string)Blocker(r)["detail"]!;

            Assert.Contains("OFTEN THE COMPLETE AND CORRECT ANSWER", detail);
            Assert.Contains("Do NOT retry this call unchanged", detail);
            Assert.Contains("do NOT place, delete or swap panels", detail);
        }

        // ─── level_filter_excluded_everything ───────────────────────────

        [Fact]
        public void A_level_filter_that_excluded_everything_names_the_levels_it_found()
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object>
                {
                    Skip(1, "level_mismatch:Level 1"),
                    Skip(2, "level_mismatch:Level 2"),
                    Skip(3, "level_mismatch:Level 1"),
                },
                new CircuitedDevice[0]);
            var b = Blocker(r);

            Assert.Equal("level_filter_excluded_everything", b["code"]);
            var levels = Assert.IsType<List<object>>(b["levels_found"]);
            Assert.Equal(new object[] { "Level 1", "Level 2" }, levels);
            Assert.Contains("Level 1, Level 2", (string)b["detail"]!);
        }

        [Fact]
        public void An_unknown_level_still_comes_through_as_a_named_level()
        {
            // Device collection emits "level_mismatch:unknown" when it cannot
            // resolve one; the prefix strip must not turn that into a blank.
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { Skip(1, "level_mismatch:unknown") }, new CircuitedDevice[0]);

            Assert.Equal(new object[] { "unknown" },
                         Assert.IsType<List<object>>(Blocker(r)["levels_found"]));
        }

        // ─── no_circuitable_devices ─────────────────────────────────────

        [Fact]
        public void The_generic_branch_censuses_the_reasons_and_names_their_fixes()
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object>
                {
                    Skip(1, "connector_voltage_unset"),
                    Skip(2, "connector_voltage_unset"),
                    Skip(3, "no_electrical_connector"),
                },
                new CircuitedDevice[0]);
            var b = Blocker(r);

            Assert.Equal("no_circuitable_devices", b["code"]);
            var detail = (string)b["detail"]!;
            Assert.Contains("connector_voltage_unset x2", detail);
            Assert.Contains("no_electrical_connector x1", detail);
            Assert.Contains("set_connector_electrical_data", detail);
        }

        [Fact]
        public void A_skip_with_no_reason_is_counted_as_unknown_rather_than_dropped()
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object> { new Dictionary<string, object?> { ["id"] = 1L } },
                new CircuitedDevice[0]);

            Assert.Contains("unknown x1", (string)Blocker(r)["detail"]!);
        }

        // ─── shared shape ───────────────────────────────────────────────

        [Fact]
        public void Every_branch_reports_the_skip_count_and_the_per_reason_census()
        {
            var r = CircuitBlockers.NothingToGroup(
                new List<object>
                {
                    Skip(1, "already_circuited"),
                    Skip(2, "no_electrical_connector"),
                    Skip(3, "no_electrical_connector"),
                },
                new[] { OnCircuit(1, 900) });

            Assert.Equal(3, Blocker(r)["skipped_count"]);
            var census = Assert.IsType<Dictionary<string, object?>>(r["skipped_by_reason"]);
            Assert.Equal(2, census["no_electrical_connector"]);
            Assert.Equal(1, census["already_circuited"]);
        }

        [Fact]
        public void The_skipped_devices_are_passed_through_untouched()
        {
            var skipped = new List<object> { Skip(1, "already_circuited") };
            var r = CircuitBlockers.NothingToGroup(skipped, new[] { OnCircuit(1, 900) });

            Assert.Same(skipped, r["skipped_devices"]);
        }
    }
}
