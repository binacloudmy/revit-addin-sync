#nullable enable
// CN09 (turn 950f45a9): set_parameters "Type Name" failed on every element (a read-only parameter), then run_revit_code renamed the type and succeeded. The
// first attempt changed nothing, yet it raised the amber "Not every change in this turn was applied". A failed attempt that touched nothing, followed by a done
// write, is the retry that worked. A real partial write, a failure with touched elements and a final failure still raise it.
using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FallbackToCodeNoBanner0143Tests
    {
        private static Envelope SetParamsFailedOnEveryElement(string phase) => new Envelope
        {
            Ok = false, Status = EnvelopeStatus.Failed, Phase = phase, Summary = "Type Name is read-only; 0 of 20 written.",
            Error = new ErrorDto { Code = ErrorCodes.RuntimeError, Message = "Type Name is read-only on every element." },
            Changes = new ChangeReportDto(),                 // a change report that touched nothing (positive evidence that nothing changed)
        };

        private static Envelope CodeDone() => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Renamed the type.",
            Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = { new FieldChangeDto { Id = 1001, Field = "Type Name", Before = "a", After = "b" } } } },
        };

        private static ToolCallDto Call(string id, string tool) => new ToolCallDto { CallId = id, IdempotencyKey = "k" + id, Tool = tool, Args = JsonDocument.Parse(tool == ToolNames.SetParameters ? "{\"param\":\"Type Name\",\"value\":\"PTh300a-REV\"}" : "{\"intent\":\"Rename the type\"}").RootElement.Clone() };

        private static TurnEvidence Turn(params (string Tool, Envelope Env)[] writes)
        {
            var ev = new TurnEvidence();
            var n = 0;
            foreach (var (tool, env) in writes)
                ev.Add(new ToolRequestDto { RequestId = "r" + n, Kind = ToolRequestKinds.Write, Calls = new List<ToolCallDto> { Call("c" + n++, tool) } }, new[] { env });
            return ev;
        }

        [Theory]
        [InlineData(EnvelopePhase.Preview)]
        [InlineData(EnvelopePhase.Commit)]
        public void TheCN09Shape_FailedSetParametersThenSuccessfulCode_RaisesNoBanner(string failedPhase)
        {
            var ev = Turn((ToolNames.SetParameters, SetParamsFailedOnEveryElement(failedPhase)), (ToolNames.RunRevitCode, CodeDone()));
            Assert.False(ev.Caution);
            Assert.Equal(1, ev.RetriedAttempts);
        }

        [Fact]
        public void ACommitFailureWithNoChangeReport_StaysAmber()
        {
            var bare = SetParamsFailedOnEveryElement(EnvelopePhase.Commit);
            bare.Changes = null;                              // no proof that nothing changed: the old rule holds
            Assert.True(Turn((ToolNames.SetParameters, bare), (ToolNames.RunRevitCode, CodeDone())).Caution);
        }

        [Fact]
        public void AFinalFailure_StillRaisesTheBanner()
        {
            Assert.True(Turn((ToolNames.SetParameters, SetParamsFailedOnEveryElement(EnvelopePhase.Commit))).Caution);
            // the code write came BEFORE the failed attempt: the failure is last
            Assert.True(Turn((ToolNames.RunRevitCode, CodeDone()), (ToolNames.SetParameters, SetParamsFailedOnEveryElement(EnvelopePhase.Commit))).Caution);
        }

        [Fact]
        public void ARealPartialWrite_StillRaisesTheBanner()
        {
            var partial = new Envelope
            {
                Ok = false, Status = EnvelopeStatus.Partial, Phase = EnvelopePhase.Commit, Summary = "12 of 20 written; 8 skipped.",
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = { new FieldChangeDto { Id = 5, Field = "Comments", After = "X" } } } },
            };
            Assert.True(Turn((ToolNames.SetParameters, partial), (ToolNames.RunRevitCode, CodeDone())).Caution);
        }

        [Fact]
        public void AFailureThatTouchedElements_IsNotAnAttemptThatChangedNothing()
        {
            var touched = SetParamsFailedOnEveryElement(EnvelopePhase.Commit);
            touched.Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = { new FieldChangeDto { Id = 9, Field = "Mark", After = "Z" } } } };
            Assert.True(Turn((ToolNames.SetParameters, touched), (ToolNames.RunRevitCode, CodeDone())).Caution);
        }

        [Fact]
        public void TheSameToolRetry_StillWorks()
        {
            Assert.False(Turn((ToolNames.RunRevitCode, new Envelope { Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Preview, Error = new ErrorDto { Code = ErrorCodes.RuntimeError, Message = "compile error" } }),
                              (ToolNames.RunRevitCode, CodeDone())).Caution);
        }

        [Fact]
        public void ACodeWriteThatWasOnlyPreviewed_IsNotTheRetry()
        {
            var preview = CodeDone();
            preview.Phase = EnvelopePhase.Preview;                                    // "Preview: ... Nothing has changed yet."
            var ev = Turn((ToolNames.SetParameters, SetParamsFailedOnEveryElement(EnvelopePhase.Preview)), (ToolNames.RunRevitCode, preview));
            Assert.True(ev.Caution);
            Assert.Equal(0, ev.RetriedAttempts);
        }

        [Fact]
        public void AnUnrelatedCodeWrite_IsNotTheRetry()
        {
            var unrelated = CodeDone();
            unrelated.Changes!.Touched.Modified[0].Field = "Comments";                  // another parameter than the failed "Type Name"
            var ev = Turn((ToolNames.SetParameters, SetParamsFailedOnEveryElement(EnvelopePhase.Preview)), (ToolNames.RunRevitCode, unrelated));
            Assert.True(ev.Caution);
            Assert.Equal(0, ev.RetriedAttempts);
            var noReport = CodeDone();
            noReport.Changes = null;                                                    // cannot be compared: amber
            Assert.True(Turn((ToolNames.SetParameters, SetParamsFailedOnEveryElement(EnvelopePhase.Preview)), (ToolNames.RunRevitCode, noReport)).Caution);
        }

        [Fact]
        public void ATypeRenameReportedAsName_MatchesTypeName()
        {
            var named = CodeDone();
            named.Changes!.Touched.Modified[0].Field = "Name";
            Assert.False(Turn((ToolNames.SetParameters, SetParamsFailedOnEveryElement(EnvelopePhase.Preview)), (ToolNames.RunRevitCode, named)).Caution);
        }
    }
}
