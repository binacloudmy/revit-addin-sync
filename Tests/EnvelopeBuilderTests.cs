// EnvelopeBuilder (W1 Task 3, plan §2.1, spec §10): the one way tools build
// contract envelopes. Build() runs EnvelopeInvariants: an invalid envelope
// throws in DEBUG builds; RELEASE logs, repairs what it safely can and returns.
// Build(strict, log) pins both modes whatever configuration the tests run in
// (CI runs them in Release).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class EnvelopeBuilderTests
    {
        private static readonly string Hash = "sha256:" + new string('a', 64);

        [Fact]
        public void NotFoundWithoutCandidatesThrowsInDebug()
        {
#if DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Fail(ErrorCodes.NotFound, "No ceiling type 'CL-02'."));
            Assert.Contains("candidates", ex.Message);
#else
            var e = EnvelopeBuilder.Fail(ErrorCodes.NotFound, "No ceiling type 'CL-02'.");
            Assert.NotNull(e.Error!.Candidates);
            Assert.Empty(EnvelopeInvariants.Violations(e));
#endif
        }

        [Fact]
        public void NotFoundWithoutCandidates_StrictThrows_LenientRepairsAndLogs()
        {
            Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Failure(ErrorCodes.NotFound, "No level 'L9'.", null, null).Build(strict: true, log: null));

            var logged = new List<string>();
            var e = EnvelopeBuilder.Failure(ErrorCodes.NotFound, "No level 'L9'.", null, null).Build(strict: false, log: logged.Add);
            Assert.Single(logged);
            Assert.Contains("candidates", logged[0]);
            Assert.Empty(e.Error!.Candidates!);
            Assert.Empty(EnvelopeInvariants.Violations(e));
        }

        [Fact]
        public void NotFoundWithEmptyCandidates_IsValid()
        {
            var e = Strict(EnvelopeBuilder.Failure(ErrorCodes.NotFound, "Nothing like 'XYZ'.", Array.Empty<string>(), null));
            Assert.False(e.Ok);
            Assert.Equal(EnvelopeStatus.Failed, e.Status);
            Assert.Equal(EnvelopePhase.Read, e.Phase);
            Assert.Empty(e.Error!.Candidates!);
        }

        [Fact]
        public void Fail_MapsBusyAndTimeoutToTheirStatus_AndKeepsThePhase()
        {
            var busy = Strict(EnvelopeBuilder.Failure(ErrorCodes.RevitBusy, "Revit is in an edit mode.", null, WritePhase.Preview));
            Assert.Equal(EnvelopeStatus.RevitBusy, busy.Status);
            Assert.Equal(EnvelopePhase.Preview, busy.Phase);
            var slow = Strict(EnvelopeBuilder.Failure(ErrorCodes.Timeout, "Took too long.", null, WritePhase.Commit));
            Assert.Equal(EnvelopeStatus.Timeout, slow.Status);
            Assert.Equal(EnvelopePhase.Commit, slow.Phase);
            var other = Strict(EnvelopeBuilder.Failure(ErrorCodes.NotOwned, "Owned by aisyah.", new[] { "W-2", "W-2", "W-3" }, null));
            Assert.Equal(EnvelopeStatus.Failed, other.Status);
            Assert.Equal(new[] { "W-2", "W-3" }, other.Error!.Candidates);
        }

        [Fact]
        public void Fail_LongMessage_KeepsFullErrorButShortSummary()
        {
            var msg = new string('x', 500);
            var e = Strict(EnvelopeBuilder.Failure(ErrorCodes.RuntimeError, msg, null, null));
            Assert.Equal(msg, e.Error!.Message);
            Assert.Equal(EnvelopeLimits.SummaryMaxChars, EnvelopeInvariants.CodePoints(e.Summary));
        }

        [Fact]
        public void Fail_UnknownCode_IsAViolation()
        {
            Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Failure("exploded", "boom", null, null).Build(strict: true, log: null));
            var e = EnvelopeBuilder.Failure("exploded", "boom", null, null).Build(strict: false, log: _ => { });
            Assert.Equal(ErrorCodes.RuntimeError, e.Error!.Code);
            Assert.Empty(EnvelopeInvariants.Violations(e));
        }

        [Fact]
        public void Read_CarriesEveryField()
        {
            var e = Strict(EnvelopeBuilder.Read("Found 3 doors on Level 2.")
                .WithCount(3)
                .WithHandle("h_1")
                .WithData(new Dictionary<string, object?> { ["rows"] = new[] { new { id = 1L, name = "D1" } } })
                .WithSearched(new SearchedDto { Sources = { "host" }, Categories = { "Doors" } })
                .WithHighlight(3, 2)
                .WithNext("Use handle h_1 to inspect them."));
            Assert.True(e.Ok);
            Assert.Equal(EnvelopeStatus.Done, e.Status);
            Assert.Equal(EnvelopePhase.Read, e.Phase);
            Assert.Equal(3, e.Count);
            Assert.Equal("h_1", e.Handle);
            Assert.Equal("D1", e.Data!.Value.GetProperty("rows")[0].GetProperty("name").GetString());
            Assert.Equal(2, e.Highlight!.VisibleInView);
            var json = CopilotJson.Serialize(e);
            Assert.DoesNotContain("\"error\"", json);
            Assert.Contains("\"visible_in_view\":2", json);
        }

        [Fact]
        public void EmptyReadWithoutSearched_StrictThrows_LenientSaysScopeUnknown()
        {
            Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Read("No doors.").WithCount(0).Build(strict: true, log: null));
            var e = EnvelopeBuilder.Read("No doors.").WithCount(0).Build(strict: false, log: _ => { });
            Assert.NotNull(e.Searched);
            Assert.NotEmpty(e.Searched!.NotSearched);
            Assert.Empty(EnvelopeInvariants.Violations(e));
        }

        [Fact]
        public void CommitOk_NeedsVerification()
        {
            Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Write(WritePhase.Commit, "Set 3 values.").Build(strict: true, log: null));
            Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Write(WritePhase.Commit, "Set 3 values.")
                    .WithVerified(new VerifiedDto { Method = VerifyMethods.None }).Build(strict: true, log: null));

            var ok = Strict(EnvelopeBuilder.Write(WritePhase.Commit, "Set 3 values; read back 3 of 3.")
                .WithChanges(new ChangesDto { Modified = { new FieldChangeDto { Id = 1, Field = "Mark", Before = "A", After = "B" } } },
                             new SideEffectsDto { ModifiedByCategory = { ["Rooms"] = 2 } })
                .WithVerified(new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 3 })
                .WithPlanHash(Hash));
            Assert.True(ok.Ok);
            Assert.Equal(2, ok.Changes!.SideEffects!.ModifiedByCategory["Rooms"]);

            var noop = Strict(EnvelopeBuilder.Write(WritePhase.Commit, "Nothing to change.").Status(EnvelopeStatus.Noop));
            Assert.True(noop.Ok);
        }

        [Fact]
        public void CommitOkUnverified_LenientIsNotOk()
        {
            var e = EnvelopeBuilder.Write(WritePhase.Commit, "Set 3 values.").Build(strict: false, log: _ => { });
            Assert.False(e.Ok);
            Assert.Equal(EnvelopeStatus.Partial, e.Status);
            Assert.NotNull(e.Error);
            Assert.Empty(EnvelopeInvariants.Violations(e));
        }

        [Fact]
        public void Status_SetsOk()
        {
            // Tidak is answered on the preview: nothing was committed, so the phase stays preview.
            Assert.True(Strict(EnvelopeBuilder.Write(WritePhase.Preview, "Drafter said Tidak.").Status(EnvelopeStatus.Rejected)).Ok);
            Assert.True(Strict(EnvelopeBuilder.Write(WritePhase.Preview, "2 of 3 previewed.").Status(EnvelopeStatus.Partial)).Ok);
            Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Read("x").Status(EnvelopeStatus.Failed).Build(strict: true, log: null));
            Assert.Throws<InvalidOperationException>(() =>
                EnvelopeBuilder.Read("x").Status("sideways").Build(strict: true, log: null));
        }

        [Fact]
        public void Summary_MustBe1To300()
        {
            Assert.Throws<InvalidOperationException>(() => EnvelopeBuilder.Read("").Build(strict: true, log: null));
            Assert.Throws<InvalidOperationException>(() => EnvelopeBuilder.Read(new string('s', 301)).Build(strict: true, log: null));
            var e = EnvelopeBuilder.Read(new string('s', 301)).WithNext(new string('n', 201)).Build(strict: false, log: _ => { });
            Assert.Equal(300, e.Summary.Length);
            Assert.Equal(200, e.Next!.Length);
            Assert.Empty(EnvelopeInvariants.Violations(e));
        }

        [Fact]
        public void WithData_ScalarIsAViolation_NullClears()
        {
            Assert.Throws<InvalidOperationException>(() => EnvelopeBuilder.Read("x").WithData(42).Build(strict: true, log: null));
            Assert.Null(Strict(EnvelopeBuilder.Read("x").WithData(new { a = 1 }).WithData(null)).Data);
        }

        [Fact]
        public void WithData_SurvivesValuesPlainJsonRejects()
        {
            var raw = new Dictionary<string, object?>
            {
                ["nan"] = double.NaN,
                ["anon"] = new { id = 5L, error = "x" },
                ["list"] = new List<object> { 1, "two", new Dictionary<string, object?> { ["three"] = null } },
                ["json"] = JsonDocument.Parse("{\"k\":[1,2]}").RootElement,
            };
            var e = Strict(EnvelopeBuilder.Read("x").WithData(raw));
            var data = e.Data!.Value;
            Assert.Equal(JsonValueKind.Object, data.ValueKind);
            Assert.Equal(5, data.GetProperty("anon").GetProperty("id").GetInt64());
            Assert.Equal(2, data.GetProperty("json").GetProperty("k")[1].GetInt32());
            Assert.Equal("NaN", data.GetProperty("nan").GetString());
        }

        [Fact]
        public void WithWarnings_TakesAFailureLogSnapshot_AndDialogs()
        {
            using (FailureLog.Begin())
            {
                FailureLog.Current.Add("Walls overlap.", WarningSeverity.Warning, "d1", new long[] { 2, 1 });
                var e = Strict(EnvelopeBuilder.Write(WritePhase.Preview, "Preview: 1 wall.")
                    .WithWarnings(FailureLog.Current.Snapshot())
                    .WithDialogs(new[] { new DialogDto { Id = "Dialog_X", Text = "Sure?", AnsweredWith = "OK" } }));
                var w = Assert.Single(e.Warnings!);
                Assert.Equal(new long[] { 1, 2 }, w.Ids);
                Assert.Equal("OK", Assert.Single(e.Dialogs!).AnsweredWith);
            }
            Assert.Null(Strict(EnvelopeBuilder.Read("x").WithWarnings(Array.Empty<WarningDto>())).Warnings);
        }

        [Fact]
        public void WithImage_IsChecked()
        {
            var good = Strict(EnvelopeBuilder.Read("Active view.")
                .WithImage(new ImageDto { Base64 = "iVBOR", Width = 800, Height = 600, Caption = "Level 1" }));
            Assert.Equal(EnvelopeLimits.ImageMediaType, good.Image!.MediaType);
            Assert.Throws<InvalidOperationException>(() => EnvelopeBuilder.Read("x")
                .WithImage(new ImageDto { Base64 = "iVBOR", Width = 2048, Height = 600, Caption = "big" }).Build(strict: true, log: null));
            Assert.Throws<InvalidOperationException>(() => EnvelopeBuilder.Read("x").WithPlanHash("md5:1").Build(strict: true, log: null));
        }

        [Fact]
        public void Builder_IsSpentAfterBuild()
        {
            var b = EnvelopeBuilder.Read("x");
            Strict(b);
            Assert.Throws<InvalidOperationException>(() => b.WithCount(1));
            Assert.Throws<InvalidOperationException>(() => b.Build());
        }

        [Fact]
        public void PublicBuild_ProducesValidEnvelopes()
        {
            var e = EnvelopeBuilder.Read("Read 2 rows.").WithCount(2).Build();
            Assert.Empty(EnvelopeInvariants.Violations(e));
            var f = EnvelopeBuilder.Fail(ErrorCodes.InvalidArgs, "param is required.", phase: WritePhase.Preview);
            Assert.Equal(EnvelopePhase.Preview, f.Phase);
            Assert.Empty(EnvelopeInvariants.Violations(f));
        }

        private static Envelope Strict(EnvelopeBuilder b) => b.Build(strict: true, log: null);
    }
}
