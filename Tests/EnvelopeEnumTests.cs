#nullable enable
// AI-298: the typed views of the envelope's enum-like strings, the typed
// per-tool readers, and the typed read failures. The wire strings are pinned
// against contracts/copilot/envelope.schema.json by CopilotContractTests; here
// the enums are pinned against those same constants, so a wire value cannot be
// added in one place only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using BinaVibe.Mcp;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class EnvelopeEnumTests
    {
        // ------------------------------------------------------------------
        // Enums match the wire constants (which CopilotContractTests pins to the schema)
        // ------------------------------------------------------------------

        [Fact]
        public void Status_EveryWireValueParses_AndRoundTrips_AndTheEnumHasNoExtraMember()
        {
            foreach (var wire in EnvelopeStatus.All)
            {
                var kind = WireEnums.ParseStatus(wire);
                Assert.NotEqual(EnvelopeStatusKind.Unknown, kind);
                Assert.Equal(wire, WireEnums.ToWire(kind));
            }
            Assert.Equal(EnvelopeStatus.All.Count, Enum.GetValues(typeof(EnvelopeStatusKind)).Length - 1);   // minus Unknown
        }

        [Fact]
        public void Phase_EveryWireValueParses_AndRoundTrips_AndTheEnumHasNoExtraMember()
        {
            foreach (var wire in EnvelopePhase.All)
            {
                var kind = WireEnums.ParsePhase(wire);
                Assert.NotEqual(EnvelopePhaseKind.Unknown, kind);
                Assert.Equal(wire, WireEnums.ToWire(kind));
            }
            Assert.Equal(EnvelopePhase.All.Count, Enum.GetValues(typeof(EnvelopePhaseKind)).Length - 1);
        }

        [Fact]
        public void ErrorCode_EveryWireValueParses_AndRoundTrips_AndTheEnumHasNoExtraMember()
        {
            foreach (var wire in ErrorCodes.All)
            {
                var kind = WireEnums.ParseError(wire);
                Assert.NotEqual(ErrorCodeKind.Unknown, kind);
                Assert.Equal(wire, WireEnums.ToWire(kind));
            }
            Assert.Equal(ErrorCodes.All.Count, Enum.GetValues(typeof(ErrorCodeKind)).Length - 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("DONE")]            // the wire is lower-case snake_case
        [InlineData("a_new_status")]    // a value the add-in does not know yet: Unknown, never a throw
        public void UnknownWireValues_AreUnknown(string? wire)
        {
            Assert.Equal(EnvelopeStatusKind.Unknown, WireEnums.ParseStatus(wire));
            Assert.Equal(EnvelopePhaseKind.Unknown, WireEnums.ParsePhase(wire));
            Assert.Equal(ErrorCodeKind.Unknown, WireEnums.ParseError(wire));
            Assert.Equal("", WireEnums.ToWire(EnvelopeStatusKind.Unknown));
        }

        [Fact]
        public void EnvelopeAccessors_ReadTheDtoStrings_WithoutChangingTheWire()
        {
            var env = new Envelope { Ok = false, Status = "failed", Phase = "commit", Summary = "x", Error = new ErrorDto { Code = "doc_changed", Message = "m" } };

            Assert.Equal(EnvelopeStatusKind.Failed, env.StatusKind());
            Assert.Equal(EnvelopePhaseKind.Commit, env.PhaseKind());
            Assert.Equal(ErrorCodeKind.DocChanged, env.ErrorKind());
            Assert.Equal(ErrorCodeKind.DocChanged, env.Error.CodeKind());
            Assert.Equal(ErrorCodeKind.Unknown, new Envelope().ErrorKind());                 // no error
            Assert.Equal(ErrorCodeKind.Unknown, ((ErrorDto?)null).CodeKind());
            // The wire is untouched: same JSON as before the enums existed.
            using var doc = JsonDocument.Parse(CopilotJson.Serialize(env));
            Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("commit", doc.RootElement.GetProperty("phase").GetString());
            Assert.Equal("doc_changed", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.False(doc.RootElement.TryGetProperty("status_kind", out _));
        }

        [Fact]
        public void CodeMode_ParsesReadAndWrite_CaseInsensitively()
        {
            Assert.Equal(CodeMode.Write, WireEnums.ParseMode("write"));
            Assert.Equal(CodeMode.Read, WireEnums.ParseMode("READ"));
            Assert.Equal(CodeMode.Unknown, WireEnums.ParseMode(null));
            Assert.Equal(CodeMode.Unknown, WireEnums.ParseMode("dry_run"));
        }

        // ------------------------------------------------------------------
        // Typed per-tool results
        // ------------------------------------------------------------------

        private static Envelope WithData(string json) => new Envelope { Ok = true, Status = "done", Phase = "read", Summary = "s", Data = JsonDocument.Parse(json).RootElement.Clone() };

        private static string ContractsExample(string file, [CallerFilePath] string thisFile = "")
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "contracts", "copilot", "examples", file);
            return File.Exists(copied) ? copied : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "contracts", "copilot", "examples", file));
        }

        [Fact]
        public void Rows_FromTheRecordedFindEnvelope_HaveRowsAndTheirElementIds()
        {
            var env = CopilotJson.Deserialize<Envelope>(File.ReadAllText(ContractsExample("envelope-read-find.json")))!;

            var r = ToolResults.Rows(env)!;

            Assert.Equal(3, r.Rows.Count);
            Assert.Equal(new long[] { 312045, 312048, 312051 }, r.RowElementIds().ToArray());
            Assert.False(r.IsTabular);
            Assert.Same(r.Rows, r.Items);
        }

        [Fact]
        public void Rows_EmptyRows_FallBackToGroups_AndAScheduleShapeIsTabular()
        {
            var grouped = ToolResults.Rows(WithData("{\"rows\":[],\"groups\":[{\"key\":\"Doors\",\"count\":3}]}"))!;
            Assert.Single(grouped.Items);
            Assert.Equal("Doors", grouped.Items[0].GetProperty("key").GetString());

            var schedule = ToolResults.Rows(WithData("{\"columns\":[\"Mark\",\"Width\"],\"rows\":[[\"D1\",900],[\"D2\",1000]]}"))!;
            Assert.True(schedule.IsTabular);
            Assert.Equal(2, schedule.Columns!.Count);
        }

        [Fact]
        public void Rows_AreTolerant_ElementIdsSkipBadRows_NoDataIsNull()
        {
            var r = ToolResults.Rows(WithData("{\"rows\":[{\"id\":7},{\"ref\":{\"id\":8,\"link\":3}},{\"ref\":\"x\"},5,{\"name\":\"no id\"},{\"id\":\"9\"}]}"))!;

            Assert.Equal(new long[] { 7, 8 }, r.RowElementIds().ToArray());
            Assert.Null(ToolResults.Rows(new Envelope()));
            Assert.Null(ToolResults.Rows(WithData("[1,2]")));                              // data is an array: not rows
            Assert.Empty(ToolResults.Rows(WithData("{\"rows\":\"oops\"}"))!.Rows);         // wrong type reads as absent
        }

        [Fact]
        public void Write_TypeEditsCreatedItemsAndCategory()
        {
            var w = ToolResults.Write(WithData(
                "{\"type_param\":true,\"types\":[{\"id\":5001,\"name\":\"PTr680a\",\"family\":\"Panel\",\"instances_in_model\":8},{\"name\":\"No id\"},7],"
                + "\"instances_affected\":38,\"instances_category\":\"Doors\",\"created\":[{\"name\":\"Wall 1\",\"category\":\"Walls\"},{\"name\":5},\"x\"]}"))!;

            Assert.True(w.TypeParam);
            Assert.Equal(2, w.Types.Count);
            Assert.Equal(5001, w.Types[0].Id);
            Assert.Equal("PTr680a", w.Types[0].Name);
            Assert.Equal("Panel", w.Types[0].Family);
            Assert.Equal(8, w.Types[0].InstancesInModel);
            Assert.Null(w.Types[1].Id);
            Assert.Null(w.Types[1].InstancesInModel);
            Assert.Equal(38, w.InstancesAffected);
            Assert.Equal("Doors", w.InstancesCategory);
            Assert.Equal(2, w.Created.Count);
            Assert.Equal("Wall 1", w.Created[0].Name);
            Assert.Null(w.Created[1].Name);                                              // a number is not a name
        }

        [Fact]
        public void Write_TypeParamNeedsTheLiteralTrue_NoDataIsNull()
        {
            Assert.False(ToolResults.Write(WithData("{\"type_param\":\"true\"}"))!.TypeParam);
            Assert.False(ToolResults.Write(WithData("{}"))!.TypeParam);
            Assert.Null(ToolResults.Write(new Envelope()));
        }

        [Fact]
        public void Mode_OfACodeEnvelope()
        {
            Assert.Equal(CodeMode.Write, ToolResults.Mode(WithData("{\"mode\":\"write\"}")));
            Assert.Equal(CodeMode.Read, ToolResults.Mode(WithData("{\"mode\":\"read\"}")));
            Assert.Equal(CodeMode.Unknown, ToolResults.Mode(WithData("{\"mode\":3}")));
            Assert.Equal(CodeMode.Unknown, ToolResults.Mode(new Envelope()));
        }

        [Fact]
        public void Check_NeedsARulesArray()
        {
            var ok = ToolResults.Check(WithData("{\"rule_set_id\":\"jkr\",\"version\":\"1\",\"rules\":[{\"rule_id\":\"r1\",\"pass\":2,\"fail\":1,\"not_verified\":0}]}"))!;

            Assert.Equal("jkr", ok.RuleSetId);
            Assert.Single(ok.Rules);
            Assert.Null(ToolResults.Check(WithData("{\"rows\":[]}")));
            Assert.Null(ToolResults.Check(WithData("{\"rules\":\"x\"}")));
            Assert.Null(ToolResults.Check(WithData("{\"rules\":[{\"rule_id\":5}]}")));    // does not decode: null, never a throw
        }

        // ------------------------------------------------------------------
        // Typed read failures (was: the message's first words)
        // ------------------------------------------------------------------

        [Fact]
        public void ReadFailures_FollowTheKind_NotTheMessage()
        {
            // The same words under every kind: only the kind decides.
            var text = "abandoned: not initialised";

            Assert.Equal(ErrorCodes.NotAllowed, ReadFailures.For(JobFailure.UpdateBlocked, text, true).Error!.Code);
            Assert.Equal(ErrorCodes.Timeout, ReadFailures.For(JobFailure.Abandoned, "anything", true).Error!.Code);
            Assert.Equal("The turn was cancelled before this call ran.", ReadFailures.For(JobFailure.Abandoned, "anything", true).Error!.Message);
            var pump = ReadFailures.For(JobFailure.PumpNotRunning, "no pump", false);
            Assert.Equal(ErrorCodes.RuntimeError, pump.Error!.Code);
            Assert.Contains("tool pump is not running (no pump)", pump.Error.Message);
            // A crash with a message that merely LOOKS like another kind is still a crash.
            var crash = ReadFailures.For(JobFailure.Crashed, text, true);
            Assert.Equal(ErrorCodes.RuntimeError, crash.Error!.Code);
            Assert.Contains("The read failed in the add-in (abandoned: not initialised). Nothing was changed.", crash.Error.Message);
            Assert.Equal(ErrorCodes.RevitBusy, ReadFailures.For(JobFailure.Crashed, "Revit is busy", false).Error!.Code);   // never reached Revit's thread
            Assert.Contains("returned no result", ReadFailures.For(JobFailure.Crashed, null, false).Error!.Message);
        }

        [Fact]
        public void McpJob_DefaultsToCrashed()
        {
            Assert.Equal(JobFailure.Crashed, default(JobFailure));
        }

        // ------------------------------------------------------------------
        // Guard: no error is classified by message text in the pane or the failure paths
        // ------------------------------------------------------------------

        private static string RepoRoot([CallerFilePath] string thisFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static IEnumerable<string> Sources(params string[] relative)
        {
            foreach (var rel in relative)
            {
                var path = Path.Combine(RepoRoot(), rel);
                if (Directory.Exists(path)) foreach (var f in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)) yield return f;
                else if (File.Exists(path)) yield return path;
            }
        }

        [Fact]
        public void NoMessageTextClassification_InThePaneOrTheFailurePaths()
        {
            // A message / reason / error text tested with StartsWith / IndexOf / Contains / EndsWith.
            var text = new Regex(@"\b(?:[Mm]essage|[Rr]eason|[Ww]hy|\.Error)\b[^;\n]{0,60}\.(?:StartsWith|IndexOf|Contains|EndsWith)\(", RegexOptions.Compiled);
            var offenders = new List<string>();
            foreach (var file in Sources("UI/Copilot", "BinaVibe/Copilot/Read/ReadExecutor.cs", "BinaVibe/Copilot/Read/ReadFailures.cs",
                                         "BinaVibe/Copilot/Writes/RevitWriteEngine.cs", "BinaVibe/Copilot/Undo", "BinaVibe/Mcp/McpJob.cs",
                                         "BinaVibe/Mcp/McpExternalEventHandler.cs", "BinaVibe/Mcp/McpJobPump.cs"))
            {
                var n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    var code = line.Split(new[] { "//" }, 2, StringSplitOptions.None)[0];
                    if (text.IsMatch(code)) offenders.Add(Path.GetFileName(file) + ":" + n + "  " + line.Trim());
                }
            }
            Assert.True(offenders.Count == 0, "Classify by a typed value, not by message text:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void ThePaneReadsToolResultsThroughTypes_NotByDigging_IntoEnvelopeData()
        {
            // TryGetProperty on an envelope's data belongs in ToolResults only. The pane (UI/Copilot) reads
            // RowsResult / WriteResult / CodeMode / CheckResult.
            var dig = new Regex(@"\.Data\b[^;\n]*TryGetProperty|Data is (?:not )?(?:System\.Text\.Json\.)?JsonElement", RegexOptions.Compiled);
            var offenders = new List<string>();
            foreach (var file in Sources("UI/Copilot"))
            {
                var n = 0;
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    if (dig.IsMatch(line.Split(new[] { "//" }, 2, StringSplitOptions.None)[0])) offenders.Add(Path.GetFileName(file) + ":" + n + "  " + line.Trim());
                }
            }
            Assert.True(offenders.Count == 0, "Read tool results through ToolResults:\n" + string.Join("\n", offenders));
        }
    }
}
