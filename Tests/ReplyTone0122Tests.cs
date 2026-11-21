#nullable enable
// r22 (PC round P5, turn 6bf0f4ec): "write it" typed at the duct write card was refused (nothing written), yet the reply "Typed text isn't
// an approval, so nothing was written…" showed the green success check. Such a reply now gets the neutral info disc, in English and Malay
// alike (the tone does not read the words); a reply after a write that committed, or with no typed answer, keeps its icon.
using System.Collections.Generic;
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReplyTone0122Tests
    {
        private static Envelope Env(string phase, bool ok = true) => new Envelope { Ok = ok, Phase = phase, Status = ok ? "done" : "rejected" };

        [Fact]
        public void ATypedAnswerAtAWriteCard_ThatWroteNothing_IsTheInfoTone()
        {
            Assert.True(ReplyTone.TypedRefusal(true, new List<Envelope?> { Env(EnvelopePhase.Read), Env(EnvelopePhase.Preview) }));   // P5: read + nothing committed
            Assert.True(ReplyTone.TypedRefusal(true, null));
        }

        [Fact]
        public void OtherReplies_KeepTheirIcon()
        {
            Assert.False(ReplyTone.TypedRefusal(true, new List<Envelope?> { Env(EnvelopePhase.Commit) }));    // a later click wrote: success
            Assert.False(ReplyTone.TypedRefusal(false, new List<Envelope?> { Env(EnvelopePhase.Read) }));     // no typed answer: as before
            Assert.False(ReplyTone.TypedRefusal(false, null));
        }

        [Fact]
        public void TheHeadline_DrawsTheInfoDisc_ForSuchAReply_InTheProseAndTheCardPaths()
        {
            string Src(string rel)
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
                return File.ReadAllText(Path.Combine(dir!.FullName, rel));
            }
            var view = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("warn ? \"Cp.AmberBg\" : info ? ReplyTone.InfoDiscFill : \"Cp.Tier1Bg\"", view);
            Assert.Contains("warn ? \"Cp.Amber\" : info ? ReplyTone.InfoGlyphStroke : \"Cp.Green\"", view);
            Assert.Contains("info: card.InfoHeadline && !warn", view);
            Assert.Contains("info: m.NothingWritten)", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("ReplyTone.TypedRefusal(_typedAtWriteCard, rr.Evidence?.Calls.Select(c => c.Env))", vm);
            Assert.Contains("_typedAtWriteCard = _typedAtWriteCard || msg.Ask.Wide != null;", vm);
        }
    }
}
