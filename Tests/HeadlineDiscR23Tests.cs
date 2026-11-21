#nullable enable
// r23 (0.0.166): the headline disc of a reply that made or changed nothing. Live on 0.0.165 + merged50:
//   PC f85dd8f6 "Nothing was changed." (set-parameter skipped all; result findings, counts {skipped 1, findings 0}) drew the green check;
//   laptop b372f902 "No circuits made." (result changes, counts {values 0, written 0, elements 0}) drew the green check;
//   laptop f7d1c886 (wall-by-points Skip, no server result, one short sentence) drew no disc at all.
// The rule: green only when something was written or made, or a read answered; the neutral "i" for nothing made, skipped or honestly
// refused; never no disc for a tool turn. P8 74bd60f7 (the "i" after Langkau) is unchanged.
using System.Collections.Generic;
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class HeadlineDiscR23Tests
    {
        private static TurnResultDto Result(string kind, ResultCountsDto counts) => new TurnResultDto { Kind = kind, Reply = "r", Counts = counts };
        private static Envelope Env(string phase, string status = "done", bool ok = true) => new Envelope { Ok = ok, Phase = phase, Status = status };

        [Fact]
        public void NothingChanged_AndNoCircuitsMade_GetTheInfoDisc()
        {
            // f85dd8f6: the set-parameter run skipped its only value; nothing committed.
            Assert.True(ReplyTone.NothingMade(Result(TurnResultKinds.Findings, new ResultCountsDto { Skipped = 1, Findings = 0 }),
                new List<Envelope?> { Env(EnvelopePhase.Read) }));
            // b372f902: circuits made 0.
            Assert.True(ReplyTone.NothingMade(Result(TurnResultKinds.Changes, new ResultCountsDto { Values = 0, Written = 0, Elements = 0 }),
                new List<Envelope?> { Env(EnvelopePhase.Read) }));
            Assert.True(ReplyTone.NothingMade(Result(TurnResultKinds.Created, new ResultCountsDto { Created = 0 }), null));
            // A commit that measured no change (noop) is still nothing made.
            Assert.True(ReplyTone.NothingMade(Result(TurnResultKinds.Changes, new ResultCountsDto { Written = 0 }),
                new List<Envelope?> { Env(EnvelopePhase.Commit, EnvelopeStatus.Noop) }));
        }

        [Fact]
        public void SomethingWrittenMadeOrFound_KeepsTheGreenCheck()
        {
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Changes, new ResultCountsDto { Written = 2, Elements = 2 }), null));
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Changes, new ResultCountsDto { Written = 5, Skipped = 2 }), null));   // partly done
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Created, new ResultCountsDto { Created = 1 }), null));
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Selection, new ResultCountsDto { Elements = 2 }), null));
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Changes, new ResultCountsDto { Reverted = 1 }), null));            // an undo
            // A read that answered fully: a check with no findings and nothing skipped, a find of none.
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Findings, new ResultCountsDto { Findings = 0 }), null));
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Selection, new ResultCountsDto { Elements = 0 }), null));
            // A committed change wins over any count.
            Assert.False(ReplyTone.NothingMade(Result(TurnResultKinds.Changes, new ResultCountsDto { Written = 0 }),
                new List<Envelope?> { Env(EnvelopePhase.Commit) }));
            // No server result, or no counts: the server did not judge; the icon stays as the other rules give it.
            Assert.False(ReplyTone.NothingMade(null, null));
            Assert.False(ReplyTone.NothingMade(new TurnResultDto { Kind = TurnResultKinds.Changes, Reply = "r" }, null));
        }

        [Fact]
        public void AToolTurnsShortReply_AlwaysHasAHeadlineDisc()
        {
            // f7d1c886: one sentence after Skip, no card, no server result.
            Assert.True(ReplyTone.NeedsHeadline(true, false, 1, true, "Skipped, so no wall was made; say a length (e.g. 3000 mm) when you want it drawn."));
            Assert.True(ReplyTone.NeedsHeadline(true, false, 2, false, "Done."));
            Assert.True(ReplyTone.NeedsHeadline(true, false, 0, true, "Nothing was written."));
            // Unchanged: a card draws its own headline; a chat reply with no tool; a table or a list keeps its markdown; the user's bubble.
            Assert.False(ReplyTone.NeedsHeadline(true, true, 1, true, "Done."));
            Assert.False(ReplyTone.NeedsHeadline(true, false, 0, false, "Hello! How can I help?"));
            Assert.False(ReplyTone.NeedsHeadline(true, false, 1, false, "| Type | Count |\n|---|---|\n| D1 | 3 |"));
            Assert.False(ReplyTone.NeedsHeadline(true, false, 1, false, "- one\n- two"));
            Assert.False(ReplyTone.NeedsHeadline(false, false, 1, true, "Draw a wall"));
            Assert.False(ReplyTone.NeedsHeadline(true, false, 1, true, ""));
        }

        [Fact]
        public void TheRulesReachBothHeadlinePaths()
        {
            string Src(string rel)
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
                return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
            }
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("|| ReplyTone.NothingMade(TurnStatus.ServerResultOf(rr), rr.Evidence?.Calls.Select(c => c.Env));", vm);
            Assert.Contains("reply.ResultCard.InfoHeadline = reply.NothingWritten;", vm);   // the card path
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("bool toolProse = Model.ReplyTone.NeedsHeadline(m.Kind == CpMsgKind.AiReply, cardShown, m.ToolsUsed?.Count ?? 0, m.NothingWritten, m.Text);", chat);
            Assert.Contains("(serverProse || noopProse || toolProse || Model.ProseSplit.Applies(m.Text))", chat);
            Assert.Contains("info: m.NothingWritten)", chat);                                // the prose path
        }
    }
}
