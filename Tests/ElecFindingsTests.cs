// ElecFindings — the pure rule evaluators behind the three electrical
// validators. Every threshold is caller-supplied; the tests pin the wire
// shape ({check, status, elements, reason, value, limit, unit}) and the
// skipped-is-not-silent contract.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ElecFindingsTests
    {
        [Fact]
        public void Load_within_ratio_passes_with_the_numbers_attached()
        {
            var f = ElecFindings.LoadVsBreaker(101, 2000, 230, 16, 0.8, threePhase: false);

            Assert.Equal("pass", f.Status);
            Assert.Equal(2000, f.Value);
            Assert.Equal(2944, f.Limit);      // 16 A x 230 V x 0.8
            Assert.Equal(new List<long> { 101 }, f.Elements);
        }

        [Fact]
        public void Load_over_ratio_fails_with_reason()
        {
            var f = ElecFindings.LoadVsBreaker(101, 3500, 230, 16, 0.8, threePhase: false);

            Assert.Equal("fail", f.Status);
            Assert.Contains("exceeds", f.Reason);
            Assert.Contains("16", f.Reason);
        }

        [Fact]
        public void Voltage_drop_fail_names_length_source()
        {
            // 29 mV/A/m x 10 A x 60 m = 17.4 V on 230 V = 7.57% > 3%
            var f = ElecFindings.VoltageDrop(101, 10, 29, 60000, 230, 3.0, false, "circuit_path");

            Assert.Equal("fail", f.Status);
            Assert.Equal("voltage_drop", f.Check);
            Assert.True(f.Value > 7.5 && f.Value < 7.6);
            Assert.Contains("circuit_path", f.Reason);
        }

        [Fact]
        public void Spacing_flags_the_largest_gap_including_run_ends()
        {
            // 10 m wall, sockets at 1 m and 3 m: the end gap (7 m) is the worst.
            var f = ElecFindings.ReceptacleSpacing("w:5", new[] { 1000.0, 3000.0 }, 10000, 3500);

            Assert.Equal("fail", f.Status);
            Assert.Equal(7000, f.Value);
        }

        [Fact]
        public void Spacing_passes_when_gaps_are_within_max()
        {
            var f = ElecFindings.ReceptacleSpacing("w:5", new[] { 2000.0, 5000.0, 8000.0 }, 10000, 3500);
            Assert.Equal("pass", f.Status);
        }

        [Fact]
        public void Bare_long_wall_with_no_receptacle_fails()
        {
            var f = ElecFindings.ReceptacleSpacing("w:9", new double[0], 8000, 3500);
            Assert.Equal("fail", f.Status);
            Assert.Contains("no receptacle", f.Reason);
        }

        [Fact]
        public void Short_bare_wall_passes()
        {
            var f = ElecFindings.ReceptacleSpacing("w:9", new double[0], 2000, 3500);
            Assert.Equal("pass", f.Status);
        }

        [Fact]
        public void Double_assigned_slots_grouped_per_panel()
        {
            var rows = new (long, long, string)[]
            {
                (1, 10, "1"), (2, 10, "1"),      // clash on panel 10 slot 1
                (3, 10, "2"),                     // fine
                (4, 20, "1"),                     // same label, other panel — fine
                (5, 30, ""),                      // unslotted — ignored here
            };
            var findings = ElecFindings.DoubleAssignedSlots(rows);

            var f = Assert.Single(findings);
            Assert.Equal("fail", f.Status);
            Assert.Equal(new List<long> { 1, 2 }, f.Elements);
            Assert.Contains("slot 1", f.Reason);
        }

        [Fact]
        public void Skipped_finding_carries_its_reason()
        {
            var f = Finding.Skipped("voltage_drop", "rule_arg_not_provided");
            var d = f.ToDict();

            Assert.Equal("skipped", d["status"]);
            Assert.Equal("rule_arg_not_provided", d["reason"]);
        }

        [Fact]
        public void Counts_roll_up_by_status()
        {
            var findings = new List<Finding>
            {
                new() { Status = "pass" }, new() { Status = "pass" },
                new() { Status = "fail" }, new() { Status = "skipped" },
            };
            var c = Finding.Counts(findings);

            Assert.Equal(2, c["pass"]);
            Assert.Equal(1, c["fail"]);
            Assert.Equal(0, c["warning"]);
            Assert.Equal(1, c["skipped"]);
        }

        [Fact]
        public void ToDict_has_the_full_wire_shape()
        {
            var keys = new Finding().ToDict().Keys.ToList();
            Assert.Equal(
                new[] { "check", "status", "elements", "reason", "value", "limit", "unit" },
                keys);
        }
    }
}
