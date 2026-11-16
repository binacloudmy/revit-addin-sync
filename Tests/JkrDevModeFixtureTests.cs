using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// TEST-ONLY JKR mode (BINA_DEV_UNLOCK / BINA_JKR_FIXTURE): off unless set
    /// exactly, and the fixture rule "an issue exists while the live value differs
    /// from the expected one". The Revit reading and generating (JkrFixtureScanner)
    /// can only be checked in Revit.
    /// </summary>
    public class JkrDevModeFixtureTests
    {
        private static JkrDevMode Mode(string unlock, string fixture) =>
            JkrDevMode.From(name => name == JkrDevMode.UnlockVar ? unlock : name == JkrDevMode.FixtureVar ? fixture : null);

        // ── Gating ──

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("true")]
        [InlineData("yes")]
        [InlineData("11")]
        public void Anything_but_exactly_1_is_off(string value)
        {
            var m = Mode(value, @"C:\x\fixture.json");
            Assert.False(m.Unlocked);
            Assert.False(m.UsesFixture);
            Assert.Null(m.FixturePath);
        }

        [Fact]
        public void Absent_variables_change_nothing()
        {
            var m = JkrDevMode.From(_ => null);
            Assert.False(m.Unlocked);
            Assert.False(m.UsesFixture);
        }

        [Fact]
        public void One_unlocks_without_a_fixture()
        {
            var m = Mode("1", null);
            Assert.True(m.Unlocked);
            Assert.False(m.UsesFixture);
        }

        [Fact]
        public void One_with_a_path_uses_the_fixture()
        {
            var m = Mode(" 1 ", "\"C:\\Bina\\revit-test\\fixture.json\"");
            Assert.True(m.Unlocked);
            Assert.True(m.UsesFixture);
            Assert.Equal(@"C:\Bina\revit-test\fixture.json", m.FixturePath);
        }

        [Fact]
        public void A_blank_fixture_path_is_ignored()
        {
            Assert.False(Mode("1", "   ").UsesFixture);
        }

        // ── Evaluate ──

        private static JkrFixtureIssue Rename(string id, long el, string expected, bool enabled = true) =>
            new JkrFixtureIssue { Id = id, ElementId = el, FixAction = "rename_type", Expected = expected, Enabled = enabled };

        private static JkrFixtureIssue Param(string id, long el, string param, string expected) =>
            new JkrFixtureIssue { Id = id, ElementId = el, FixAction = "set_parameter", FixParameter = param, Expected = expected };

        [Fact]
        public void An_entry_is_an_issue_only_while_the_value_differs()
        {
            var fx = new JkrFixture { Issues = { Rename("A", 1, "jkrAR_x"), Param("B", 2, "Comments", "JKR-TEST-2") } };
            var model = new Dictionary<long, string> { [1] = "Generic 200", [2] = "JKR-TEST-2" };

            var found = JkrFixtureFile.Evaluate(fx, i => new JkrFixtureObservation(true, model[i.ElementId]));
            Assert.Equal(new[] { "A" }, found.Select(f => f.Issue.Id));
            Assert.Equal("Generic 200", found[0].Actual);
        }

        [Fact]
        public void After_a_fix_the_entry_passes_and_after_undo_it_fails_again()
        {
            var fx = new JkrFixture { Issues = { Rename("A", 1, "jkrAR_x") } };
            var model = new Dictionary<long, string> { [1] = "Generic 200" };
            JkrFixtureObservation Read(JkrFixtureIssue i) => new JkrFixtureObservation(true, model[i.ElementId]);

            Assert.Single(JkrFixtureFile.Evaluate(fx, Read));
            model[1] = "jkrAR_x";          // Fix all
            Assert.Empty(JkrFixtureFile.Evaluate(fx, Read));
            model[1] = "Generic 200";      // Ctrl+Z / Reset
            Assert.Single(JkrFixtureFile.Evaluate(fx, Read));
        }

        [Fact]
        public void A_missing_element_stays_an_issue()
        {
            var fx = new JkrFixture { Issues = { Rename("M", 999999999, "jkrAR_missing") } };
            var f = Assert.Single(JkrFixtureFile.Evaluate(fx, _ => JkrFixtureObservation.Missing));
            Assert.Equal(JkrFixtureFile.NotFound, f.Actual);
        }

        [Fact]
        public void Disabled_entries_are_skipped_and_not_read()
        {
            var reads = 0;
            var fx = new JkrFixture { Issues = { Rename("A", 1, "x", enabled: false) } };
            Assert.Empty(JkrFixtureFile.Evaluate(fx, _ => { reads++; return new JkrFixtureObservation(true, "y"); }));
            Assert.Equal(0, reads);
        }

        [Fact]
        public void Comparison_is_exact()
        {
            var fx = new JkrFixture { Issues = { Param("A", 1, "Comments", "JKR-TEST") } };
            Assert.Single(JkrFixtureFile.Evaluate(fx, _ => new JkrFixtureObservation(true, "jkr-test")));
            Assert.Single(JkrFixtureFile.Evaluate(fx, _ => new JkrFixtureObservation(true, "JKR-TEST ")));
        }

        [Fact]
        public void Findings_keep_fixture_order()
        {
            var fx = new JkrFixture { Issues = { Rename("C", 3, "x"), Rename("A", 1, "x"), Rename("B", 2, "x") } };
            var ids = JkrFixtureFile.Evaluate(fx, _ => new JkrFixtureObservation(true, "y")).Select(f => f.Issue.Id);
            Assert.Equal(new[] { "C", "A", "B" }, ids);
        }

        // ── Validate ──

        [Fact]
        public void A_well_formed_fixture_has_no_errors()
        {
            var fx = new JkrFixture { Issues = { Rename("A", 1, "x"), Param("B", 2, "Comments", "y") } };
            Assert.Empty(JkrFixtureFile.Validate(fx));
        }

        [Fact]
        public void Broken_entries_are_reported()
        {
            var fx = new JkrFixture
            {
                Issues =
                {
                    Rename("A", 0, "x"),
                    Rename("A", 2, "x"),
                    new JkrFixtureIssue { Id = "C", ElementId = 3, FixAction = "set_parameter", Expected = "v" },
                    new JkrFixtureIssue { Id = "D", ElementId = 4, FixAction = "delete", Expected = "v" },
                    Rename("E", 5, ""),
                }
            };
            var errors = JkrFixtureFile.Validate(fx);
            Assert.Contains(errors, e => e.StartsWith("A: element_id"));
            Assert.Contains(errors, e => e == "A: duplicate id.");
            Assert.Contains(errors, e => e.StartsWith("C: set_parameter needs fix_parameter"));
            Assert.Contains(errors, e => e.StartsWith("D: fix_action"));
            Assert.Contains(errors, e => e.StartsWith("E: expected is empty"));
        }

        // ── File ──

        [Fact]
        public void Save_and_load_round_trip()
        {
            var dir = Path.Combine(Path.GetTempPath(), "jkr-fixture-test-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "sub", "fixture.json");
            try
            {
                var fx = new JkrFixture { Model = "m", Issues = { Param("B", 2, "Type Comments", "y") } };
                fx.Issues[0].FixTarget = "type";
                fx.Issues[0].ExpectFailure = "read-only";
                JkrFixtureFile.Save(path, fx);

                var back = JkrFixtureFile.Load(path);
                var i = Assert.Single(back.Issues);
                Assert.Equal("m", back.Model);
                Assert.Equal("Type Comments", i.FixParameter);
                Assert.Equal("type", i.FixTarget);
                Assert.Equal("read-only", i.ExpectFailure);
                Assert.True(i.Enabled);
                Assert.Contains("\"fix_parameter\"", File.ReadAllText(path));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void An_entry_without_enabled_is_enabled()
        {
            var dir = Path.Combine(Path.GetTempPath(), "jkr-fixture-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "f.json");
            try
            {
                File.WriteAllText(path, "{\"issues\":[{\"id\":\"A\",\"element_id\":5,\"fix_action\":\"rename_type\",\"expected\":\"x\"}]}");
                Assert.True(Assert.Single(JkrFixtureFile.Load(path).Issues).Enabled);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
