using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The distribution-system matcher. The reference case throughout
    /// is the one that actually failed in UAT on 2026-08-07: three sockets set
    /// to 240 V 1-pole, a DB carrying a US-style "120/240 Single" system, and
    /// Revit answering "panel and circuit do not match". The copilot read that
    /// as a broken panel, made and deleted boards, and finally told the drafter
    /// to do it by hand. The arithmetic below is what it needed instead.</summary>
    public class DistributionSystemMatchTests
    {
        private static DistSysOption Us120_240(bool accepts = true) => new DistSysOption
        {
            Id = 1, Name = "120/240 Single", ThreePhase = false,
            LineToGroundV = 120, LineToGroundMinV = 110, LineToGroundMaxV = 130,
            LineToLineV = 240, LineToLineMinV = 220, LineToLineMaxV = 260,
            PanelAccepts = accepts,
        };

        private static DistSysOption My240_415(bool accepts = true) => new DistSysOption
        {
            Id = 2, Name = "240/415V Three Phase", ThreePhase = true,
            LineToGroundV = 240, LineToGroundMinV = 220, LineToGroundMaxV = 260,
            LineToLineV = 415, LineToLineMinV = 395, LineToLineMaxV = 435,
            PanelAccepts = accepts,
        };

        private static CircuitDemand Demand(double v, int poles) =>
            new CircuitDemand { VoltageV = v, Poles = poles };

        // ─── the pole rule, which is the whole thing ────────────────────

        [Fact]
        public void OnePoleSitsLineToGround() => Assert.False(DistributionSystemMatch.IsLineToLine(1));

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void TwoOrMorePolesSitLineToLine(int p) =>
            Assert.True(DistributionSystemMatch.IsLineToLine(p));

        [Fact]
        public void TwoFortyOnAUsBoardNeedsTwoPoles()
        {
            // The exact UAT case: 240 V is line-to-LINE on 120/240, so it
            // needs 2 poles. At 1 pole it would be sitting on a 120 V leg.
            Assert.Equal(2, DistributionSystemMatch.PolesFor(Us120_240(), 240));
        }

        [Fact]
        public void OneTwentyOnAUsBoardIsOnePole() =>
            Assert.Equal(1, DistributionSystemMatch.PolesFor(Us120_240(), 120));

        [Fact]
        public void TwoFortyOnAMalaysianBoardIsOnePole()
        {
            // Same 240 V circuit, no change to the family, seats at 1 pole
            // because line-to-ground IS 240 here. This is why the answer is a
            // distribution system, not a different panel.
            Assert.Equal(1, DistributionSystemMatch.PolesFor(My240_415(), 240));
        }

        [Fact]
        public void FourFifteenOnAMalaysianBoardIsThreePole() =>
            Assert.Equal(3, DistributionSystemMatch.PolesFor(My240_415(), 415));

        [Fact]
        public void AVoltageMatchingNeitherLegSeatsNowhere() =>
            Assert.Null(DistributionSystemMatch.PolesFor(Us120_240(), 400));

        [Fact]
        public void RevitsOwnMinMaxWindowIsTheTolerance()
        {
            // 125 V is inside the 110-130 band Revit itself publishes, so it
            // seats. Nothing here invents a percentage when a band exists.
            Assert.Equal(1, DistributionSystemMatch.PolesFor(Us120_240(), 125));
            Assert.Null(DistributionSystemMatch.PolesFor(Us120_240(), 135));
        }

        [Fact]
        public void MissingRangeFallsBackToATolerance()
        {
            var o = new DistSysOption { Name = "x", LineToGroundV = 240, PanelAccepts = true };
            Assert.Equal(1, DistributionSystemMatch.PolesFor(o, 250));
            Assert.Null(DistributionSystemMatch.PolesFor(o, 300));
        }

        // ─── the UAT scenario end to end ────────────────────────────────

        [Fact]
        public void UatCase_240VoltOnePoleOnUsBoard_ReportsThePoleChangeNotAPanelProblem()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { Us120_240() });
            Assert.Empty(r.AssignableNow);
            var fit = Assert.Single(r.FitsWithPoleChange);
            Assert.Equal("120/240 Single", fit.Name);
            Assert.Equal(2, fit.PolesRequired);
            Assert.False(fit.FitsAsIs);
        }

        [Fact]
        public void UatCase_SummaryNamesBothWaysOut()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { Us120_240() });
            Assert.Contains("2 poles", r.Summary);
            Assert.Contains("set_connector_electrical_data", r.Summary);
            Assert.Contains("create_distribution_system", r.Summary);
        }

        [Fact]
        public void UatCase_SuggestsAMalaysianSystemThatNeedsNoFamilyChange()
        {
            // The drafter has just fixed these families. Offering a system that
            // seats the circuit AS IT IS beats telling them to redo the work.
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { Us120_240() });
            Assert.NotNull(r.Create);
            Assert.Equal(240, r.Create!.LineToGroundV);
            Assert.Equal(415, r.Create.LineToLineV);
            Assert.Equal("three", r.Create.Phase);
        }

        [Fact]
        public void UatCase_WithTheMalaysianSystemPresentItIsAssignableAsIs()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { Us120_240(), My240_415() });
            var fit = Assert.Single(r.AssignableNow);
            Assert.Equal("240/415V Three Phase", fit.Name);
            Assert.True(fit.FitsAsIs);
            Assert.Contains("set_distribution_system", r.Summary);
        }

        // ─── panel acceptance is Revit's verdict, not ours ──────────────

        [Fact]
        public void SystemsThePanelRejectsAreNeverOffered()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { My240_415(accepts: false) });
            Assert.Empty(r.AssignableNow);
            Assert.Empty(r.FitsWithPoleChange);
            Assert.Contains("240/415V Three Phase", r.PanelRejects);
        }

        [Fact]
        public void RejectedSystemsStillProduceACreateSuggestion()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { My240_415(accepts: false) });
            Assert.NotNull(r.Create);
        }

        // ─── degenerate inputs ──────────────────────────────────────────

        [Fact]
        public void NoVoltageBlamesTheFamilyNotThePanel()
        {
            var r = DistributionSystemMatch.Solve(Demand(0, 1), new[] { Us120_240() });
            Assert.Contains("set_connector_electrical_data", r.Summary);
            Assert.Contains("panel is not the problem", r.Summary);
        }

        // ─── the 0 V computed route (2026-08: prose alone here was the loop
        //     trigger — the agent got a refusal and no next action) ────────

        [Fact]
        public void NoVoltageReturnsTheConnectorFixNotJustProse()
        {
            var d = Demand(0, 1);
            d.DefaultVoltageV = 240;
            d.DefaultPoles = 1;
            var r = DistributionSystemMatch.Solve(d, new[] { Us120_240() });

            Assert.NotNull(r.FixConnector);
            Assert.Equal(240, r.FixConnector!.VoltageV);
            Assert.Equal(1, r.FixConnector.Poles);
            Assert.Equal("power_unbalanced", r.FixConnector.SystemType);
            Assert.True(r.FixConnector.IsProposal);
        }

        [Fact]
        public void NoVoltageAlsoComputesTheSystemToCreate()
        {
            // Both halves of the route in ONE result: fix the connector, then
            // this system seats it. Previously the distribution-system half
            // was invisible until the connector half was fixed.
            var d = Demand(0, 1);
            d.DefaultVoltageV = 240;
            var r = DistributionSystemMatch.Solve(d, new DistSysOption[0]);

            Assert.NotNull(r.Create);
            Assert.Equal(240, r.Create!.LineToGroundV);
            Assert.Contains("fix_connector", r.Summary);
            Assert.Contains("CONVENTION", r.Summary);
        }

        [Fact]
        public void NoVoltageWithoutAConventionFallsBackTo240SinglePhase()
        {
            var r = DistributionSystemMatch.Solve(Demand(0, 1), new[] { Us120_240() });
            Assert.NotNull(r.FixConnector);
            Assert.Equal(240, r.FixConnector!.VoltageV);
            Assert.Equal(1, r.FixConnector.Poles);
        }

        [Fact]
        public void NoVoltageThreePoleConventionIsBalancedPower()
        {
            var d = Demand(0, 3);
            d.DefaultVoltageV = 415;
            d.DefaultPoles = 3;
            var r = DistributionSystemMatch.Solve(d, new DistSysOption[0]);
            Assert.Equal("power_balanced", r.FixConnector!.SystemType);
        }

        [Fact]
        public void HealthyVoltageNeverCarriesAConnectorFix()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { My240_415() });
            Assert.Null(r.FixConnector);
        }

        [Fact]
        public void NoVoltageOnAOnePolePanelSuggestsSinglePhaseOnly()
        {
            // The 2026-08-07 regression guard, now on the 0 V path too: a
            // 1-pole board must never be offered a three-phase system.
            var d = Demand(0, 1);
            d.DefaultVoltageV = 240;
            d.PanelConnectorPoles = 1;
            var r = DistributionSystemMatch.Solve(d, new DistSysOption[0]);

            Assert.Equal("single", r.Create!.Phase);
            Assert.Null(r.Create.LineToLineV);
            Assert.Contains("cannot take a", r.Summary);
        }

        [Fact]
        public void NoVoltageInvalidConventionPolesFallBackToOne()
        {
            var d = Demand(0, 1);
            d.DefaultVoltageV = 240;
            d.DefaultPoles = 7;
            var r = DistributionSystemMatch.Solve(d, new DistSysOption[0]);
            Assert.Equal(1, r.FixConnector!.Poles);
        }

        [Fact]
        public void NoSystemsAtAllSaysSoPlainly()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new DistSysOption[0]);
            Assert.Contains("no distribution systems at all", r.Summary);
            Assert.NotNull(r.Create);
        }

        [Fact]
        public void NullInputsDoNotThrow()
        {
            var r = DistributionSystemMatch.Solve(null!, null!);
            Assert.NotNull(r);
            Assert.Empty(r.AssignableNow);
        }

        [Fact]
        public void NullEntriesInTheListAreSkipped()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { null!, My240_415() });
            Assert.Single(r.AssignableNow);
        }

        // ─── the create suggestion ──────────────────────────────────────

        [Fact]
        public void TwoPoleDemandSuggestsASinglePhaseSystemAtThatLineToLine()
        {
            var s = DistributionSystemMatch.Suggest(240, 2);
            Assert.Equal("single", s.Phase);
            Assert.Equal(240, s.LineToLineV);
            Assert.Equal(120, s.LineToGroundV);
        }

        [Fact]
        public void ThreePoleDemandSuggestsThreePhaseWithTheStandardPartner()
        {
            var s = DistributionSystemMatch.Suggest(415, 3);
            Assert.Equal("three", s.Phase);
            Assert.Equal(415, s.LineToLineV);
            Assert.Equal(240, s.LineToGroundV);
        }

        [Theory]
        [InlineData(240, 415)]   // Malaysia / IEC — NOT 240*sqrt(3) = 415.69
        [InlineData(230, 400)]
        [InlineData(120, 208)]
        [InlineData(277, 480)]
        public void StandardNominalPairsAreUsedNotRootThree(double ltg, double ltl)
        {
            // Naming a system "240/416V" would be wrong on every drawing and
            // schedule that quotes it. The pairs are conventions.
            Assert.Equal(ltl, DistributionSystemMatch.LineToLineFor(ltg));
            Assert.Equal(ltg, DistributionSystemMatch.LineToGroundFor(ltl));
        }

        [Fact]
        public void NonStandardVoltageFallsBackToRootThree()
        {
            // 500 is on no standard list, so arithmetic is the honest answer.
            Assert.Equal(866, DistributionSystemMatch.LineToLineFor(500));
        }

        // ─── the panel's own connector limits what can be suggested ─────

        [Fact]
        public void OnePolePanelNeverGetsAThreePhaseSuggestion()
        {
            // UAT 2026-08-07: the DB family is authored with a 1-pole
            // connector. Suggesting 240/415 three-phase sent the drafter to
            // the Family Editor to re-author a panel that was fine.
            var s = DistributionSystemMatch.Suggest(240, 1, panelCanTakeThreePhase: false);
            Assert.Equal("single", s.Phase);
            Assert.Equal(240, s.LineToGroundV);
            Assert.Null(s.LineToLineV);
        }

        [Fact]
        public void ThreePhaseCapablePanelStillGetsTheThreePhaseSuggestion()
        {
            var s = DistributionSystemMatch.Suggest(240, 1, panelCanTakeThreePhase: true);
            Assert.Equal("three", s.Phase);
            Assert.Equal(415, s.LineToLineV);
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(3, true)]
        [InlineData(null, true)]   // unknown makes no assumption
        public void PanelThreePhaseCapabilityIsReadFromItsConnectorPoles(int? poles, bool expected)
        {
            var d = new CircuitDemand { VoltageV = 240, Poles = 1, PanelConnectorPoles = poles };
            Assert.Equal(expected, d.PanelCanTakeThreePhase);
        }

        [Fact]
        public void SolveOnAOnePolePanelSuggestsSinglePhaseAndSaysWhy()
        {
            var d = new CircuitDemand { VoltageV = 240, Poles = 1, PanelConnectorPoles = 1 };
            var r = DistributionSystemMatch.Solve(d, new[] { Us120_240() });
            Assert.Equal("single", r.Create!.Phase);
            Assert.Contains("1-pole", r.Summary);
            Assert.Contains("cannot take a THREE-PHASE", r.Summary);
        }

        [Fact]
        public void SolveOnAnUnknownPanelKeepsTheThreePhaseOffer()
        {
            var d = new CircuitDemand { VoltageV = 240, Poles = 1 };
            var r = DistributionSystemMatch.Solve(d, new[] { Us120_240() });
            Assert.Equal("three", r.Create!.Phase);
            Assert.DoesNotContain("cannot take a THREE-PHASE", r.Summary);
        }

        [Fact]
        public void SuggestionNameCarriesTheVoltages()
        {
            Assert.Contains("240", DistributionSystemMatch.Suggest(240, 1).Name);
            Assert.Contains("415", DistributionSystemMatch.Suggest(240, 1).Name);
        }

        // ─── ordinary success ───────────────────────────────────────────

        [Fact]
        public void AMatchingBoardIsReportedAssignableWithNoCreateSuggestion()
        {
            var r = DistributionSystemMatch.Solve(Demand(120, 1), new[] { Us120_240() });
            Assert.Single(r.AssignableNow);
            Assert.Null(r.Create);
            Assert.Contains("seats on it as-is", r.Summary);
        }

        [Fact]
        public void TwoPoleTwoFortyOnTheUsBoardIsAssignableAsIs()
        {
            // Same board, same 240 V — correct pole count, no problem at all.
            var r = DistributionSystemMatch.Solve(Demand(240, 2), new[] { Us120_240() });
            Assert.Single(r.AssignableNow);
            Assert.Empty(r.FitsWithPoleChange);
        }

        [Fact]
        public void AssignableWinsOverPoleChangeWhenBothExist()
        {
            var r = DistributionSystemMatch.Solve(Demand(240, 1), new[] { Us120_240(), My240_415() });
            Assert.Single(r.AssignableNow);
            Assert.Single(r.FitsWithPoleChange);
            Assert.Contains("240/415V Three Phase", r.Summary);
            Assert.Null(r.Create);   // nothing to create, one already works
        }
    }
}
