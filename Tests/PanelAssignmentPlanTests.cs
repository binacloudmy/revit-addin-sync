using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The plan engine behind plan_panel_assignment. Reference
    /// scenario throughout: a room of JKR 13A sockets (0 V connectors — the
    /// library ships every electrical family that way), a DB with no
    /// distribution system, and the drafter asking "assign a suitable panel to
    /// the sockets in this room". The engine must answer read-only with ranked
    /// verdicts and complete steps — the trial-mutation loop this replaces
    /// started exactly where any of these assertions fail.</summary>
    public class PanelAssignmentPlanTests
    {
        private static SupplyConvention My => new SupplyConvention { VoltageV = 240, Poles = 1 };

        private static DevicePlanFacts Socket(long id, double? v = 240, int? poles = 1,
                                              string family = "JKR_Socket_13A", bool conn = true) =>
            new DevicePlanFacts
            {
                ElementId = id, FamilyName = family, VoltageV = v,
                VoltageSource = v is > 0 ? "instance" : "absent",
                Poles = poles, HasElectricalConnector = conn,
            };

        private static DistSysOption My240_415(bool accepts = true) => new DistSysOption
        {
            Id = 2, Name = "240/415V Three Phase", ThreePhase = true,
            LineToGroundV = 240, LineToGroundMinV = 220, LineToGroundMaxV = 260,
            LineToLineV = 415, LineToLineMinV = 395, LineToLineMaxV = 435,
            PanelAccepts = accepts,
        };

        private static DistSysOption Us120_240(bool accepts = true) => new DistSysOption
        {
            Id = 1, Name = "120/240 Single", ThreePhase = false,
            LineToGroundV = 120, LineToGroundMinV = 110, LineToGroundMaxV = 130,
            LineToLineV = 240, LineToLineMinV = 220, LineToLineMaxV = 260,
            PanelAccepts = accepts,
        };

        private static PanelPlanFacts Db(long id, string? system, int total = 12, int used = 3,
                                         string name = "DB-1", params DistSysOption[] options) =>
            new PanelPlanFacts
            {
                PanelId = id, Name = name, DistributionSystem = system,
                TotalSlots = total, UsedSlots = used,
                Options = options.ToList(),
            };

        // ─── grouping ───────────────────────────────────────────────────

        [Fact]
        public void OneFamilyOneVoltageIsOneGroup()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1), Socket(2), Socket(3) },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            var g = Assert.Single(r.Groups);
            Assert.True(g.Ready);
            Assert.Equal(new long[] { 1, 2, 3 }, g.ElementIds);
            Assert.Equal(240, g.VoltageV);
            Assert.Equal("measured", g.DemandSource);
            Assert.Null(g.ConnectorFix);
        }

        [Fact]
        public void MixedVoltagesSplitIntoTwoCircuitGroups()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1), Socket(2), Socket(5, v: 415, poles: 3, family: "JKR_Motor") },
                new[] { Db(10, null, options: My240_415()) }, My);

            Assert.Equal(2, r.Groups.Count);
            Assert.All(r.Groups, g => Assert.True(g.Ready));
        }

        [Fact]
        public void ZeroVoltFamilyBecomesAFixGroupWithProposedArgs()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null), Socket(2, v: 0) },
                new[] { Db(10, null, options: My240_415()) }, My);

            var g = Assert.Single(r.Groups);
            Assert.False(g.Ready);
            Assert.Equal("convention_proposal", g.DemandSource);
            var fix = g.ConnectorFix!;
            Assert.Equal("set_connector_electrical_data", fix.Tool);
            Assert.Equal("JKR_Socket_13A", fix.Args["family_name"]);
            Assert.Equal(240.0, fix.Args["voltage_v"]);
            Assert.Equal(1, fix.Args["poles"]);
            Assert.Equal("power_unbalanced", fix.Args["system_type"]);
            Assert.True(fix.IsProposal);
            Assert.True(fix.NeedsUserConfirm);
            Assert.Contains("dry_run", fix.Reason);
        }

        [Fact]
        public void ReadyAndFixFamiliesCoexistAsSeparateGroups()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1), Socket(2, v: null, family: "JKR_Socket_Old") },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            Assert.Equal(2, r.Groups.Count);
            Assert.Single(r.Groups, g => g.Ready);
            Assert.Single(r.Groups, g => g.ConnectorFix != null);
        }

        [Fact]
        public void ConventionOverrideIsRespected()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null) },
                new[] { Db(10, null, options: My240_415()) },
                new SupplyConvention { VoltageV = 415, Poles = 3 });

            var fix = r.Groups.Single().ConnectorFix!;
            Assert.Equal(415.0, fix.Args["voltage_v"]);
            Assert.Equal("power_balanced", fix.Args["system_type"]);
        }

        [Fact]
        public void DeviceWithoutConnectorIsABlockerNotAGroup()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1), Socket(9, conn: false, family: "JKR_Blank") },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            Assert.Single(r.Groups);
            var b = Assert.Single(r.Blockers);
            Assert.Equal("no_electrical_connector", b.Code);
            Assert.Contains("JKR_Blank", b.Detail);
            Assert.Contains("9", b.Detail);
            // The one genuinely un-toolable case still names its read, never
            // a bare "go to the Family Editor".
            Assert.Contains("get_connector_electrical_data", b.Fix);
        }

        // ─── verdicts ───────────────────────────────────────────────────

        [Fact]
        public void PanelWithMatchingSystemIsAssignableNow()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            var v = Assert.Single(r.Panels);
            Assert.Equal(PanelPlanVerdicts.AssignableNow, v.Verdict);
            Assert.Same(v, r.Recommended);
        }

        [Fact]
        public void PanelWithNoSystemButASeatingOneGetsSetVerdict()
        {
            // The exact UAT template state: system exists in the model, the
            // panel was placed without one.
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, null, options: My240_415()) }, My);

            var v = Assert.Single(r.Panels);
            Assert.Equal(PanelPlanVerdicts.NeedsDistributionSystemSet, v.Verdict);
            Assert.Contains("NO distribution system", v.Reason);
        }

        [Fact]
        public void NothingSuitableAnywhereGetsCreateVerdictWithSpec()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, null) }, My);   // zero options in the model

            var v = Assert.Single(r.Panels);
            Assert.Equal(PanelPlanVerdicts.NeedsDistributionSystemCreated, v.Verdict);
            Assert.NotNull(v.Match.Create);
        }

        [Fact]
        public void WrongPoleCountPrefersCreatingASystemOverAFamilyEdit()
        {
            // 240 V 1-pole on a US board seats only at 2 poles — but a
            // Malaysian 240/415 system seats it at 1 pole with NO family
            // change, so the created-system route wins and the pole-change
            // alternative stays visible in `match` for the agent.
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, "120/240 Single", options: Us120_240()) }, My);

            var v = Assert.Single(r.Panels);
            Assert.Equal(PanelPlanVerdicts.NeedsDistributionSystemCreated, v.Verdict);
            Assert.NotEmpty(v.Match.FitsWithPoleChange);
            Assert.Equal(240, v.Match.Create!.LineToGroundV);
        }

        [Fact]
        public void FullPanelIsReportedFullWithTheHonestException()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, "240/415V Three Phase", total: 12, used: 12, options: My240_415()) }, My);

            var v = Assert.Single(r.Panels);
            Assert.Equal(PanelPlanVerdicts.PanelFull, v.Verdict);
            // panel_full is the one verdict where another board is genuine.
            Assert.Contains("another existing board", v.Reason);
            Assert.Null(r.Recommended);
        }

        [Fact]
        public void UnknownSlotCountIsNeverFull()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, "240/415V Three Phase", total: 0, used: 0, options: My240_415()) }, My);

            Assert.Equal(PanelPlanVerdicts.AssignableNow, r.Panels.Single().Verdict);
        }

        [Fact]
        public void PanelRejectingEverySystemIsRankedLastWithReason()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, null, options: My240_415(accepts: false)) }, My);

            var v = Assert.Single(r.Panels);
            Assert.Equal(PanelPlanVerdicts.PanelRejectsAll, v.Verdict);
            Assert.Null(r.Recommended);
        }

        [Fact]
        public void FixGroupDemandUsesTheConventionForJudging()
        {
            // 0 V sockets judged AS IF fixed to 240/1 — the plan sees past the
            // broken family to the whole route.
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null) },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            Assert.Equal(PanelPlanVerdicts.AssignableNow, r.Panels.Single().Verdict);
        }

        // ─── ranking ────────────────────────────────────────────────────

        [Fact]
        public void VerdictOrderDominatesRanking()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[]
                {
                    Db(30, null, name: "DB-CREATE"),
                    Db(20, null, name: "DB-SET", options: My240_415()),
                    Db(10, "240/415V Three Phase", name: "DB-NOW", options: My240_415()),
                }, My);

            Assert.Equal(new[] { "DB-NOW", "DB-SET", "DB-CREATE" },
                         r.Panels.Select(p => p.Name).ToArray());
            Assert.Equal(new[] { 1, 2, 3 }, r.Panels.Select(p => p.Rank).ToArray());
        }

        [Fact]
        public void FreeSlotsBreakTiesWithinAVerdict()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[]
                {
                    Db(10, "240/415V Three Phase", total: 12, used: 10, name: "DB-TIGHT", options: My240_415()),
                    Db(20, "240/415V Three Phase", total: 12, used: 2, name: "DB-ROOMY", options: My240_415()),
                }, My);

            Assert.Equal("DB-ROOMY", r.Panels[0].Name);
        }

        [Fact]
        public void DistanceBreaksTiesAfterSlotsAndNullDistanceSortsLast()
        {
            var near = Db(20, "240/415V Three Phase", name: "DB-NEAR", options: My240_415());
            near.DistanceMm = 1000;
            var far = Db(10, "240/415V Three Phase", name: "DB-FAR", options: My240_415());
            far.DistanceMm = 9000;
            var unknown = Db(5, "240/415V Three Phase", name: "DB-UNKNOWN", options: My240_415());

            var r = PanelAssignmentPlan.Build(new[] { Socket(1) }, new[] { far, unknown, near }, My);

            Assert.Equal(new[] { "DB-NEAR", "DB-FAR", "DB-UNKNOWN" },
                         r.Panels.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void LowestIdWinsWhenEverythingElseTies()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[]
                {
                    Db(20, "240/415V Three Phase", name: "DB-B", options: My240_415()),
                    Db(10, "240/415V Three Phase", name: "DB-A", options: My240_415()),
                }, My);

            Assert.Equal(10, r.Panels[0].PanelId);
        }

        [Fact]
        public void RecommendedSkipsFullAndRejectingBoards()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[]
                {
                    Db(10, "240/415V Three Phase", total: 4, used: 4, name: "DB-FULL", options: My240_415()),
                    Db(20, null, name: "DB-SET", options: My240_415()),
                }, My);

            Assert.Equal("DB-SET", r.Recommended!.Name);
        }

        // ─── steps ──────────────────────────────────────────────────────

        [Fact]
        public void AssignableNowIsOneStep()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1), Socket(2) },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            var s = Assert.Single(r.Recommended!.Steps);
            Assert.Equal("create_circuit", s.Tool);
            Assert.Equal(new long[] { 1, 2 }, s.Args["element_ids"]);
            Assert.Equal(10L, s.Args["panel_id"]);
        }

        [Fact]
        public void SetVerdictIsSetSystemThenCircuit()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, null, options: My240_415()) }, My);

            var steps = r.Recommended!.Steps;
            Assert.Equal(new[] { "set_distribution_system", "create_circuit" },
                         steps.Select(s => s.Tool).ToArray());
            Assert.Equal("240/415V Three Phase", steps[0].Args["distribution_system"]);
            Assert.Equal(new[] { 1, 2 }, steps.Select(s => s.Order).ToArray());
        }

        [Fact]
        public void CreateVerdictIsCreateSetCircuit()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new[] { Db(10, null) }, My);

            Assert.Equal(new[] { "create_distribution_system", "set_distribution_system", "create_circuit" },
                         r.Recommended!.Steps.Select(s => s.Tool).ToArray());
        }

        [Fact]
        public void FixGroupPrependsTheConnectorWriteToEveryRoute()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null) },
                new[] { Db(10, null) }, My);

            var steps = r.Recommended!.Steps;
            Assert.Equal(new[]
            {
                "set_connector_electrical_data", "create_distribution_system",
                "set_distribution_system", "create_circuit",
            }, steps.Select(s => s.Tool).ToArray());
            Assert.Equal(new[] { 1, 2, 3, 4 }, steps.Select(s => s.Order).ToArray());
        }

        [Fact]
        public void EveryMutatingStepDemandsConfirmation()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null) },
                new[] { Db(10, null) }, My);

            Assert.All(r.Recommended!.Steps.Where(s => s.Mutates),
                       s => Assert.True(s.NeedsUserConfirm));
        }

        [Fact]
        public void ConventionDerivedStepsAreFlaggedProposals()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null) },
                new[] { Db(10, null) }, My);

            var steps = r.Recommended!.Steps;
            Assert.True(steps.First(s => s.Tool == "set_connector_electrical_data").IsProposal);
            Assert.True(steps.First(s => s.Tool == "create_distribution_system").IsProposal);
            Assert.False(steps.First(s => s.Tool == "create_circuit").IsProposal);
        }

        [Fact]
        public void MeasuredDemandCreateStepIsNotAProposal()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },       // measured 240 V
                new[] { Db(10, null) }, My);

            Assert.False(r.Recommended!.Steps
                .First(s => s.Tool == "create_distribution_system").IsProposal);
        }

        [Fact]
        public void NoStepEverContainsAProhibition()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null), Socket(2) },
                new[] { Db(10, null, options: Us120_240()), Db(20, null) }, My);

            foreach (var v in r.Panels)
                foreach (var s in v.Steps)
                {
                    Assert.DoesNotContain("do not", s.Reason.ToLowerInvariant());
                    Assert.DoesNotContain("never ", s.Reason.ToLowerInvariant());
                }
            Assert.DoesNotContain("do not", r.Summary.ToLowerInvariant());
        }

        // ─── degenerate inputs ──────────────────────────────────────────

        [Fact]
        public void NoDevicesSaysSoWithoutThrowing()
        {
            var r = PanelAssignmentPlan.Build(
                new DevicePlanFacts[0],
                new[] { Db(10, null) }, My);
            Assert.NotEmpty(r.Summary);
            Assert.Empty(r.Panels);
        }

        [Fact]
        public void NoPanelsBlocksWithCreatePanelAsNextAction()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1) },
                new PanelPlanFacts[0], My);

            var b = Assert.Single(r.Blockers);
            Assert.Equal("no_panels_in_model", b.Code);
            Assert.Contains("create_panel", b.Fix);
            Assert.Contains("distribution_system", b.Fix);
            Assert.Null(r.Recommended);
        }

        [Fact]
        public void AllDevicesConnectorlessStillReportsCleanly()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, conn: false), Socket(2, conn: false) },
                new[] { Db(10, null) }, My);

            Assert.Empty(r.Groups);
            Assert.NotEmpty(r.Blockers);
            Assert.NotEmpty(r.Summary);
        }

        [Fact]
        public void NullConventionFallsBackToMalaysianDefaults()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null) },
                new[] { Db(10, null) }, null);

            Assert.Equal(240.0, r.Groups.Single().ConnectorFix!.Args["voltage_v"]);
        }

        [Fact]
        public void MultipleDemandsGetTheRePlanNote()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1), Socket(5, v: 415, poles: 3, family: "JKR_Motor") },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            Assert.NotNull(r.Note);
            Assert.Contains("element_ids", r.Note);
        }

        [Fact]
        public void SummaryFlagsConventionDemandAsUnconfirmed()
        {
            var r = PanelAssignmentPlan.Build(
                new[] { Socket(1, v: null) },
                new[] { Db(10, "240/415V Three Phase", options: My240_415()) }, My);

            Assert.Contains("CONVENTION", r.Summary);
        }
    }
}
