#nullable enable
// RunCompare: how much of a reference's conduit lies along the created runs, and the reverse.
// Synthetic segments only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class RunCompareTests
    {
        private static RunSegment S(double x1, double y1, double x2, double y2, double z = 2700) =>
            new RunSegment { Kind = "conduit", X1 = x1, Y1 = y1, Z1 = z, X2 = x2, Y2 = y2, Z2 = z };

        [Fact]
        public void A_created_run_within_100mm_covers_the_reference()
        {
            RunCompareResult r = RunCompare.Compare(new[] { S(0, 0, 10000, 0) }, new[] { S(0, 80, 6000, 80), S(6000, 80, 10000, 80) });

            Assert.Equal(1.0, r.ReferenceCoveredShare, 3);
            Assert.Equal(1.0, r.CreatedCoveredShare, 3);
            Assert.Equal(0, r.ExtraLengthMm, 3);

            RunCompareResult off = RunCompare.Compare(new[] { S(0, 0, 10000, 0) }, new[] { S(0, 150, 10000, 150) });
            Assert.Equal(0, off.ReferenceCoveredShare, 3);
        }

        [Fact]
        public void A_missing_run_lowers_the_share()
        {
            RunCompareResult r = RunCompare.Compare(new[] { S(0, 0, 10000, 0), S(0, 5000, 10000, 5000) }, new[] { S(0, 0, 10000, 0) });
            Assert.Equal(0.5, r.ReferenceCoveredShare, 3);
            Assert.Equal(20000, r.ReferenceLengthMm, 3);
            Assert.Equal(1.0, r.CreatedCoveredShare, 3);
        }

        [Fact]
        public void Extra_created_length_is_reported()
        {
            RunCompareResult r = RunCompare.Compare(new[] { S(0, 0, 10000, 0) }, new[] { S(0, 0, 10000, 0), S(0, 8000, 5000, 8000) });
            Assert.Equal(1.0, r.ReferenceCoveredShare, 3);
            Assert.Equal(5000, r.ExtraLengthMm, 1);
            Assert.Equal(10.0 / 15.0, r.CreatedCoveredShare, 3);
            Assert.Equal(5.0, (double)r.ToWire()["extra_length_m"]!, 2);
        }

        [Fact]
        public void Height_counts_only_in_3d()
        {
            RunSegment[] reference = { S(0, 0, 10000, 0, 2700) };
            RunSegment[] created = { S(0, 0, 10000, 0, 3200) };
            Assert.Equal(1.0, RunCompare.Compare(reference, created).ReferenceCoveredShare, 3);
            Assert.Equal(0, RunCompare.Compare(reference, created, 100, in3d: true).ReferenceCoveredShare, 3);
        }

        [Fact]
        public void The_dump_runs_are_read_by_kind()
        {
            const string dump = @"{""runs"": [
                {""id"": 11, ""kind"": ""conduit"", ""type"": ""Conduit with Fittings : PVC"", ""start_mm"": [0, 0, 2700], ""end_mm"": [1000, 0, 2700]},
                {""id"": 12, ""kind"": ""cable_tray"", ""type"": ""Cable Tray with Fittings : Trunking"", ""start_mm"": [0, 0, 2900], ""end_mm"": [0, 1000, 2900]},
                {""id"": 13, ""kind"": ""conduit"", ""start_mm"": [1, 2]}
            ]}";
            using (JsonDocument doc = JsonDocument.Parse(dump))
            {
                List<RunSegment> all = RunCompare.Read(doc.RootElement);
                Assert.Equal(new long[] { 11, 12 }, all.Select(s => s.Id));
                RunSegment conduit = Assert.Single(RunCompare.Read(doc.RootElement, "conduit"));
                Assert.Equal(1000, conduit.X2);
                Assert.Equal(2700, conduit.Z1);
                Assert.Equal("Conduit with Fittings : PVC", conduit.Type);
            }
        }
    }
}
