using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The panel-refusal diagnosis. Every case here maps to a reason
    /// Revit reports as "the panel", which is what sends an agent off creating
    /// and deleting boards. A blocker that names the wrong cause is worse than
    /// no blocker, because it sends the fix in a confident wrong direction.</summary>
    public class PanelAssignmentDiagnosisTests
    {
        private static AssignmentFacts Healthy() => new AssignmentFacts
        {
            PanelExists = true,
            PanelIsElectricalEquipment = true,
            DistributionSystem = "240/415V 3-phase",
            CircuitVoltageV = 240,
            SystemLineToGroundV = 240,
            SystemLineToLineV = 415,
            CircuitPoles = 1,
            PanelPhaseCount = 3,
            TotalSlots = 42,
            UsedSlots = 6,
        };

        private static string[] Codes(AssignmentFacts f) =>
            PanelAssignmentDiagnosis.Hard(f).Select(b => b.Code).ToArray();

        [Fact]
        public void HealthyPairHasNoBlockers() => Assert.Empty(PanelAssignmentDiagnosis.Hard(Healthy()));

        [Fact]
        public void NullFactsDoNotThrow() => Assert.Empty(PanelAssignmentDiagnosis.Hard(null!));

        // ─── the causes, in the order they actually bite ────────────────

        [Fact]
        public void MissingDistributionSystemIsBlocked()
        {
            // The most common cause by far, and the one that reads most like a
            // panel mismatch.
            var f = Healthy();
            f.DistributionSystem = null;
            Assert.Contains("panel_no_distribution_system", Codes(f));
        }

        [Fact]
        public void BlankDistributionSystemCountsAsMissing()
        {
            var f = Healthy();
            f.DistributionSystem = "   ";
            Assert.Contains("panel_no_distribution_system", Codes(f));
        }

        [Fact]
        public void DistributionSystemBlockerNamesTheSettingsTools()
        {
            var f = Healthy();
            f.DistributionSystem = null;
            var b = PanelAssignmentDiagnosis.Hard(f).Single(x => x.Code == "panel_no_distribution_system");
            Assert.Contains("list_electrical_settings", b.Fix);
            Assert.Contains("set_distribution_system", b.Fix);
        }

        [Fact]
        public void UnreadableCircuitVoltageIsBlockedAndPointsAtTheConnectorTools()
        {
            var f = Healthy();
            f.CircuitVoltageV = null;
            var b = PanelAssignmentDiagnosis.Hard(f).Single(x => x.Code == "circuit_voltage_unreadable");
            Assert.Contains("set_connector_electrical_data", b.Fix);
        }

        [Fact]
        public void ZeroCircuitVoltageIsBlocked()
        {
            var f = Healthy();
            f.CircuitVoltageV = 0;
            Assert.Contains("circuit_zero_voltage", Codes(f));
        }

        [Fact]
        public void ZeroVoltageBlockerSaysEveryPanelWillSayTheSame()
        {
            // The sentence that has to defeat the "try another panel" reflex.
            var f = Healthy();
            f.CircuitVoltageV = 0;
            var b = PanelAssignmentDiagnosis.Hard(f).Single(x => x.Code == "circuit_zero_voltage");
            Assert.Contains("every panel", b.Detail);
        }

        [Fact]
        public void PanelNotFoundShortCircuitsEverythingElse()
        {
            // Reporting six blockers for one missing element is noise, and the
            // other five would be read off unreadable state anyway.
            var f = Healthy();
            f.PanelExists = false;
            Assert.Equal(new[] { "panel_not_found" }, Codes(f));
        }

        [Fact]
        public void NonEquipmentShortCircuitsEverythingElse()
        {
            var f = Healthy();
            f.PanelIsElectricalEquipment = false;
            Assert.Equal(new[] { "panel_not_equipment" }, Codes(f));
        }

        [Fact]
        public void ThreePoleOnSinglePhasePanelIsBlocked()
        {
            var f = Healthy();
            f.PanelPhaseCount = 1;
            f.CircuitPoles = 3;
            Assert.Contains("pole_mismatch", Codes(f));
        }

        [Fact]
        public void SinglePoleOnSinglePhasePanelIsFine()
        {
            var f = Healthy();
            f.PanelPhaseCount = 1;
            f.SystemLineToLineV = null;
            f.CircuitPoles = 1;
            Assert.Empty(PanelAssignmentDiagnosis.Hard(f));
        }

        [Fact]
        public void FullPanelIsBlocked()
        {
            var f = Healthy();
            f.UsedSlots = 42;
            Assert.Contains("panel_full", Codes(f));
        }

        [Fact]
        public void FullPanelIsTheOnlyBlockerWhereANewPanelIsAGenuineAnswer()
        {
            // Everything else has a real fix elsewhere. This one legitimately
            // may need another board — but the drafter decides, not the agent.
            var f = Healthy();
            f.UsedSlots = 42;
            var b = PanelAssignmentDiagnosis.Hard(f).Single(x => x.Code == "panel_full");
            Assert.Contains("drafter decide", b.Fix);
        }

        [Fact]
        public void UnknownSlotCountDoesNotReportAFullPanel()
        {
            // TotalSlots 0 means "could not read", not "no slots".
            var f = Healthy();
            f.TotalSlots = 0;
            f.UsedSlots = 0;
            Assert.DoesNotContain("panel_full", Codes(f));
        }

        // ─── voltage fit ────────────────────────────────────────────────

        [Fact]
        public void CircuitMatchingLineToLineIsAccepted()
        {
            var f = Healthy();
            f.CircuitVoltageV = 415;
            Assert.DoesNotContain("voltage_mismatch", Codes(f));
        }

        [Fact]
        public void CircuitMatchingNeitherVoltageIsBlocked()
        {
            var f = Healthy();
            f.CircuitVoltageV = 110;
            Assert.Contains("voltage_mismatch", Codes(f));
        }

        [Fact]
        public void VoltageMismatchQuotesBothNumbers()
        {
            // "does not match" without the numbers is unactionable.
            var f = Healthy();
            f.CircuitVoltageV = 110;
            var b = PanelAssignmentDiagnosis.Hard(f).Single(x => x.Code == "voltage_mismatch");
            Assert.Contains("110", b.Detail);
            Assert.Contains("240", b.Detail);
            Assert.Contains("415", b.Detail);
        }

        [Theory]
        [InlineData(230)]   // within tolerance of 240
        [InlineData(250)]
        [InlineData(400)]   // within tolerance of 415
        public void SmallVoltageDeviationsAreTolerated(double v)
        {
            // A false mismatch blocks a legitimate assignment, which is worse
            // than missing one — the tool attempts and reports either way.
            var f = Healthy();
            f.CircuitVoltageV = v;
            Assert.DoesNotContain("voltage_mismatch", Codes(f));
        }

        [Fact]
        public void NoSystemVoltageMeansNoMismatchClaim()
        {
            // Cannot compare against a value we could not read.
            var f = Healthy();
            f.SystemLineToGroundV = null;
            f.SystemLineToLineV = null;
            f.CircuitVoltageV = 110;
            Assert.DoesNotContain("voltage_mismatch", Codes(f));
        }

        [Fact]
        public void ZeroVoltageIsReportedAsZeroNotAsMismatch()
        {
            // Both are true; only one names the actual fix.
            var f = Healthy();
            f.CircuitVoltageV = 0;
            var codes = Codes(f);
            Assert.Contains("circuit_zero_voltage", codes);
            Assert.DoesNotContain("voltage_mismatch", codes);
        }

        // ─── ordering and the standing advice ───────────────────────────

        [Fact]
        public void CheapestMostCommonCauseIsReportedFirst()
        {
            // The caller surfaces blockers[0] as the headline, so ordering is
            // what the drafter actually reads.
            var f = Healthy();
            f.DistributionSystem = null;
            f.CircuitVoltageV = 0;
            f.UsedSlots = 42;
            Assert.Equal("panel_no_distribution_system", Codes(f).First());
        }

        [Fact]
        public void MultipleCausesAreAllReported()
        {
            var f = Healthy();
            f.DistributionSystem = null;
            f.CircuitVoltageV = 0;
            f.UsedSlots = 42;
            Assert.True(Codes(f).Length >= 3);
        }

        [Fact]
        public void StandingAdvicePointsAtAResolutionRatherThanForbidding()
        {
            // A prohibition with no alternative leaves the agent stuck, which
            // is how the first version of this still ended in a loop and then
            // in "do it by hand in Revit". The advice must name a next action.
            var advice = PanelAssignmentDiagnosis.PanelNamedRegardlessAdvice;
            Assert.Contains("resolution", advice);
            Assert.DoesNotContain("Do NOT", advice);
        }

        [Fact]
        public void VoltageMismatchExplainsThePoleRule()
        {
            // The whole mismatch reduces to this one fact, so the blocker has
            // to carry it — 240 V 1-pole on a 120/240 board is not a broken
            // panel, it is a breaker sitting line-to-ground.
            var f = Healthy();
            f.CircuitVoltageV = 110;
            var b = PanelAssignmentDiagnosis.Hard(f).Single(x => x.Code == "voltage_mismatch");
            Assert.Contains("line-to-ground", b.Detail);
            Assert.Contains("line-to-line", b.Detail);
        }

        [Fact]
        public void NoBlockerEverSuggestsTryingAnotherNewPanel()
        {
            // Sweep every blocker this can emit: none may recommend creating a
            // panel except panel_full, which qualifies it.
            var cases = new[]
            {
                Mutate(f => f.DistributionSystem = null),
                Mutate(f => f.CircuitVoltageV = null),
                Mutate(f => f.CircuitVoltageV = 0),
                Mutate(f => f.CircuitVoltageV = 110),
                Mutate(f => { f.PanelPhaseCount = 1; f.CircuitPoles = 3; }),
                Mutate(f => f.PanelExists = false),
                Mutate(f => f.PanelIsElectricalEquipment = false),
            };
            foreach (var f in cases)
                foreach (var b in PanelAssignmentDiagnosis.Hard(f))
                    Assert.DoesNotContain("create_panel", b.Fix);
        }

        private static AssignmentFacts Mutate(System.Action<AssignmentFacts> change)
        {
            var f = Healthy();
            change(f);
            return f;
        }
    }
}
