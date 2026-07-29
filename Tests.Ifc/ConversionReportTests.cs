// Tests/ConversionReportTests.cs
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.IfcConvert;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ConversionReportTests
    {
        static IfcElement Wall(bool convertible, string? reason = null) => new()
        {
            SourceId = 1, Entity = IfcEntity.Wall, Convertible = convertible, Reason = reason,
            StartMm = new[] { 0.0, 0, 0 }, EndMm = new[] { 3000.0, 0, 0 }, HeightMm = 3000, ThicknessMm = 200,
        };

        [Fact]
        public void Add_ConvertedAndKept_TalliesAndReportsReasons()
        {
            var report = new ConversionReport();
            report.Add(Wall(true), new NativeStep("create_wall", new() { ["level"] = "L1" }));
            report.Add(Wall(false, "curved geometry"), null);

            Assert.Equal(1, report.ConvertedCounts["Wall"]);
            Assert.Single(report.KeptAsIs);
            Assert.Equal("curved geometry", report.KeptAsIs[0].Reason);

            var dict = report.ToDict();
            Assert.True(dict.ContainsKey("converted"));
            Assert.True(dict.ContainsKey("keptAsIs"));
        }

        [Fact]
        public void NativeStep_BatchArgs_WrapsStepsForExecuteRevitBatch()
        {
            var steps = new List<NativeStep> { new("create_wall", new() { ["level"] = "L1" }) };
            var args = NativeStep.BatchArgs(steps);
            var json = System.Text.Json.JsonSerializer.Serialize(args);
            Assert.Contains("\"steps\"", json);
            Assert.Contains("\"tool\":\"create_wall\"", json);
        }
    }
}
