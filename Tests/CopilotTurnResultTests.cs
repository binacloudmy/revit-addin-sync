#nullable enable
// AI-294: turn_end.result. The shared fixtures in contracts/copilot/examples/turn-result.json
// must round-trip through the add-in's contract types, the invariants must name what the
// invalid cases break, and a result the add-in cannot read must never lose the turn_end.
//
// Revit-free: compiles in Tests.csproj. The contract files are copied next to the test
// assembly (Tests.csproj W0 block).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotTurnResultTests
    {
        private static string ContractsDir([CallerFilePath] string thisFile = "")
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "contracts", "copilot");
            if (File.Exists(Path.Combine(copied, "tools.json"))) return copied;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "contracts", "copilot");
                if (File.Exists(Path.Combine(candidate, "tools.json"))) return candidate;
            }
            var fromSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "contracts", "copilot"));
            if (File.Exists(Path.Combine(fromSource, "tools.json"))) return fromSource;
            throw new DirectoryNotFoundException("contracts/copilot not found");
        }

        private static JsonElement Fixtures()
        {
            var text = File.ReadAllText(Path.Combine(ContractsDir(), "examples", "turn-result.json"));
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        private static IEnumerable<object[]> Names(string section) =>
            Fixtures().GetProperty(section).EnumerateObject().Select(p => new object[] { p.Name });

        public static IEnumerable<object[]> ValidNames() => Names("valid");
        public static IEnumerable<object[]> InvalidNames() => Names("invalid");
        public static IEnumerable<object[]> ToleranceNames() => Names("reader_tolerance");

        private static JsonElement Case(string section, string name) => Fixtures().GetProperty(section).GetProperty(name);

        private static TurnEndDto ReadEnd(JsonElement data) =>
            CopilotJson.Deserialize<TurnEndDto>(data.GetRawText()) ?? throw new InvalidDataException("turn_end deserialized to null");

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void Valid_TurnEndRoundTripsAndHasNoViolations(string name)
        {
            var data = Case("valid", name);
            var end = ReadEnd(data);
            var again = JsonNode.Parse(CopilotJson.Serialize(end));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data.GetRawText()), again), name + " changed on the round trip:\n" + again);

            if (!data.TryGetProperty("result", out var raw))
            {
                Assert.Null(end.Result);
                Assert.Null(end.ReadResult());
                return;
            }

            // The typed result alone round-trips too: every key of the schema has a property.
            var result = end.ReadResult();
            Assert.NotNull(result);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(raw.GetRawText()), JsonNode.Parse(CopilotJson.Serialize(result!))),
                name + ": the typed result lost or changed a value");
            Assert.Empty(TurnResultInvariants.Violations(result!));
        }

        [Fact]
        public void Valid_FixtureCoversEveryKindSourceKindAndActionKind()
        {
            var results = Fixtures().GetProperty("valid").EnumerateObject()
                .Select(p => p.Value).Where(v => v.TryGetProperty("result", out _))
                .Select(v => ReadEnd(v).ReadResult()!).ToList();
            Assert.Equal(TurnResultKinds.All.OrderBy(x => x), results.Select(r => r.Kind).Distinct().OrderBy(x => x));
            var sources = results.SelectMany(r => r.Values ?? new()).Select(v => v.Source?.Kind).Where(k => k != null).Distinct().OrderBy(x => x);
            Assert.Equal(ResultSourceKinds.All.OrderBy(x => x), sources);
            var actions = results.SelectMany(r => r.Checks ?? new()).SelectMany(c => c.Actions ?? new()).Select(a => a.Kind).Distinct().OrderBy(x => x);
            Assert.Equal(ResultActionKinds.All.OrderBy(x => x), actions);
        }

        [Fact]
        public void Valid_BrochureValuesNameAPageOrThePrompt()
        {
            var result = ReadEnd(Case("valid", "brochure_fill_ms")).ReadResult()!;
            Assert.Equal("ms", result.Lang);
            var url = result.Values!.Single(v => v.Param == "URL");
            Assert.Equal(ResultSourceKinds.Document, url.Source!.Kind);
            Assert.Equal(8, url.Source.Page);
            Assert.Equal("www.pcolite.com", url.After!.Value.GetString());
            Assert.Equal(ResultSourceKinds.Prompt, result.Values!.Single(v => v.Param == "Description").Source!.Kind);
            Assert.Equal(52, result.Counts!.Elements);
        }

        [Theory]
        [MemberData(nameof(InvalidNames))]
        public void Invalid_NeverThrowsAndIsReportedAsTheFixtureSays(string name)
        {
            var c = Case("invalid", name);
            var end = ReadEnd(c.GetProperty("data"));          // the turn_end itself is always readable
            var result = end.ReadResult();
            switch (c.GetProperty("add_in").GetString())
            {
                case "no_result":
                    Assert.Null(result);
                    break;
                case "violation":
                    Assert.NotNull(result);
                    Assert.NotEmpty(TurnResultInvariants.Violations(result!));
                    break;
                case "ignored":
                    Assert.NotNull(result);
                    Assert.Empty(TurnResultInvariants.Violations(result!));
                    break;
                default:
                    throw new InvalidDataException(name + ": add_in must be no_result, violation or ignored");
            }
        }

        [Theory]
        [MemberData(nameof(ToleranceNames))]
        public void ReaderTolerance_KeepsTheTurnAndReadsWhatItUnderstands(string name)
        {
            var c = Case("reader_tolerance", name);
            var end = ReadEnd(c.GetProperty("data"));
            Assert.Equal(TurnEndStatus.Done, end.Status);
            Assert.Equal("ses_4c1d9e7a", end.SessionId);
            var result = end.ReadResult();
            var expect = c.GetProperty("expect");
            if (expect.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(result);
                return;
            }
            Assert.NotNull(result);
            Assert.Equal(expect.GetProperty("reply").GetString(), result!.Reply);
            Assert.Equal(expect.GetProperty("kind").GetString(), result.Kind);
            Assert.Equal(expect.GetProperty("values").GetInt32(), result.Values?.Count ?? 0);
            Assert.Equal(expect.GetProperty("checks").GetInt32(), result.Checks?.Count ?? 0);
            var written = expect.GetProperty("written");
            Assert.Equal(written.ValueKind == JsonValueKind.Null ? (int?)null : written.GetInt32(), result.Counts?.Written);
        }

        [Fact]
        public void ReaderTolerance_UnknownEnumStringsAreKeptForThePaneToDecide()
        {
            var result = ReadEnd(Case("reader_tolerance", "unknown_enum_values").GetProperty("data")).ReadResult()!;
            Assert.Equal("report", result.Kind);
            Assert.Equal("web", result.Values!.Single().Source!.Kind);
            Assert.NotEmpty(TurnResultInvariants.Violations(result));
        }

        [Fact]
        public void TryRead_AbsentNullAndNonObjectGiveNoResult()
        {
            Assert.Null(TurnResultReader.TryRead(null));
            Assert.Null(TurnResultReader.TryRead(JsonDocument.Parse("null").RootElement));
            Assert.Null(TurnResultReader.TryRead(JsonDocument.Parse("\"Done.\"").RootElement));
            Assert.Null(TurnResultReader.TryRead(JsonDocument.Parse("{}").RootElement));
            Assert.Null(TurnResultReader.TryRead(JsonDocument.Parse("{\"reply\":\"  \",\"kind\":\"changes\"}").RootElement));
        }

        [Fact]
        public void TryRead_DropsNullItemsInLists()
        {
            var json = "{\"reply\":\"Done.\",\"kind\":\"changes\",\"values\":[null,{\"param\":\"URL\",\"target\":{\"scope\":\"type\"}}],"
                     + "\"checks\":[null,{\"severity\":\"info\",\"text\":\"x\",\"actions\":[null]}],\"not_searched\":[null,\"Link A\"]}";
            var result = TurnResultReader.TryRead(JsonDocument.Parse(json).RootElement)!;
            Assert.Single(result.Values!);
            Assert.Single(result.Checks!);
            Assert.Empty(result.Checks![0].Actions!);
            Assert.Equal(new[] { "Link A" }, result.NotSearched);
        }

        [Fact]
        public void TurnEnd_WithoutResultIsTheOldShape()
        {
            var end = CopilotJson.Deserialize<TurnEndDto>("{\"seq\":2,\"status\":\"done\",\"session_id\":\"s\",\"receipt_ids\":[]}")!;
            Assert.Null(end.Result);
            Assert.Null(end.ReadResult());
            Assert.DoesNotContain("result", CopilotJson.Serialize(end));
        }

        [Fact]
        public void ProtocolSchema_EnumsAndLimitsMatchTheConstants()
        {
            var defs = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), "protocol3.schema.json"))).RootElement.GetProperty("$defs");
            string[] EnumOf(JsonElement node) => node.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).ToArray();
            Assert.Equal(TurnResultKinds.All, EnumOf(defs.GetProperty("turn_result").GetProperty("properties").GetProperty("kind")));
            Assert.Equal(ResultSourceKinds.All, EnumOf(defs.GetProperty("result_source").GetProperty("properties").GetProperty("kind")));
            Assert.Equal(ResultTargetScopes.All, EnumOf(defs.GetProperty("result_target").GetProperty("properties").GetProperty("scope")));
            Assert.Equal(ResultActionKinds.All, EnumOf(defs.GetProperty("result_action").GetProperty("properties").GetProperty("kind")));
            Assert.Equal(RuleSeverity.All, EnumOf(defs.GetProperty("result_check").GetProperty("properties").GetProperty("severity")));

            var result = defs.GetProperty("turn_result").GetProperty("properties");
            Assert.Equal(TurnResultLimits.ReplyMaxChars, result.GetProperty("reply").GetProperty("maxLength").GetInt32());
            Assert.Equal(TurnResultLimits.MaxValues, result.GetProperty("values").GetProperty("maxItems").GetInt32());
            Assert.Equal(TurnResultLimits.MaxChecks, result.GetProperty("checks").GetProperty("maxItems").GetInt32());
            Assert.Equal(TurnResultLimits.QuoteMaxChars, defs.GetProperty("result_source").GetProperty("properties").GetProperty("quote").GetProperty("maxLength").GetInt32());
            Assert.Equal(TurnResultLimits.ActionLabelMaxChars, defs.GetProperty("result_action").GetProperty("properties").GetProperty("label").GetProperty("maxLength").GetInt32());
            Assert.Equal(TurnResultLimits.MaxActions, defs.GetProperty("result_check").GetProperty("properties").GetProperty("actions").GetProperty("maxItems").GetInt32());

            // result is optional on turn_end.
            var turnEnd = defs.GetProperty("event_turn_end");
            Assert.True(turnEnd.GetProperty("properties").TryGetProperty("result", out _));
            Assert.DoesNotContain("result", turnEnd.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        }

        [Fact]
        public void ProtocolSchema_EveryKeyHasATypedProperty()
        {
            // A key added to the schema without a C# property would be dropped on a round trip.
            var defs = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), "protocol3.schema.json"))).RootElement.GetProperty("$defs");
            string[] Keys(string def) => defs.GetProperty(def).GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();
            string[] JsonNames(Type t) => t.GetProperties()
                .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name)
                .Where(n => n != null).Select(n => n!).OrderBy(x => x).ToArray();
            Assert.Equal(Keys("turn_result"), JsonNames(typeof(TurnResultDto)));
            Assert.Equal(Keys("result_counts"), JsonNames(typeof(ResultCountsDto)));
            Assert.Equal(Keys("result_value"), JsonNames(typeof(ResultValueDto)));
            Assert.Equal(Keys("result_target"), JsonNames(typeof(ResultTargetDto)));
            Assert.Equal(Keys("result_source"), JsonNames(typeof(ResultSourceDto)));
            Assert.Equal(Keys("result_check"), JsonNames(typeof(ResultCheckDto)));
            Assert.Equal(Keys("result_action"), JsonNames(typeof(ResultActionDto)));
        }
    }
}
