#nullable enable
// r23 (0.0.167), live PC round P13 on 0.0.166 (user 9002):
// (1) a65ed21b "Set Comments ... on all doors with Mark FD1, ask me first" → This view (2 doors) → write card → Cancel left nothing
//     selected, and e07ef4cb "on the selected doors" found none. The events: no find_elements ran; the set-parameter playbook's two read
//     passes (seq 4, seq 11) ran with the turn's selection empty and Revit's empty (snapshots seq 5, 8, 12, 15: 0 selected), so no swap
//     was made and there was nothing to give back; the server's gate (seq 14) cancelled set_parameters on ids 977255, 977284 before any
//     request reached the add-in, and the turn ended with no result card (no "selected" claim). The doors were never selected in that
//     path: the give-back keeps a selection the Copilot made, it does not create one. This pins that exact sequence.
// (2) C6 44c1f9e6: a turn whose change attempts were all refused drew the green check (see RefusedOnly; the live case needs the backend,
//     because its refusals never reach the add-in).
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class P13R23Tests
    {
        private static SnapshotModel Turn(params long[] ids) => new SnapshotModel
        {
            Doc = new SnapshotDoc { Title = "MRSM1-run" },
            Selection = new SnapshotSelection { Count = ids.Length, Ids = ids.ToList() },
        };

        [Fact]
        public void A65ed21b_ThePlaybookPathWithNoFind_SwapsNothing_SoTheCancelLeavesTheSelectionAsItWas()
        {
            var ledger = new SelectionSwapLedger();
            var turn = Turn();                                   // seq 1: nothing selected
            foreach (var _ in new[] { "seq 4 read pass", "plan of seq 11", "seq 11 read pass" })
            {
                var d = TurnSelection.Decide(turn, new long[0], id => true);   // Revit holds nothing either
                Assert.False(d.Restore);                         // so UseTurnSelection records no swap
            }
            Assert.False(ledger.Open);
            Assert.Null(ledger.Take(new long[0], id => true));   // the preview-end / cancel give-back has nothing to give
            // Contrast c7601a4a (20 Nov): a find selected the 2 doors first; then the read pass swaps and the give-back restores them.
            ledger.Record(new long[] { 977255, 977284 }, null, new long[0]);
            Assert.Equal(new long[] { 977255, 977284 }, ledger.Take(new long[0], id => true)!.Ids);
        }

        private static (ToolCallDto Call, Envelope Env) Call(string tool, string args, bool ok, string phase, string status) =>
            (new ToolCallDto { Tool = tool, Args = JsonDocument.Parse(args).RootElement.Clone() }, new Envelope { Ok = ok, Phase = phase, Status = status });

        [Fact]
        public void EveryChangeAttemptRefused_GetsTheInfoDisc_NeverTheGreenCheck()
        {
            const string reload = "{\"code\":\"x\",\"expected\":{\"modifies\":6}}";
            Assert.True(ReplyTone.RefusedOnly(new[]
            {
                Call(ToolNames.ModelOverview, "{\"sections\":[\"links\"]}", true, EnvelopePhase.Read, EnvelopeStatus.Done),
                Call(ToolNames.RunRevitCode, reload, false, EnvelopePhase.Read, EnvelopeStatus.Rejected),
                Call(ToolNames.RunRevitCode, reload, false, EnvelopePhase.Read, EnvelopeStatus.Rejected),
            }));
            Assert.True(ReplyTone.RefusedOnly(new[] { Call(ToolNames.SetParameters, "{\"param\":\"Comments\"}", false, EnvelopePhase.Preview, EnvelopeStatus.Failed) }));
        }

        [Fact]
        public void AChangeThatWentThrough_OrAReadOnlyTurn_IsNotRefusedOnly()
        {
            const string write = "{\"code\":\"x\",\"expected\":{\"modifies\":2}}";
            Assert.False(ReplyTone.RefusedOnly(new[]
            {
                Call(ToolNames.RunRevitCode, write, false, EnvelopePhase.Read, EnvelopeStatus.Rejected),
                Call(ToolNames.RunRevitCode, write, true, EnvelopePhase.Commit, EnvelopeStatus.Done),   // the retry committed
            }));
            Assert.False(ReplyTone.RefusedOnly(new[]
            {
                Call(ToolNames.FindElements, "{\"queries\":[]}", true, EnvelopePhase.Read, EnvelopeStatus.Done),
                Call(ToolNames.RunRevitCode, "{\"code\":\"x\"}", false, EnvelopePhase.Read, EnvelopeStatus.Failed),   // a read code that failed: not a change attempt
            }));
            Assert.False(ReplyTone.RefusedOnly(new[] { Call(ToolNames.RunRevitCode, "{\"playbook\":\"set-parameter\",\"code\":\"x\"}", true, EnvelopePhase.Read, EnvelopeStatus.Done) }));
            Assert.False(ReplyTone.RefusedOnly(null));
        }
    }
}
