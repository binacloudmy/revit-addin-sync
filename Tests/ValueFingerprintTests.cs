#nullable enable
// W3 fix round 3 (ruling R75). Preview and commit may diverge on VALUES while
// touching the same ids (code can use hash codes or the ids of elements it
// just created), so every modified existing element is fingerprinted after the
// preview's inner commits and again after the commit: all parameter storage
// values, its type id and its location/bbox. A mismatch is diff_drift. At most
// 500 elements are compared, the first 500 by id.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ValueFingerprintTests
    {
        private static KeyValuePair<string, string> P(string k, string v) => new(k, v);

        [Fact]
        public void IsStableAcrossParameterOrderAndSensitiveToEveryPart()
        {
            var a = ValueFingerprint.Compute(new[] { P("Mark#-1001203", "W1"), P("Height#-1001300", ValueFingerprint.Number(9.84251968503937)) }, "501", "pt:0,0,0");
            var b = ValueFingerprint.Compute(new[] { P("Height#-1001300", ValueFingerprint.Number(9.84251968503937)), P("Mark#-1001203", "W1") }, "501", "pt:0,0,0");
            Assert.Equal(a, b);

            Assert.NotEqual(a, ValueFingerprint.Compute(new[] { P("Mark#-1001203", "W2"), P("Height#-1001300", ValueFingerprint.Number(9.84251968503937)) }, "501", "pt:0,0,0"));
            Assert.NotEqual(a, ValueFingerprint.Compute(new[] { P("Mark#-1001203", "W1"), P("Height#-1001300", ValueFingerprint.Number(9.84251968503937)) }, "502", "pt:0,0,0"));
            Assert.NotEqual(a, ValueFingerprint.Compute(new[] { P("Mark#-1001203", "W1"), P("Height#-1001300", ValueFingerprint.Number(9.84251968503937)) }, "501", "pt:0,0,1"));
        }

        [Fact]
        public void NumbersAreRoundedOnlyBelowMeasurableNoise()
        {
            Assert.Equal(ValueFingerprint.Number(2700.0), ValueFingerprint.Number(2700.00000000001));
            Assert.NotEqual(ValueFingerprint.Number(2700.0), ValueFingerprint.Number(2700.001));
            Assert.Equal(ValueFingerprint.Coordinate(1.0), ValueFingerprint.Coordinate(1.0000000001));
            Assert.NotEqual(ValueFingerprint.Coordinate(1.0), ValueFingerprint.Coordinate(1.001));
        }

        [Fact]
        public void SelectsTheFirstFiveHundredByIdOrder()
        {
            var ids = Enumerable.Range(1, 800).Select(i => (long)(1000 - i)).ToList();
            var chosen = ValueFingerprint.Select(ids);
            Assert.Equal(ValueFingerprint.MaxElements, chosen.Count);
            Assert.Equal(200L, chosen[0]);
            Assert.Equal(699L, chosen[chosen.Count - 1]);
            Assert.Equal(3, ValueFingerprint.Select(new long[] { 3, 1, 2, 3 }).Count);
        }

        private static ParamSample S(string name, long definitionId, string value, bool readOnly = false) =>
            new ParamSample(name, definitionId, value, readOnly);

        [Fact]
        public void ParametersDefinedInsideTheBatchAreKeyedAsNew()
        {
            // The batch created and bound a shared parameter: its definition id is a new
            // element whose id differs between preview (9001) and commit (9101).
            var preview = ValueFingerprint.FromParameters(new[] { S("Mark", -1001203, "s:W1"), S("JKR_Code", 9001, "s:A") }, "501", "pt:0,0,0", new HashSet<long> { 9001 });
            var commit = ValueFingerprint.FromParameters(new[] { S("Mark", -1001203, "s:W1"), S("JKR_Code", 9101, "s:A") }, "501", "pt:0,0,0", new HashSet<long> { 9101 });
            Assert.Equal(preview, commit);
            Assert.Equal("JKR_Code#new", ValueFingerprint.ParameterKey("JKR_Code", 9001, new HashSet<long> { 9001 }));
            Assert.Equal("Mark#-1001203", ValueFingerprint.ParameterKey("Mark", -1001203, new HashSet<long> { 9001 }));
            // A different value under the new definition is still drift.
            var other = ValueFingerprint.FromParameters(new[] { S("Mark", -1001203, "s:W1"), S("JKR_Code", 9101, "s:B") }, "501", "pt:0,0,0", new HashSet<long> { 9101 });
            Assert.NotEqual(preview, other);
        }

        [Fact]
        public void ReadOnlyComputedValuesAreLeftOut()
        {
            var a = ValueFingerprint.FromParameters(new[] { S("Mark", -1001203, "s:W1"), S("Area", -1012805, "d:12.000000001", readOnly: true) }, "501", "pt:0,0,0", new HashSet<long>());
            var b = ValueFingerprint.FromParameters(new[] { S("Mark", -1001203, "s:W1"), S("Area", -1012805, "d:12.000000002", readOnly: true) }, "501", "pt:0,0,0", new HashSet<long>());
            Assert.Equal(a, b);
            // Settable values still count.
            var c = ValueFingerprint.FromParameters(new[] { S("Mark", -1001203, "s:W2"), S("Area", -1012805, "d:12.000000001", readOnly: true) }, "501", "pt:0,0,0", new HashSet<long>());
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void AnElementUnreadableOnOneSideIsNamedInAWarning()
        {
            var preview = Modified((10, "aaa"), (11, "bbb"));
            // Id 11 was modified on both sides, but its values could not be read in the commit.
            var commit = Modified((10, "aaa"), (11, "unused"));
            commit.Values.Remove(11);
            commit.Unreadable.Add(11);

            Assert.True(ShapeComparer.Compare(Plans, preview, commit).IsMatch);   // not compared, never drift
            var warning = ValueFingerprint.UnreadWarning(preview, commit);
            Assert.NotNull(warning);
            Assert.Contains("11", warning);
            Assert.Null(ValueFingerprint.UnreadWarning(preview, Modified((10, "aaa"), (11, "bbb"))));
        }

        private static BatchShape Modified(params (long id, string fp)[] values)
        {
            var s = new BatchShape();
            foreach (var (id, fp) in values)
            {
                s.Modified.Add(id);
                s.CategoryOf[id] = "Walls";
                s.Values[id] = fp;
            }
            s.ValuesTotal = values.Length;
            return s;
        }

        private static readonly IReadOnlyList<WritePlan> Plans = new[] { new WritePlan { Tool = ToolNames.RunRevitCode } };

        [Fact]
        public void ValueDriftOnTheSameIdsIsDiffDrift()
        {
            var preview = Modified((10, "aaa"), (11, "bbb"));
            Assert.True(ShapeComparer.Compare(Plans, preview, Modified((10, "aaa"), (11, "bbb"))).IsMatch);

            var drift = ShapeComparer.Compare(Plans, preview, Modified((10, "aaa"), (11, "ccc")));
            Assert.False(drift.IsMatch);
            Assert.Contains("11", drift.Reason);
            Assert.Contains("values", drift.Reason);
        }

        [Fact]
        public void IdsFingerprintedOnOnlyOneSideAreNotCompared()
        {
            var preview = Modified((10, "aaa"), (11, "bbb"));
            var commit = Modified((10, "aaa"), (11, "bbb"));
            commit.Values.Remove(11);
            Assert.True(ShapeComparer.Compare(Plans, preview, commit).IsMatch);
        }

        [Fact]
        public void TheCapWarningNamesHowManyWereCompared()
        {
            var s = Modified((10, "a"));
            s.ValuesTotal = 742;
            Assert.Equal("Values were compared for 1 of 742 changed elements.", ValueFingerprint.CapWarning(s));
            Assert.Null(ValueFingerprint.CapWarning(Modified((10, "a"))));
        }
    }
}
