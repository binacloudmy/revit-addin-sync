#nullable enable
// W3 Task 4 (spec §11 #5; ruling R14: typed parsing, invalid_args). The
// set_parameters arguments as the add-in receives them: exactly one of
// target{filter|handle|ids} or table_rows, a parameter, a value with target,
// host-only filters, and tables keyed by element id or one match column.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SetParametersArgsTests
    {
        private static SetParametersArgs Parse(string json) => SetParametersArgs.Parse(JsonDocument.Parse(json).RootElement);

        private static WriteRefusal Refused(string json) => Assert.Throws<WriteRefusal>(() => Parse(json));

        [Fact]
        public void ParsesIdsHandleAndFilterTargets()
        {
            var ids = Parse("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"ids\":[3,1]}}");
            Assert.Equal(new long[] { 3, 1 }, ids.Target!.Ids);
            Assert.Equal("Mark", ids.Param);
            Assert.False(ids.TypeParam);

            var handle = Parse("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"handle\":\"h_7f3a91c2\"},\"type_param\":true}");
            Assert.Equal("h_7f3a91c2", handle.Target!.Handle);
            Assert.True(handle.TypeParam);

            var filter = Parse("{\"param\":\"Height Offset From Level\",\"value\":\"2700 mm\",\"target\":{\"filter\":{\"category\":\"Ceilings\",\"level\":\"Level 2\",\"parameter\":{\"name\":\"Mark\",\"op\":\"empty\"}}}}");
            Assert.Equal("Ceilings", filter.Target!.Filter!.Category);
            Assert.Equal("Level 2", filter.Target.Filter.Level);
            Assert.Equal(ConditionOp.Empty, filter.Target.Filter.Parameter!.Op);
            Assert.Contains("Level 2", filter.Target.Filter.Describe());
        }

        [Theory]
        [InlineData("{\"value\":\"A1\",\"target\":{\"ids\":[1]}}")]                                    // no param
        [InlineData("{\"param\":\"Mark\",\"target\":{\"ids\":[1]}}")]                                  // no value
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\"}")]                                          // no target
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{}}")]                            // empty target
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"ids\":[1],\"handle\":\"h\"}}")] // two targets
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"ids\":[]}}")]
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"ids\":[\"x\"]}}")]               // wrong type
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"filter\":{}}}")]               // matches everything
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"filter\":{\"category\":\"Doors\",\"parameter\":{\"name\":\"Mark\",\"op\":\"eq\"}}}}")]
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"filter\":{\"category\":\"Doors\",\"parameter\":{\"name\":\"Mark\",\"op\":\"like\",\"value\":\"x\"}}}}")]
        [InlineData("{\"param\":\"Mark\",\"from_table\":\"doors.xlsx\"}")]                              // not rewritten by the cloud
        [InlineData("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"ids\":[1]},\"table_rows\":[{\"id\":1,\"Mark\":\"A\"}]}")]
        [InlineData("[1,2]")]
        public void RefusesWhatIsNotOneClearTarget(string json)
        {
            Assert.Equal(ErrorCodes.InvalidArgs, Refused(json).Code);
        }

        [Fact]
        public void RefusesLinkTargetsBecauseWritesAreHostOnly()
        {
            var r = Refused("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"filter\":{\"category\":\"Doors\",\"source\":\"link:STR_v3\"}}}");
            Assert.Equal(ErrorCodes.InvalidArgs, r.Code);
            Assert.Contains("host", r.Message);
            // "host" itself is fine.
            Parse("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"filter\":{\"category\":\"Doors\",\"source\":\"host\"}}}");
        }

        [Fact]
        public void TableByIdColumn()
        {
            var a = Parse("{\"param\":\"Fire Rating\",\"table_rows\":[{\"Element ID\":101,\"Fire Rating\":\"FR60\"},{\"Element ID\":\"102\",\"Fire Rating\":\"FR30\"}]}");
            Assert.Equal(TableKey.Id, a.Table!.Key);
            Assert.Equal(2, a.Table.Rows.Count);
            Assert.Equal(101, a.Table.Rows[0].Id);
            Assert.Equal(102, a.Table.Rows[1].Id);
            Assert.Equal("FR30", a.Table.Rows[1].Value.GetString());
        }

        [Fact]
        public void TableByOneMatchColumnAndAValueColumn()
        {
            var a = Parse("{\"param\":\"Comments\",\"table_rows\":[{\"Mark\":\"D1\",\"value\":\"Pintu utama\"},{\"Mark\":\"D2\",\"value\":\"Pintu tandas\"}]}");
            Assert.Equal(TableKey.Match, a.Table!.Key);
            Assert.Equal("Mark", a.Table.MatchParam);
            Assert.Equal("D2", a.Table.Rows[1].MatchValue);
        }

        [Fact]
        public void TableRefusesWithoutAValueOrKeyColumnOrWithConflicts()
        {
            Assert.Contains("column", Refused("{\"param\":\"Comments\",\"table_rows\":[{\"Mark\":\"D1\",\"Note\":\"x\"}]}").Message);
            Assert.Contains("identif", Refused("{\"param\":\"Comments\",\"table_rows\":[{\"Comments\":\"x\",\"Level\":\"L1\",\"Room\":\"R\"}]}").Message);
            Assert.Contains("conflict", Refused("{\"param\":\"Comments\",\"table_rows\":[{\"id\":1,\"Comments\":\"x\"},{\"id\":1,\"Comments\":\"y\"}]}").Message);
            Assert.Equal(ErrorCodes.InvalidArgs, Refused("{\"param\":\"Comments\",\"table_rows\":[{\"id\":\"abc\",\"Comments\":\"x\"}]}").Code);
        }

        [Fact]
        public void TableSkipsBlankKeysButCountsThem()
        {
            var a = Parse("{\"param\":\"Comments\",\"table_rows\":[{\"Mark\":\"D1\",\"Comments\":\"x\"},{\"Mark\":\"\",\"Comments\":\"y\"},{\"Mark\":null,\"Comments\":\"z\"}]}");
            Assert.Single(a.Table!.Rows);
            Assert.Equal(2, a.Table.BlankRows);
        }

        [Fact]
        public void ConditionEvaluatesOnReadings()
        {
            var empty = new FilterCondition("Mark", ConditionOp.Empty, null);
            Assert.True(empty.Matches(ParamReading.Missing));
            Assert.True(empty.Matches(ParamReading.OfText("")));
            Assert.False(empty.Matches(ParamReading.OfText("D1")));

            var eq = new FilterCondition("Mark", ConditionOp.Eq, JsonDocument.Parse("\"d1\"").RootElement);
            Assert.True(eq.Matches(ParamReading.OfText("D1")));
            Assert.False(new FilterCondition("Mark", ConditionOp.Ne, eq.Value).Matches(ParamReading.OfText("D1")));

            var contains = new FilterCondition("Comments", ConditionOp.Contains, JsonDocument.Parse("\"tandas\"").RootElement);
            Assert.True(contains.Matches(ParamReading.OfText("Pintu Tandas OKU")));

            var gt = new FilterCondition("Height", ConditionOp.Gt, JsonDocument.Parse("\"2.4 m\"").RootElement);
            Assert.True(gt.Matches(ParamReading.OfNumber(ValueKind.Length, 2700, "2700 mm", "mm")));
            Assert.False(gt.Matches(ParamReading.OfNumber(ValueKind.Length, 2400, "2400 mm", "mm")));
            var lt = new FilterCondition("Height", ConditionOp.Lt, JsonDocument.Parse("2500").RootElement);
            Assert.True(lt.Matches(ParamReading.OfNumber(ValueKind.Length, 2400, "2400 mm", "mm")));

            var eqLen = new FilterCondition("Height", ConditionOp.Eq, JsonDocument.Parse("\"2.7 m\"").RootElement);
            Assert.True(eqLen.Matches(ParamReading.OfNumber(ValueKind.Length, 2700.0000001, "2700 mm", "mm")));

            var bad = new FilterCondition("Height", ConditionOp.Gt, JsonDocument.Parse("\"tall\"").RootElement);
            Assert.Equal(ErrorCodes.InvalidArgs, Assert.Throws<WriteRefusal>(() => bad.Matches(ParamReading.OfNumber(ValueKind.Length, 1, "1 mm", "mm"))).Code);
        }
    }
}
