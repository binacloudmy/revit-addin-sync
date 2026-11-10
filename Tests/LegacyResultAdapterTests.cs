// LegacyResultAdapter + ResultSemantics (W1 Task 3, plan A02 idea): the old
// BinaVibe.Mcp tools return bare dictionaries. A refusal without an exception
// ({"ok": false}, an "error" with no ok, status not_implemented) must reach the
// model as a failed envelope, never as done. Counts become count; the raw
// dictionary stays in data. Every adapted envelope passes EnvelopeInvariants.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LegacyResultAdapterTests
    {
        private static Dictionary<string, object?> D(params (string k, object? v)[] kv) =>
            kv.ToDictionary(p => p.k, p => p.v);

        [Fact]
        public void OkFalseBecomesFailed()
        {
            var legacy = D(("ok", false), ("error", "no plan view is active"), ("view", "3D"));
            var e = Valid(LegacyResultAdapter.ToEnvelope(legacy, null));
            Assert.False(e.Ok);
            Assert.Equal(EnvelopeStatus.Failed, e.Status);
            Assert.Equal(EnvelopePhase.Read, e.Phase);
            Assert.Equal(ErrorCodes.RuntimeError, e.Error!.Code);
            Assert.Equal("no plan view is active", e.Error.Message);
            Assert.Equal("3D", e.Data!.Value.GetProperty("view").GetString());
            Assert.False(e.Data.Value.GetProperty("ok").GetBoolean());
        }

        [Fact]
        public void NotImplementedBecomesFailedNotImplemented()
        {
            // ToolRegistry.NotImplemented's shape, before and after it gained ok:false.
            foreach (var legacy in new[]
            {
                D(("error", "tool frobnicate not implemented yet"), ("status", "not_implemented")),
                D(("ok", false), ("error", "tool frobnicate not implemented yet"), ("status", "not_implemented")),
            })
            {
                var e = Valid(LegacyResultAdapter.ToEnvelope(legacy, WritePhase.Preview));
                Assert.False(e.Ok);
                Assert.Equal(EnvelopeStatus.Failed, e.Status);
                Assert.Equal(EnvelopePhase.Preview, e.Phase);
                Assert.Equal(ErrorCodes.NotImplemented, e.Error!.Code);
                Assert.Contains("frobnicate", e.Summary);
            }
        }

        [Fact]
        public void ErrorWithoutOk_IsFailed_AndContractCodesAreKept()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("error", "level 'L9' not found"), ("code", "not_found"),
                ("candidates", new List<string> { "Level 1", "Level 2" })), null));
            Assert.Equal(ErrorCodes.NotFound, e.Error!.Code);
            Assert.Equal(new[] { "Level 1", "Level 2" }, e.Error.Candidates);

            var stale = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", false), ("status", "stale_document"), ("error", "stale document: planned against a different document")), WritePhase.Commit));
            Assert.Equal(ErrorCodes.DocChanged, stale.Error!.Code);
        }

        [Fact]
        public void NotFound_WithoutNearMatches_CarriesEmptyCandidates()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", false), ("error_code", "not_found"), ("error", "no such type")), null));
            Assert.NotNull(e.Error!.Candidates);
            Assert.Empty(e.Error.Candidates!);
        }

        [Fact]
        public void NearMatches_ComeFromSuggestionsOrAvailableLists()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", false), ("error", "type 'CL-02' not found"),
                ("available_types", new object[] { "CL-02A", new Dictionary<string, object?> { ["name"] = "CL-20" }, 7 })), null));
            Assert.Equal(new[] { "CL-02A", "CL-20" }, e.Error!.Candidates);
        }

        [Fact]
        public void CountsMapToCount()
        {
            Assert.Equal(34, Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("count", 34)), null)).Count);
            Assert.Equal(12, Valid(LegacyResultAdapter.ToEnvelope(D(("total", 12L)), null)).Count);
            Assert.Equal(5, Valid(LegacyResultAdapter.ToEnvelope(D(("matched", JsonDocument.Parse("5").RootElement)), WritePhase.Preview)).Count);
            Assert.Equal(2, Valid(LegacyResultAdapter.ToEnvelope(D(("found", 2.0)), null)).Count);
            Assert.Null(Valid(LegacyResultAdapter.ToEnvelope(D(("count", 2.5)), null)).Count);
            Assert.Null(Valid(LegacyResultAdapter.ToEnvelope(D(("count", -1)), null)).Count);
            Assert.Null(Valid(LegacyResultAdapter.ToEnvelope(D(("count", "many")), null)).Count);
        }

        [Fact]
        public void EmptyRead_SaysTheScopeWasNotReported()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("count", 0)), null));
            Assert.True(e.Ok);
            Assert.Equal(0, e.Count);
            Assert.NotNull(e.Searched);
            Assert.Empty(e.Searched!.Sources);
            Assert.NotEmpty(e.Searched.NotSearched);
        }

        [Fact]
        public void SuccessfulRead_IsDone_WithTheLegacySummary()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("summary", "3 sheets"), ("count", 3)), null));
            Assert.Equal(EnvelopeStatus.Done, e.Status);
            Assert.Equal("3 sheets", e.Summary);
            var plain = Valid(LegacyResultAdapter.ToEnvelope(D(("levels", new[] { "L1" })), null));
            Assert.True(plain.Ok);
            Assert.False(string.IsNullOrWhiteSpace(plain.Summary));
        }

        [Fact]
        public void Preview_IsDone_AndNeedsNoVerification()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("dry_run", true), ("would_set", 4)), WritePhase.Preview));
            Assert.True(e.Ok);
            Assert.Equal(EnvelopePhase.Preview, e.Phase);
            Assert.Null(e.Verified);
        }

        [Fact]
        public void Commit_WithReadBack_IsVerified_AndMismatchesMakeItPartial()
        {
            var clean = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("set", 2),
                ("verified", D(("checked", 2), ("matches", 2), ("mismatches", new List<object>())))), WritePhase.Commit));
            Assert.True(clean.Ok);
            Assert.Equal(EnvelopeStatus.Done, clean.Status);
            Assert.Equal(VerifyMethods.ParamReadback, clean.Verified!.Method);
            Assert.Equal(2, clean.Verified.Checked);

            var drift = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true),
                ("verified", D(("checked", 2), ("matches", 1), ("mismatches", new List<object>
                {
                    D(("id", 312045L), ("expected", "2700"), ("actual", "2400")),
                })))), WritePhase.Commit));
            Assert.True(drift.Ok);
            Assert.Equal(EnvelopeStatus.Partial, drift.Status);
            var m = Assert.Single(drift.Verified!.Mismatches);
            Assert.Equal(312045, m.Id);
            Assert.Equal("2400", m.Actual);
        }

        [Fact]
        public void Commit_BulkEditWithSkips_IsPartial_CountingWhatWasSet()
        {
            // BulkEdit.SetParameterByFilter's real result: 3 of 10 set, all 3 read back.
            var bare = Valid(LegacyResultAdapter.ToEnvelope(D(("set", 3), ("skipped", 7),
                ("verified", D(("checked", 3), ("matches", 3), ("mismatches", new List<object>())))), WritePhase.Commit));
            Assert.True(bare.Ok);
            Assert.Equal(EnvelopeStatus.Partial, bare.Status);
            Assert.Equal(3, bare.Count);

            var full = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("matched", 10), ("would_set", 10), ("set", 3),
                ("skipped", 7), ("skips", new List<object> { D(("id", 5L), ("reason", "read-only")) }),
                ("verified", D(("checked", 3), ("matches", 3), ("mismatches", new List<object>()))),
                ("headline", "3 of 10 set, 3 verified")), WritePhase.Commit));
            Assert.Equal(EnvelopeStatus.Partial, full.Status);
            Assert.Equal(3, full.Count);
            Assert.Equal("3 of 10 set, 3 verified", full.Summary);
        }

        [Fact]
        public void Commit_WithFailuresOrSkipsList_IsPartial_AndCleanIsDone()
        {
            var readBack = D(("checked", 2), ("matches", 2), ("mismatches", new List<object>()));
            var failures = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("changed", 2), ("verified", readBack),
                ("failures", new List<object> { D(("id", 9L), ("error", "pinned")) })), WritePhase.Commit));
            Assert.Equal(EnvelopeStatus.Partial, failures.Status);
            Assert.Equal(2, failures.Count);

            var clean = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("created", 2), ("skipped", 0),
                ("skips", new List<object>()), ("failures", new List<object>()), ("verified", readBack)), WritePhase.Commit));
            Assert.Equal(EnvelopeStatus.Done, clean.Status);
            Assert.Equal(2, clean.Count);

            // Skips on an unverified commit stay not ok (partial, with the unverified error).
            var unverified = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("set", 1), ("skipped", 4)), WritePhase.Commit));
            Assert.False(unverified.Ok);
            Assert.Equal(EnvelopeStatus.Partial, unverified.Status);
        }

        [Fact]
        public void Preview_CountsWhatWouldChange()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("dry_run", true), ("matched", 10), ("would_set", 7)), WritePhase.Preview));
            Assert.Equal(7, e.Count);
        }

        [Fact]
        public void FailedEmptyRead_StillSaysWhatWasSearched()
        {
            var e = LegacyResultAdapter.ToEnvelope(D(("ok", false), ("error", "no rooms in this phase"), ("count", 0)), null);
            Assert.Empty(EnvelopeInvariants.Violations(e));
            Assert.False(e.Ok);
            Assert.Equal(0, e.Count);
            Assert.NotNull(e.Searched);
        }

        [Fact]
        public void Commit_WithExistsCheck_IsVerified()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("created_id", 9L),
                ("verified", D(("exists", true), ("length_mm", 3000.0)))), WritePhase.Commit));
            Assert.True(e.Ok);
            Assert.Equal(VerifyMethods.Exists, e.Verified!.Method);

            var gone = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("created_id", 9L),
                ("verified", D(("exists", false)))), WritePhase.Commit));
            Assert.Equal(EnvelopeStatus.Partial, gone.Status);
            Assert.Single(gone.Verified!.Mismatches);
        }

        [Fact]
        public void Commit_WithoutReadBack_IsNeverOk()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", true), ("moved", 3)), WritePhase.Commit));
            Assert.False(e.Ok);
            Assert.Equal(EnvelopeStatus.Partial, e.Status);
            Assert.Equal(VerifyMethods.None, e.Verified!.Method);
            Assert.Contains("not verified", e.Error!.Message);
        }

        [Fact]
        public void LongLegacyText_IsShortened()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(D(("ok", false), ("error", new string('e', 900))), null));
            Assert.Equal(900, e.Error!.Message.Length);
            Assert.True(e.Summary.Length <= EnvelopeLimits.SummaryMaxChars);
            var s = Valid(LegacyResultAdapter.ToEnvelope(D(("message", new string('m', 400))), null));
            Assert.True(s.Summary.Length <= EnvelopeLimits.SummaryMaxChars);
        }

        [Fact]
        public void NullLegacy_IsAFailure()
        {
            var e = Valid(LegacyResultAdapter.ToEnvelope(null!, null));
            Assert.False(e.Ok);
            Assert.True(ResultSemantics.IsFailure(null!));
        }

        [Theory]
        [InlineData("{\"ok\":false}", true)]
        [InlineData("{\"ok\":false,\"error\":\"x\"}", true)]
        [InlineData("{\"error\":\"x\"}", true)]
        [InlineData("{\"error\":\"\"}", false)]
        [InlineData("{\"error\":null}", false)]
        [InlineData("{\"status\":\"not_implemented\"}", true)]
        [InlineData("{\"ok\":true,\"status\":\"failed\"}", true)]
        [InlineData("{\"ok\":true,\"error\":\"partial note\"}", false)]
        [InlineData("{\"ok\":true}", false)]
        [InlineData("{}", false)]
        [InlineData("{\"status\":\"blocked\",\"ok\":true}", false)]
        public void IsFailure(string json, bool expected)
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
                .ToDictionary(kv => kv.Key, kv => (object?)Plain(kv.Value));
            Assert.Equal(expected, ResultSemantics.IsFailure(legacy));
            // Same verdict when the values are still JsonElements (results parsed off the wire).
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            Assert.Equal(expected, ResultSemantics.IsFailure(raw));
        }

        [Fact]
        public void ToolRegistryNotImplemented_SaysOkFalse()
        {
            var src = TxGuardContractTests.ReadSource("BinaVibe/Mcp/Tools/ToolRegistry.cs");
            var at = src.IndexOf("Dictionary<string, object?> NotImplemented(string tool)", System.StringComparison.Ordinal);
            Assert.True(at > 0, "ToolRegistry.NotImplemented not found");
            var body = src.Substring(at, 400);
            Assert.Contains("[\"ok\"] = false", body);
            Assert.Contains("[\"status\"] = \"not_implemented\"", body);
        }

        private static object? Plain(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null => null,
            _ => v,
        };

        private static Envelope Valid(Envelope e)
        {
            Assert.Empty(EnvelopeInvariants.Violations(e));
            return e;
        }
    }
}
