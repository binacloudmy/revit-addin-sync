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

        // ---- r22, L8 R1 (959b3001): Langkau on the Malay box card left nothing made, and the reply showed the green check -------------

        private static AskCardModel Box() => AskCardModel.From(new AskDto
        {
            RequestId = "r", Question = "Ketinggian itu akan menggerakkan bahagian atas ke arah bertentangan. Yang mana satu maksud anda?",
            Options = new List<AskOptionDto>
            {
                new AskOptionDto { Id = "up|by|4 m", Label = "Naikkan bahagian atas sebanyak 4 m, ke +8804 mm dari PTBR_Aras Bumbung" },
                new AskOptionDto { Id = "Skip", Label = "Langkau", Kind = AskOptionKinds.Skip },
            },
        }, ReceiptLanguage.Ms);

        private static AnswerDto Answer(string key, object value) =>
            new AnswerDto { Values = new Dictionary<string, System.Text.Json.JsonElement> { [key] = System.Text.Json.JsonSerializer.SerializeToElement(value) } };

        [Fact]
        public void ASkipOrCancel_IsADecline_AndARealPickIsNot()
        {
            Assert.True(ReplyTone.Declines(Box(), Answer("Skip", true)));                    // Langkau (959b3001), and Skip in English alike
            Assert.False(ReplyTone.Declines(Box(), Answer("up|by|4 m", true)));
            Assert.True(ReplyTone.Declines(Box(), Answer("text", "")));                      // "Don't answer"
            Assert.False(ReplyTone.Declines(Box(), Answer("text", "naikkan 4 m")));          // a typed answer is not a decline
            Assert.True(ReplyTone.Declines(Box(), Answer("write_cancel", true)));
            Assert.True(ReplyTone.TypedRefusal(ReplyTone.Declines(Box(), Answer("Skip", true)), new List<Envelope?> { Env(EnvelopePhase.Preview) }));   // nothing made: info
            Assert.False(ReplyTone.TypedRefusal(ReplyTone.Declines(Box(), Answer("Skip", true)), new List<Envelope?> { Env(EnvelopePhase.Commit) }));
        }

        // ---- r22, P6 S2 (fba25214): the Malay undo line kept "(+1 more)" -----------------------------------------------------

        [Fact]
        public void TheMalayUndoLine_SaysLagi_AndKeepsTheEntryName()
        {
            Assert.Equal("Dibuat asal: Set parameter Width (+1 lagi)", BinaVibe.Copilot.Undo.UndoWords.Line(false, new[] { "Set parameter Width (+1 more)" }, true));
            Assert.Equal("Undone: Set parameter Width (+1 more)", BinaVibe.Copilot.Undo.UndoWords.Line(false, new[] { "Set parameter Width (+1 more)" }, false));
            Assert.Equal("Dibuat asal: Set parameter Width (+1 lagi)", BinaVibe.Copilot.Undo.UndoWords.ForPane("Undone: Set parameter Width (+1 more)", true));
        }

        // ---- r22, P7 T2 (9a2a937e): Langkau on the tag-type FORM card came back as {"tag type": "Skip"} and the reply kept the green check -------

        private static AskCardModel TagForm() => AskCardModel.From(new AskDto
        {
            RequestId = "r", Question = "Jenis tag yang mana? (1 lagi tidak disenaraikan)",
            Fields = new List<AskFieldDto>
            {
                new AskFieldDto { Id = "tag type", Label = "Jenis tag yang mana? (1 lagi tidak disenaraikan)", Type = AskFieldTypes.Choice, Group = "Jenis tag", Choices = new List<AskOptionDto>
                {
                    new AskOptionDto { Id = "jkrAR18t_dor_(t190) M_Door Tag Mark_: (t190a) M_Door Tag", Label = "jkrAR18t_dor_(t190) M_Door Tag Mark_: (t190a) M_Door Tag" },
                    new AskOptionDto { Id = "(t192a) sc1-100 tx3mm r2mm KKP", Label = "(t192a) sc1-100 tx3mm r2mm KKP" },
                    new AskOptionDto { Id = "Skip", Label = "Langkau", Kind = AskOptionKinds.Skip },
                } },
            },
        }, ReceiptLanguage.Ms);

        [Fact]
        public void ASkipPickedOnAFormField_IsADecline_AndATagTypeIsNot()
        {
            Assert.True(ReplyTone.Declines(TagForm(), Answer("tag type", "Skip")));                          // the P7 turn's own answer
            Assert.False(ReplyTone.Declines(TagForm(), Answer("tag type", "(t192a) sc1-100 tx3mm r2mm KKP")));
            Assert.False(ReplyTone.Declines(TagForm(), Answer("text", "guna t190")));                        // typed: an answer
            Assert.True(ReplyTone.TypedRefusal(ReplyTone.Declines(TagForm(), Answer("tag type", "Skip")), new List<Envelope?> { Env(EnvelopePhase.Read), Env(EnvelopePhase.Preview) }));
        }
    }
}
