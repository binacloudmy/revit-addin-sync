#nullable enable
// W0 contract tests (lean-harness plan §2, Task 0.2). Every example in
// contracts/copilot/examples must round-trip through the add-in's
// System.Text.Json contract types without losing or inventing a key, must
// satisfy the §2.1 envelope invariants, and tools.json must hold exactly the
// 9 model-facing tools with descriptions of at most 600 characters. The
// CopilotToolHost tests pin which calls may reach the read executor and the
// write runner.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.
// The contract files are copied next to the test assembly (Tests.csproj W0
// block); the fallbacks walk up from the assembly and from this source file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotContractTests
    {
        // ------------------------------------------------------------------
        // Locating contracts/copilot
        // ------------------------------------------------------------------

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

            throw new DirectoryNotFoundException("contracts/copilot not found next to the test assembly, above it, or above " + thisFile);
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { ContractsDir() }.Concat(parts).ToArray()));

        private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static readonly string[] ExpectedEnvelopeExamples =
        {
            "envelope-commit-setparams.json",
            "envelope-failed-notfound.json",
            "envelope-look.json",
            "envelope-preview-setparams.json",
            "envelope-read-empty.json",
            "envelope-read-find.json",
        };

        public static IEnumerable<object[]> EnvelopeExamples() =>
            ExpectedEnvelopeExamples.Select(f => new object[] { f });

        private static Envelope LoadEnvelope(string file) =>
            CopilotJson.Deserialize<Envelope>(Read("examples", file))
            ?? throw new InvalidDataException(file + " deserialized to null");

        // ------------------------------------------------------------------
        // JSON shape helpers
        // ------------------------------------------------------------------

        /// <summary>Every object key as a path ("changes.touched.modified[].before"),
        /// so a POCO that drops or invents a nested key fails the comparison.</summary>
        private static string[] KeyPaths(string json)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            Walk(Parse(json), "$", paths);
            return paths.ToArray();
        }

        private static void Walk(JsonElement e, string path, SortedSet<string> paths)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        var child = path + "." + p.Name;
                        paths.Add(child);
                        Walk(p.Value, child, paths);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item, path + "[]", paths);
                    break;
            }
        }

        private static void AssertRoundTrip<T>(string original) where T : class
        {
            var model = CopilotJson.Deserialize<T>(original);
            Assert.NotNull(model);
            var again = CopilotJson.Serialize(model!);
            Assert.Equal(KeyPaths(original), KeyPaths(again));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(again)),
                typeof(T).Name + " changed a value on the round trip:\n" + again);
        }

        private static int CodePoints(string s) => s.Count(c => !char.IsLowSurrogate(c));

        private static string[] EnumOf(JsonElement schemaNode) =>
            schemaNode.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).ToArray();

        // ------------------------------------------------------------------
        // Envelope (plan §2.1)
        // ------------------------------------------------------------------

        [Fact]
        public void Envelope_ExampleSetIsComplete()
        {
            var onDisk = Directory.GetFiles(Path.Combine(ContractsDir(), "examples"), "envelope-*.json")
                .Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(ExpectedEnvelopeExamples, onDisk);
        }

        [Theory]
        [MemberData(nameof(EnvelopeExamples))]
        public void Envelope_RoundTripsEveryExample(string file)
        {
            AssertRoundTrip<Envelope>(Read("examples", file));
        }

        [Theory]
        [MemberData(nameof(EnvelopeExamples))]
        public void Envelope_ExamplesSatisfyInvariants(string file)
        {
            Assert.Empty(EnvelopeInvariants.Violations(LoadEnvelope(file)));
        }

        [Fact]
        public void Envelope_ExamplesExerciseEveryInvariantBranch()
        {
            var all = ExpectedEnvelopeExamples.Select(LoadEnvelope).ToList();
            Assert.Contains(all, e => !e.Ok && e.Error != null);
            Assert.Contains(all, e => e.Error?.Code == ErrorCodes.NotFound && e.Error.Candidates is { Count: > 0 });
            Assert.Contains(all, e => e.Phase == EnvelopePhase.Commit && e.Ok && e.Verified is { Method: VerifyMethods.ParamReadback });
            Assert.Contains(all, e => e.Phase == EnvelopePhase.Read && e.Count == 0 && e.Searched != null);
            Assert.Contains(all, e => e.Phase == EnvelopePhase.Preview && e.PlanHash != null);
            Assert.Contains(all, e => e.Image != null);
            // The preview and the commit of the same batch carry the same plan hash.
            Assert.Equal(LoadEnvelope("envelope-preview-setparams.json").PlanHash, LoadEnvelope("envelope-commit-setparams.json").PlanHash);
        }

        [Fact]
        public void Envelope_LookExampleImageMatchesItsPngHeader()
        {
            var image = LoadEnvelope("envelope-look.json").Image!;
            Assert.Equal("image/png", image.MediaType);
            var png = Convert.FromBase64String(image.Base64);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
            int BigEndian(int at) => (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            Assert.Equal(image.Width, BigEndian(16));
            Assert.Equal(image.Height, BigEndian(20));
        }

        public static IEnumerable<object[]> BrokenEnvelopes()
        {
            const string ok = "\"ok\":true,\"status\":\"done\"";
            yield return new object[] { "ok_false_without_error", "{\"ok\":false,\"status\":\"failed\",\"phase\":\"read\",\"summary\":\"s\"}" };
            yield return new object[] { "not_found_without_candidates", "{\"ok\":false,\"status\":\"failed\",\"phase\":\"read\",\"summary\":\"s\",\"error\":{\"code\":\"not_found\",\"message\":\"m\"}}" };
            yield return new object[] { "commit_ok_without_verified", "{" + ok + ",\"phase\":\"commit\",\"summary\":\"s\"}" };
            yield return new object[] { "commit_ok_verified_none", "{" + ok + ",\"phase\":\"commit\",\"summary\":\"s\",\"verified\":{\"method\":\"none\",\"checked\":0,\"mismatches\":[]}}" };
            yield return new object[] { "read_count_zero_without_searched", "{" + ok + ",\"phase\":\"read\",\"summary\":\"s\",\"count\":0}" };
            yield return new object[] { "unknown_status", "{\"ok\":true,\"status\":\"success\",\"phase\":\"read\",\"summary\":\"s\"}" };
            yield return new object[] { "unknown_phase", "{" + ok + ",\"phase\":\"apply\",\"summary\":\"s\"}" };
            yield return new object[] { "unknown_error_code", "{\"ok\":false,\"status\":\"failed\",\"phase\":\"read\",\"summary\":\"s\",\"error\":{\"code\":\"boom\",\"message\":\"m\"}}" };
            yield return new object[] { "empty_summary", "{" + ok + ",\"phase\":\"read\",\"summary\":\"\"}" };
            yield return new object[] { "summary_over_300", "{" + ok + ",\"phase\":\"read\",\"summary\":\"" + new string('x', 301) + "\"}" };
            yield return new object[] { "next_over_200", "{" + ok + ",\"phase\":\"read\",\"summary\":\"s\",\"next\":\"" + new string('x', 201) + "\"}" };
            yield return new object[] { "bad_plan_hash", "{" + ok + ",\"phase\":\"preview\",\"summary\":\"s\",\"plan_hash\":\"md5:abc\"}" };
            yield return new object[] { "negative_count", "{" + ok + ",\"phase\":\"read\",\"summary\":\"s\",\"count\":-1,\"searched\":{\"sources\":[],\"categories\":[],\"not_searched\":[]}}" };
            yield return new object[] { "data_is_a_string", "{" + ok + ",\"phase\":\"read\",\"summary\":\"s\",\"data\":\"rows\"}" };
            yield return new object[] { "unknown_verify_method", "{" + ok + ",\"phase\":\"commit\",\"summary\":\"s\",\"verified\":{\"method\":\"eyeball\",\"checked\":1,\"mismatches\":[]}}" };
            yield return new object[] { "unknown_warning_severity", "{" + ok + ",\"phase\":\"read\",\"summary\":\"s\",\"warnings\":[{\"text\":\"t\",\"severity\":\"info\",\"ids\":[]}]}" };
            yield return new object[] { "image_not_png", "{" + ok + ",\"phase\":\"read\",\"summary\":\"s\",\"image\":{\"media_type\":\"image/jpeg\",\"base64\":\"AA==\",\"width\":1,\"height\":1,\"caption\":\"c\"}}" };
            yield return new object[] { "image_over_1024", "{" + ok + ",\"phase\":\"read\",\"summary\":\"s\",\"image\":{\"media_type\":\"image/png\",\"base64\":\"AA==\",\"width\":2048,\"height\":10,\"caption\":\"c\"}}" };
        }

        [Theory]
        [MemberData(nameof(BrokenEnvelopes))]
        public void EnvelopeInvariants_FlagBrokenEnvelopes(string name, string json)
        {
            var envelope = CopilotJson.Deserialize<Envelope>(json)!;
            Assert.True(EnvelopeInvariants.Violations(envelope).Count > 0, name + " was accepted");
        }

        [Fact]
        public void EnvelopeInvariants_AcceptTheAllowedEdgeCases()
        {
            // A commit that changed nothing may skip read-back; a preview with
            // zero targets need not carry `searched` (only reads must).
            var noop = "{\"ok\":true,\"status\":\"noop\",\"phase\":\"commit\",\"summary\":\"Nothing to change.\"}";
            var noopNone = "{\"ok\":true,\"status\":\"noop\",\"phase\":\"commit\",\"summary\":\"s\",\"verified\":{\"method\":\"none\",\"checked\":0,\"mismatches\":[]}}";
            var previewZero = "{\"ok\":true,\"status\":\"done\",\"phase\":\"preview\",\"summary\":\"s\",\"count\":0}";
            var notFoundEmpty = "{\"ok\":false,\"status\":\"failed\",\"phase\":\"read\",\"summary\":\"s\",\"error\":{\"code\":\"not_found\",\"message\":\"m\",\"candidates\":[]}}";
            foreach (var json in new[] { noop, noopNone, previewZero, notFoundEmpty })
                Assert.Empty(EnvelopeInvariants.Violations(CopilotJson.Deserialize<Envelope>(json)!));
        }

        [Fact]
        public void EnvelopeSchema_EnumsMatchContractConstants()
        {
            var schema = Parse(Read("envelope.schema.json"));
            var props = schema.GetProperty("properties");
            var defs = schema.GetProperty("$defs");
            Assert.Equal(EnvelopeStatus.All, EnumOf(props.GetProperty("status")));
            Assert.Equal(EnvelopePhase.All, EnumOf(props.GetProperty("phase")));
            Assert.Equal(ErrorCodes.All, EnumOf(defs.GetProperty("error").GetProperty("properties").GetProperty("code")));
            Assert.Equal(VerifyMethods.All, EnumOf(defs.GetProperty("verified").GetProperty("properties").GetProperty("method")));
            Assert.Equal(WarningSeverity.All, EnumOf(defs.GetProperty("warning").GetProperty("properties").GetProperty("severity")));
            Assert.Equal(EnvelopeLimits.SummaryMaxChars, props.GetProperty("summary").GetProperty("maxLength").GetInt32());
            Assert.Equal(EnvelopeLimits.NextMaxChars, props.GetProperty("next").GetProperty("maxLength").GetInt32());
        }

        [Fact]
        public void EnvelopeFactory_BuildsValidFailureEnvelopes()
        {
            var notImplemented = EnvelopeFactory.NotImplemented(ToolNames.Look);
            var notAllowed = EnvelopeFactory.NotAllowed("delete_everything");
            var notFound = EnvelopeFactory.Failed(ErrorCodes.NotFound, "No ceiling type 'CL-02'.");
            var longMessage = EnvelopeFactory.Failed(ErrorCodes.RuntimeError, new string('x', 900), new[] { "a" });

            Assert.Equal(ErrorCodes.NotImplemented, notImplemented.Error!.Code);
            Assert.Equal(ErrorCodes.NotAllowed, notAllowed.Error!.Code);
            Assert.NotNull(notFound.Error!.Candidates);               // present even when nothing similar exists
            foreach (var e in new[] { notImplemented, notAllowed, notFound, longMessage })
            {
                Assert.False(e.Ok);
                Assert.Equal(EnvelopeStatus.Failed, e.Status);
                Assert.Empty(EnvelopeInvariants.Violations(e));
            }
            Assert.Throws<ArgumentException>(() => EnvelopeFactory.Failed("boom", "m"));
        }

        // ------------------------------------------------------------------
        // tools.json (plan §2.3)
        // ------------------------------------------------------------------

        [Fact]
        public void ToolsJson_HasExactlyNineTools_DescriptionsUnder600()
        {
            var tools = Parse(Read("tools.json")).EnumerateArray().ToList();

            Assert.Equal(9, tools.Count);
            Assert.Equal(ToolNames.All, tools.Select(t => t.GetProperty("name").GetString()!).ToArray());
            foreach (var tool in tools)
            {
                var name = tool.GetProperty("name").GetString()!;
                Assert.Equal(new[] { "name", "kind", "description", "parameters" }, tool.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.Equal(ToolNames.KindOf(name), tool.GetProperty("kind").GetString());

                var description = tool.GetProperty("description").GetString()!;
                Assert.InRange(CodePoints(description), 1, ToolNames.DescriptionMaxChars);
            }
        }

        [Fact]
        public void ToolsJson_ParametersAreProviderSafeObjectSchemas()
        {
            // The loop sends `parameters` verbatim to an OpenAI-compatible API:
            // keep the top level a plain closed object with no combinators at
            // all (ruling R16). Rules that need one, such as inspect's "target
            // or schedule", are stated in property descriptions and enforced by
            // the tool's argument parsing (invalid_args).
            foreach (var tool in Parse(Read("tools.json")).EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString();
                var p = tool.GetProperty("parameters");
                Assert.Equal("object", p.GetProperty("type").GetString());
                Assert.False(p.GetProperty("additionalProperties").GetBoolean(), name + " must close its top level");
                var properties = p.GetProperty("properties").EnumerateObject().Select(x => x.Name).ToHashSet();
                Assert.NotEmpty(properties);
                if (p.TryGetProperty("required", out var required))
                    foreach (var r in required.EnumerateArray()) Assert.Contains(r.GetString()!, properties);
                foreach (var banned in new[] { "anyOf", "oneOf", "allOf", "not", "enum", "$ref", "if", "then", "else" })
                    Assert.False(p.TryGetProperty(banned, out _), name + " has top-level " + banned);
            }
        }

        private static JsonElement ToolParameters(string tool) =>
            Parse(Read("tools.json")).EnumerateArray().Single(t => t.GetProperty("name").GetString() == tool).GetProperty("parameters");

        [Fact]
        public void ToolsJson_InspectTargetAndScheduleAreOptionalAndDocumented()
        {
            // Neither is required by the schema; both say one of them is, and the
            // add-in (W2) answers invalid_args when neither is given.
            var p = ToolParameters(ToolNames.Inspect);
            Assert.False(p.TryGetProperty("required", out _), "inspect must not require target or schedule in the schema");
            var props = p.GetProperty("properties");
            foreach (var name in new[] { "target", "schedule" })
                Assert.Contains("Give target or schedule (at least one).", props.GetProperty(name).GetProperty("description").GetString());
        }

        [Fact]
        public void ToolsJson_WriteFilterIsHostOnly()
        {
            var filter = ToolParameters(ToolNames.SetParameters).GetProperty("properties").GetProperty("target")
                .GetProperty("properties").GetProperty("filter");
            Assert.False(filter.GetProperty("properties").TryGetProperty("source", out _), "writes apply to the host model only");
            Assert.False(filter.GetProperty("additionalProperties").GetBoolean());
            var query = ToolParameters(ToolNames.FindElements).GetProperty("properties").GetProperty("queries").GetProperty("items");
            Assert.True(query.GetProperty("properties").TryGetProperty("source", out _), "find_elements still searches links");
        }

        [Fact]
        public void AskFields_ChoiceTypeRequiresChoices()
        {
            // The same rule in tools.json (ask_user.fields[]) and protocol3 (the ask event).
            var toolField = ToolParameters(ToolNames.AskUser).GetProperty("properties").GetProperty("fields").GetProperty("items");
            var eventField = Parse(Read("protocol3.schema.json")).GetProperty("$defs").GetProperty("ask_field");
            foreach (var field in new[] { toolField, eventField })
            {
                Assert.True(field.GetProperty("properties").TryGetProperty("choices", out var choices));
                Assert.Equal(2, choices.GetProperty("minItems").GetInt32());
                Assert.Equal(AskFieldTypes.Choice, field.GetProperty("if").GetProperty("properties").GetProperty("type").GetProperty("const").GetString());
                Assert.Equal(new[] { "choices" }, field.GetProperty("then").GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToArray());
                Assert.Equal(AskFieldTypes.All, EnumOf(field.GetProperty("properties").GetProperty("type")));
            }

            const string ask = "{\"seq\":3,\"request_id\":\"req_09\",\"question\":\"Pintu jenis apa?\",\"multi\":false,\"purpose\":\"clarify\","
                + "\"fields\":[{\"id\":\"door_type\",\"label\":\"Jenis pintu\",\"type\":\"choice\",\"choices\":[{\"id\":\"single\",\"label\":\"Single 900\"},{\"id\":\"double\",\"label\":\"Double 1800\"}]},"
                + "{\"id\":\"height\",\"label\":\"Tinggi\",\"type\":\"number\",\"unit\":\"mm\"}]}";
            AssertRoundTrip<AskDto>(ask);
            var dto = CopilotJson.Deserialize<AskDto>(ask)!;
            Assert.Equal(new[] { "single", "double" }, dto.Fields![0].Choices!.Select(c => c.Id));
            Assert.Null(dto.Fields[1].Choices);
        }

        // Golden SHA-256 (UTF-8) of each normative description (plan §2.3). A
        // change here must be deliberate: update the plan, tools.json and the
        // same table in bina-ai tests/assistant/test_contracts.py together.
        private static readonly (string Tool, string Sha256)[] PinnedDescriptions =
        {
            ("model_overview", "3c56c32151ab022ab019cbfa50f7b7f049be5ea737c3b393fa965c0fcdd07430"),
            ("find_elements", "1a078913f86324a79aea63698eb693b181c7762c33732581993c92b419baabbd"),
            ("inspect", "89cd03a433bd1ae6e15440ea4728218cde4f7eca1a2eb83a868feac7fc862960"),
            ("check", "54a5412e3577f452d889358bb038f4b11d460c05bf83c72c2214883eb70619cc"),
            ("set_parameters", "d0028e4c69d37193fa0f6e785a92031925e4b3832d51c9ab99a93bf7d782090d"),
            ("look", "f45ba7a0b671a17e1f2d3f80257405e4af52208d5bb50dfea1ce74b2fb2eb701"),
            ("run_revit_code", "404d4a19650a2632e5e4a87e85e395c15e5d9b616c10d44c28caaa29bbbc40f0"),
            ("lookup", "a6f953c7394d76c2fa829142cea7580a85174c7449bab0b1b2bad317ba717745"),
            ("ask_user", "b7ce60f47d5c60f4255124ddae1932336f20ece2d1315b0e3b6e081d7563570d"),
        };

        [Fact]
        public void ToolsJson_DescriptionsMatchPinnedHashes()
        {
            var actual = Parse(Read("tools.json")).EnumerateArray().Select(t =>
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(t.GetProperty("description").GetString()!);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hex = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
                return (t.GetProperty("name").GetString()!, hex);
            }).ToArray();
            Assert.Equal(PinnedDescriptions, actual);
        }

        [Fact]
        public void ToolNames_AddInToolsAreTheSevenNonCloudTools()
        {
            Assert.Equal(7, ToolNames.AddIn.Count);
            Assert.Equal(ToolNames.All.Where(n => ToolNames.KindOf(n) is "read" or "write" or "code"), ToolNames.AddIn);
            Assert.Equal("server", ToolNames.KindOf(ToolNames.Lookup));
            Assert.Equal("pane", ToolNames.KindOf(ToolNames.AskUser));
            Assert.Null(ToolNames.KindOf("call_tool"));
        }

        // ------------------------------------------------------------------
        // Protocol 3 frames (plan §2.2)
        // ------------------------------------------------------------------

        // frames.json holds two turns: "frames" (complete) and "failure_frames"
        // (refused before any tool ran, then a 426). seq restarts per turn.
        private static IEnumerable<List<JsonElement>> FrameTurns()
        {
            var root = Parse(Read("examples", "frames.json"));
            yield return root.GetProperty("frames").EnumerateArray().ToList();
            yield return root.GetProperty("failure_frames").EnumerateArray().ToList();
        }

        [Fact]
        public void Frames_RoundTripEveryFrame()
        {
            var seen = new HashSet<string>();
            foreach (var turn in FrameTurns())
                RoundTripTurn(turn, seen);
            Assert.Equal(SseEventTypes.All.OrderBy(x => x, StringComparer.Ordinal), seen.OrderBy(x => x, StringComparer.Ordinal));
        }

        private static void RoundTripTurn(List<JsonElement> frames, HashSet<string> seen)
        {
            long lastSeq = 0;
            foreach (var frame in frames)
            {
                if (frame.TryGetProperty("event", out var ev))
                {
                    var type = ev.GetString()!;
                    seen.Add(type);
                    var data = frame.GetProperty("data").GetRawText();
                    switch (type)
                    {
                        case SseEventTypes.Progress: AssertRoundTrip<ProgressDto>(data); break;
                        case SseEventTypes.ReplyDelta: AssertRoundTrip<ReplyDeltaDto>(data); break;
                        case SseEventTypes.ToolRequest: AssertRoundTrip<ToolRequestDto>(data); break;
                        case SseEventTypes.Ask: AssertRoundTrip<AskDto>(data); break;
                        case SseEventTypes.TurnEnd: AssertRoundTrip<TurnEndDto>(data); break;
                        case SseEventTypes.Error: AssertRoundTrip<StreamErrorDto>(data); break;
                        default: throw new InvalidDataException("unknown event " + type);
                    }
                    var seq = frame.GetProperty("data").GetProperty("seq").GetInt64();
                    Assert.True(seq > lastSeq, "seq must increase within a turn: " + seq + " after " + lastSeq);
                    lastSeq = seq;
                }
                else if (frame.TryGetProperty("request", out var request))
                {
                    var body = frame.GetProperty("body").GetRawText();
                    var route = request.GetString()!;
                    if (route == "POST /copilot/turns") AssertRoundTrip<TurnRequestDto>(body);
                    else if (route.StartsWith("POST /copilot/turns/", StringComparison.Ordinal) && route.EndsWith("/results", StringComparison.Ordinal)) AssertRoundTrip<ResultsRequestDto>(body);
                    else throw new InvalidDataException("unexpected request " + route);
                }
                else
                {
                    Assert.Equal(426, frame.GetProperty("response").GetInt32());
                    AssertRoundTrip<UpdateRequiredDto>(frame.GetProperty("body").GetRawText());
                }
            }
        }

        [Fact]
        public void Frames_ToolRequestArgsStayRawJson()
        {
            var request = FrameTurns().First()
                .First(f => f.TryGetProperty("event", out var e) && e.GetString() == SseEventTypes.ToolRequest);
            var dto = CopilotJson.Deserialize<ToolRequestDto>(request.GetProperty("data").GetRawText())!;
            var call = dto.Calls.Single(c => c.Tool == ToolNames.FindElements);
            Assert.Equal(JsonValueKind.Object, call.Args.ValueKind);
            Assert.Equal("Ceilings", call.Args.GetProperty("queries")[0].GetProperty("category").GetString());
            Assert.False(string.IsNullOrEmpty(call.IdempotencyKey));
        }

        [Fact]
        public void ProtocolSchema_EnumsMatchContractConstants()
        {
            var defs = Parse(Read("protocol3.schema.json")).GetProperty("$defs");
            Assert.Equal(SseEventTypes.All, EnumOf(defs.GetProperty("sse_frame").GetProperty("properties").GetProperty("event")));
            Assert.Equal(Protocol.Version, defs.GetProperty("turn_request").GetProperty("properties").GetProperty("protocol").GetProperty("const").GetInt32());
            Assert.Equal(ToolNames.AddIn, EnumOf(defs.GetProperty("tool_call").GetProperty("properties").GetProperty("tool")));
        }

        // ------------------------------------------------------------------
        // Snapshot (plan §2.4) and rule sets (plan §2.5)
        // ------------------------------------------------------------------

        [Fact]
        public void Snapshot_ExampleRoundTripsWithinCaps()
        {
            var json = Read("examples", "snapshot.json");
            AssertRoundTrip<SnapshotModel>(json);
            var snapshot = CopilotJson.Deserialize<SnapshotModel>(json)!;
            Assert.NotNull(snapshot.Doc);
            Assert.Contains(snapshot.Doc!.Kind, SnapshotDocKinds.All);
            Assert.Contains(snapshot.Doc.Mode, SnapshotDocModes.All);
            Assert.InRange(snapshot.Selection.Ids.Count, 0, SnapshotLimits.MaxSelectionIds);
            Assert.InRange(snapshot.Rooms?.Count ?? 0, 0, SnapshotLimits.MaxRooms);
        }

        [Fact]
        public void RuleSet_ExampleRoundTripsWithKnownKinds()
        {
            var json = Read("examples", "ruleset-model-health.json");
            AssertRoundTrip<RuleSet>(json);
            var rules = CopilotJson.Deserialize<RuleSet>(json)!;
            Assert.NotEmpty(rules.Rules);
            Assert.Equal(rules.Rules.Count, rules.Rules.Select(r => r.Id).Distinct().Count());
            foreach (var rule in rules.Rules)
            {
                Assert.Contains(rule.Assertion.Kind, AssertionKinds.All);
                Assert.Contains(rule.Severity, RuleSeverity.All);
            }
        }

        // ------------------------------------------------------------------
        // CopilotToolHost routing (Revit-free)
        // ------------------------------------------------------------------

        private sealed class FakeReads : IReadExecutor
        {
            public List<string> Seen { get; } = new();
            public Func<IReadOnlyList<ToolCallDto>, Envelope[]>? Answer { get; set; }

            public Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct)
            {
                Seen.AddRange(calls.Select(c => c.Tool));
                return Task.FromResult(Answer != null ? Answer(calls) : calls.Select(c => Ran(c.Tool)).ToArray());
            }
        }

        private sealed class FakeWrites : IWriteBatchRunner
        {
            public WriteBatchRequest? Got { get; private set; }
            public Exception? Throw { get; set; }

            public Task<Envelope[]> RunAsync(WriteBatchRequest req, CancellationToken ct)
            {
                Got = req;
                if (Throw != null) throw Throw;
                return Task.FromResult(req.Calls.Select(c => Ran(c.Tool)).ToArray());
            }
        }

        private static Envelope Ran(string tool) =>
            new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "ran " + tool };

        private static ToolCallDto[] Calls(params string[] tools) =>
            tools.Select((t, i) => new ToolCallDto { CallId = "call_" + i, IdempotencyKey = "key_" + i, Tool = t, Args = Parse("{}") }).ToArray();

        [Fact]
        public async Task Host_ReadsRunOnlyCataloguedReadAndCodeTools()
        {
            var reads = new FakeReads();
            var host = new CopilotToolHost { ReadExecutor = reads };

            var results = await host.RunReadsAsync("turn", Calls("find_elements", "set_parameters", "lookup", "call_tool", "run_revit_code", "inspect"), new SnapshotModel(), CancellationToken.None);

            Assert.Equal(new[] { "find_elements", "run_revit_code", "inspect" }, reads.Seen);
            Assert.Equal(new[] { "ran find_elements", "ran run_revit_code", "ran inspect" }, new[] { results[0], results[4], results[5] }.Select(e => e.Summary));
            foreach (var refused in new[] { results[1], results[2], results[3] })
                Assert.Equal(ErrorCodes.NotAllowed, refused.Error!.Code);
            Assert.All(results, e => Assert.Empty(EnvelopeInvariants.Violations(e)));
        }

        [Fact]
        public async Task Host_WritesRunOnlyWriteAndCodeTools()
        {
            var writes = new FakeWrites();
            var host = new CopilotToolHost { WriteRunner = writes };
            var snapshot = new SnapshotModel { ChangeSeq = 7 };

            var results = await host.RunWritesAsync(new WriteBatchRequest
            {
                TurnId = "turn", RequestId = "req_02", Description = "Naikkan siling", Snapshot = snapshot,
                Calls = Calls("set_parameters", "inspect", "run_revit_code"),
            }, CancellationToken.None);

            Assert.Equal(new[] { "set_parameters", "run_revit_code" }, writes.Got!.Calls.Select(c => c.Tool));
            Assert.Equal(("turn", "req_02", "Naikkan siling"), (writes.Got.TurnId, writes.Got.RequestId, writes.Got.Description));
            Assert.Same(snapshot, writes.Got.Snapshot);
            Assert.Equal(ErrorCodes.NotAllowed, results[1].Error!.Code);
            Assert.Equal(new[] { "ran set_parameters", "ran run_revit_code" }, new[] { results[0], results[2] }.Select(e => e.Summary));
        }

        [Fact]
        public async Task Host_UnwiredExecutorsAnswerNotImplemented()
        {
            var host = new CopilotToolHost();
            var reads = await host.RunReadsAsync("turn", Calls("look"), new SnapshotModel(), CancellationToken.None);
            var writes = await host.RunWritesAsync(new WriteBatchRequest { Calls = Calls("set_parameters") }, CancellationToken.None);
            Assert.Equal(ErrorCodes.NotImplemented, reads.Single().Error!.Code);
            Assert.Equal(ErrorCodes.NotImplemented, writes.Single().Error!.Code);
        }

        [Fact]
        public async Task Host_ExecutorFailuresBecomeFailedEnvelopes()
        {
            var crashingWrites = new CopilotToolHost { WriteRunner = new FakeWrites { Throw = new InvalidOperationException("boom") } };
            var crashed = (await crashingWrites.RunWritesAsync(new WriteBatchRequest { Calls = Calls("set_parameters") }, CancellationToken.None)).Single();
            Assert.Equal(ErrorCodes.RuntimeError, crashed.Error!.Code);
            Assert.Contains("may or may not have been applied", crashed.Error.Message);

            var short1 = new CopilotToolHost { ReadExecutor = new FakeReads { Answer = calls => new[] { Ran(calls[0].Tool) } } };
            var shortResults = await short1.RunReadsAsync("turn", Calls("find_elements", "inspect"), new SnapshotModel(), CancellationToken.None);
            Assert.Equal("ran find_elements", shortResults[0].Summary);
            Assert.Equal(ErrorCodes.RuntimeError, shortResults[1].Error!.Code);

            var cancelled = new CopilotToolHost { WriteRunner = new FakeWrites { Throw = new OperationCanceledException() } };
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.RunWritesAsync(new WriteBatchRequest { Calls = Calls("set_parameters") }, CancellationToken.None));
        }

        private sealed class StubbedReads : IReadExecutor
        {
            public Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct) =>
                throw new NotImplementedException("W2");
        }

        [Fact]
        public async Task Host_StubbedWriteRunnerIsNeverReportedAsCleanNotImplemented()
        {
            // A write runner that throws NotImplementedException may still have
            // committed part of a batch: it fails like any other runner crash.
            var stubbedWrites = new CopilotToolHost { WriteRunner = new FakeWrites { Throw = new NotImplementedException("W3") } };
            var write = (await stubbedWrites.RunWritesAsync(new WriteBatchRequest { Calls = Calls("set_parameters", "run_revit_code") }, CancellationToken.None));
            Assert.All(write, e =>
            {
                Assert.Equal(EnvelopeStatus.Failed, e.Status);
                Assert.Equal(ErrorCodes.RuntimeError, e.Error!.Code);
                Assert.Contains("may or may not have been applied", e.Error.Message);
                Assert.Empty(EnvelopeInvariants.Violations(e));
            });

            // A stubbed read tool changed nothing, so not_implemented stays honest.
            var stubbedReads = new CopilotToolHost { ReadExecutor = new StubbedReads() };
            var read = (await stubbedReads.RunReadsAsync("turn", Calls("look"), new SnapshotModel(), CancellationToken.None)).Single();
            Assert.Equal(ErrorCodes.NotImplemented, read.Error!.Code);
        }

        [Fact]
        public void RuleSetSchema_EnumsMatchContractConstants()
        {
            var defs = Parse(Read("ruleset.schema.json")).GetProperty("$defs");
            Assert.Equal(AssertionKinds.All, EnumOf(defs.GetProperty("assertion").GetProperty("properties").GetProperty("kind")));
            Assert.Equal(RuleSeverity.All, EnumOf(defs.GetProperty("rule").GetProperty("properties").GetProperty("severity")));

            var snapshotDoc = Parse(Read("snapshot.schema.json")).GetProperty("$defs").GetProperty("doc").GetProperty("properties");
            Assert.Equal(SnapshotDocKinds.All, EnumOf(snapshotDoc.GetProperty("kind")));
            Assert.Equal(SnapshotDocModes.All, EnumOf(snapshotDoc.GetProperty("mode")));
        }
    }
}
