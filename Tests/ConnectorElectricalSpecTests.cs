using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>Argument rules for set_connector_electrical_data. These exist
    /// because the tool opens a family document and reloads it — every refusal
    /// that can be decided from the arguments alone must be decided BEFORE that
    /// happens, or a bad request costs a family edit.</summary>
    public class ConnectorElectricalSpecTests
    {
        private static ConnectorElectricalSpec Build(
            double? voltageV = null, long? poles = null, string? systemType = null,
            string? loadClassification = null, double? apparentLoadVa = null,
            string? associateTo = null, long? connectorIndex = null, bool dryRun = false)
            => ConnectorElectricalData.Build(voltageV, poles, systemType, loadClassification,
                apparentLoadVa, associateTo, connectorIndex, dryRun);

        // ─── the bug this tool was written for ──────────────────────────

        [Fact]
        public void ZeroVoltageIsRefused()
        {
            // 0 V is the broken state, not a fix for it. Accepting it would let
            // the agent report success having written the same value back.
            var spec = Build(voltageV: 0);
            Assert.False(spec.IsValid);
            Assert.Contains(spec.Errors, e => e.Contains("greater than 0"));
        }

        [Fact]
        public void ZeroVoltageErrorNamesTheRealSupplyValues()
        {
            var spec = Build(voltageV: 0);
            var msg = string.Join(" ", spec.Errors);
            Assert.Contains("240", msg);
            Assert.Contains("415", msg);
        }

        [Theory]
        [InlineData(-240)]
        [InlineData(1_000_001)]
        public void ImplausibleVoltageIsRefused(double v) => Assert.False(Build(voltageV: v).IsValid);

        [Theory]
        [InlineData(240)]
        [InlineData(415)]
        [InlineData(24)]
        public void PlausibleVoltageIsAccepted(double v)
        {
            var spec = Build(voltageV: v);
            Assert.True(spec.IsValid);
            Assert.Equal(v, spec.VoltageV);
        }

        [Fact]
        public void NonFiniteVoltageIsRefused()
        {
            Assert.False(Build(voltageV: double.NaN).IsValid);
            Assert.False(Build(voltageV: double.PositiveInfinity).IsValid);
        }

        // ─── poles ──────────────────────────────────────────────────────

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ValidPolesAccepted(long p) => Assert.True(Build(voltageV: 240, poles: p).IsValid);

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(-1)]
        public void InvalidPolesRefused(long p) => Assert.False(Build(voltageV: 240, poles: p).IsValid);

        // ─── system classification vocabulary ───────────────────────────

        [Theory]
        [InlineData("power_unbalanced", "PowerUnBalanced")]
        [InlineData("single_phase", "PowerUnBalanced")]
        [InlineData("1ph", "PowerUnBalanced")]
        [InlineData("satu_fasa", "PowerUnBalanced")]
        [InlineData("three_phase", "PowerBalanced")]
        [InlineData("3ph", "PowerBalanced")]
        [InlineData("power", "PowerCircuit")]
        [InlineData("fire_alarm", "FireAlarm")]
        [InlineData("nurse_call", "NurseCall")]
        public void SystemTypeWordsMap(string word, string expected)
            => Assert.Equal(expected, ConnectorElectricalData.SystemClassificationName(word));

        [Fact]
        public void SystemTypeIsCaseAndWhitespaceInsensitive()
            => Assert.Equal("PowerUnBalanced",
                ConnectorElectricalData.SystemClassificationName("  Power_UnBalanced  "));

        [Fact]
        public void DataMapsToDataCircuitNotData()
            // MEPSystemClassification spells it DataCircuit; ElectricalSystemType
            // spells it Data. Mixing the two is a silent Enum.TryParse failure.
            => Assert.Equal("DataCircuit", ConnectorElectricalData.SystemClassificationName("data"));

        [Fact]
        public void UnknownSystemTypeIsRefusedAndListsWhatIsAccepted()
        {
            var spec = Build(voltageV: 240, systemType: "power_threephase_wye");
            Assert.False(spec.IsValid);
            Assert.Contains(spec.Errors, e => e.Contains("power_unbalanced"));
        }

        [Fact]
        public void OmittedSystemTypeLeavesClassificationAlone()
        {
            var spec = Build(voltageV: 240);
            Assert.True(spec.IsValid);
            Assert.Null(spec.SystemClassificationName);
        }

        [Fact]
        public void VoltageOnANonPowerClassificationIsRefused()
        {
            // Almost always a mis-typed system_type, and Revit accepts it
            // silently, so the model looks fine and behaves wrongly.
            var spec = Build(voltageV: 240, systemType: "fire_alarm");
            Assert.False(spec.IsValid);
            Assert.Contains(spec.Errors, e => e.Contains("not a power classification"));
        }

        [Fact]
        public void PowerClassificationsAreRecognised()
        {
            Assert.True(ConnectorElectricalData.IsPowerClassification("PowerUnBalanced"));
            Assert.True(ConnectorElectricalData.IsPowerClassification("PowerBalanced"));
            Assert.True(ConnectorElectricalData.IsPowerClassification("PowerCircuit"));
            Assert.False(ConnectorElectricalData.IsPowerClassification("DataCircuit"));
            Assert.False(ConnectorElectricalData.IsPowerClassification(null));
        }

        [Fact]
        public void AcceptedSystemTypesIsStableAndNonEmpty()
        {
            var once = ConnectorElectricalData.AcceptedSystemTypes.ToList();
            var twice = ConnectorElectricalData.AcceptedSystemTypes.ToList();
            Assert.NotEmpty(once);
            Assert.Equal(once, twice);
        }

        // ─── the association trap ───────────────────────────────────────

        [Fact]
        public void AssociationWithoutVoltageIsRefused()
        {
            // Binding with no value creates an empty parameter and leaves the
            // connector exactly as broken as it was — the failure mode that
            // makes "just set the parameter" advice useless.
            var spec = Build(poles: 1, associateTo: "Kadaran_Voltan");
            Assert.False(spec.IsValid);
            Assert.Contains(spec.Errors, e => e.Contains("needs voltage_v"));
        }

        [Fact]
        public void AssociationWithVoltageIsAccepted()
        {
            var spec = Build(voltageV: 240, associateTo: "Kadaran_Voltan");
            Assert.True(spec.IsValid);
            Assert.Equal("Kadaran_Voltan", spec.AssociateToParameter);
        }

        [Theory]
        [InlineData("Kadaran{Voltan}")]
        [InlineData("Voltan:Utama")]
        [InlineData("a|b")]
        [InlineData("x<y")]
        public void ForbiddenCharactersInParameterNameAreRefused(string name)
            => Assert.NotNull(ConnectorElectricalData.ValidateParameterName(name));

        [Fact]
        public void PaddedParameterNameIsRefused()
            // Revit stores the padding verbatim and the parameter can never be
            // matched by name afterwards.
            => Assert.NotNull(ConnectorElectricalData.ValidateParameterName(" Kadaran_Voltan "));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyParameterNameIsRefused(string? name)
            => Assert.NotNull(ConnectorElectricalData.ValidateParameterName(name));

        [Fact]
        public void OrdinaryParameterNameIsAccepted()
            => Assert.Null(ConnectorElectricalData.ValidateParameterName("Kadaran_Voltan_jkr_stv"));

        // ─── no-op detection ────────────────────────────────────────────

        [Fact]
        public void RequestWithNothingToSetIsRefused()
        {
            // ok:true on a call that wrote nothing is how an agent concludes it
            // fixed a family it never touched.
            var spec = Build();
            Assert.False(spec.IsValid);
            Assert.Contains(spec.Errors, e => e.Contains("nothing to set"));
        }

        [Fact]
        public void LoadClassificationAloneIsEnoughToBeAWrite()
        {
            var spec = Build(loadClassification: "Receptacle");
            Assert.True(spec.IsValid);
            Assert.True(spec.HasAnyWrite);
        }

        [Fact]
        public void LoadClassificationIsTrimmed()
            => Assert.Equal("Receptacle", Build(loadClassification: "  Receptacle  ").LoadClassification);

        // ─── apparent load ──────────────────────────────────────────────

        [Fact]
        public void NegativeApparentLoadIsRefused()
            => Assert.False(Build(apparentLoadVa: -1).IsValid);

        [Fact]
        public void ZeroApparentLoadIsAllowed()
            // Unlike voltage, 0 VA is a legitimate value — a spare outlet draws
            // nothing until something is plugged in.
            => Assert.True(Build(apparentLoadVa: 0).IsValid);

        // ─── connector index ────────────────────────────────────────────

        [Fact]
        public void NegativeConnectorIndexIsRefused()
            => Assert.False(Build(voltageV: 240, connectorIndex: -1).IsValid);

        [Fact]
        public void OmittedConnectorIndexMeansEveryConnector()
            => Assert.Null(Build(voltageV: 240).ConnectorIndex);

        [Fact]
        public void ConnectorIndexZeroIsKeptNotTreatedAsAbsent()
        {
            var spec = Build(voltageV: 240, connectorIndex: 0);
            Assert.True(spec.IsValid);
            Assert.Equal(0, spec.ConnectorIndex);
        }

        [Fact]
        public void DryRunIsCarried() => Assert.True(Build(voltageV: 240, dryRun: true).DryRun);

        // ─── read-side advice ───────────────────────────────────────────

        [Fact]
        public void AbsentVoltageAdviceExplainsWhyTheCircuitStillBuilds()
        {
            // The whole diagnostic gap: create_circuit's guard treats an
            // unreadable voltage as "unknown, let it pass".
            var advice = ConnectorElectricalData.VoltageAdvice(ConnectorElectricalData.SourceAbsent, null);
            Assert.NotNull(advice);
            Assert.Contains("unknown", advice!);
            Assert.Contains("set_connector_electrical_data", advice);
        }

        [Fact]
        public void ZeroVoltageAdviceIsGiven()
        {
            var advice = ConnectorElectricalData.VoltageAdvice(ConnectorElectricalData.SourceInstance, 0);
            Assert.NotNull(advice);
            Assert.Contains("0 V", advice!);
        }

        [Fact]
        public void HealthyVoltageGetsNoAdvice()
        {
            Assert.Null(ConnectorElectricalData.VoltageAdvice(ConnectorElectricalData.SourceInstance, 240));
            Assert.Null(ConnectorElectricalData.VoltageAdvice(ConnectorElectricalData.SourceSymbol, 415));
        }

        // ─── multiple problems ──────────────────────────────────────────

        [Fact]
        public void EveryProblemIsReportedNotJustTheFirst()
        {
            // One round trip per mistake is one family edit per mistake.
            var spec = Build(voltageV: 0, poles: 7, systemType: "nonsense");
            Assert.False(spec.IsValid);
            Assert.True(spec.Errors.Count >= 3, $"expected 3+ errors, got {spec.Errors.Count}");
        }
    }
}
