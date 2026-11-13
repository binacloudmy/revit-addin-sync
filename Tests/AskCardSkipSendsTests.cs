#nullable enable
// Live 14 Nov, E-d09: a one-field form ("Dimension type X is not in the model. Which dimension type should I use?", four
// types and Skip). After clicking Skip the drafter still had to click Continue. On a form with one field Skip is the whole
// answer: the card sends it at once, as a single question sends the option clicked. A form with several fields keeps its
// design: every pick, Skip included, is drafted until the footer button. Invented names.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskCardSkipSendsTests
    {
        private static AskOptionDto Opt(string id, string label, string? kind = null) => new AskOptionDto { Id = id, Label = label, Kind = kind };

        private static AskFieldDto TypeField(string id) => new AskFieldDto
        {
            Id = id, Label = "Dimension type", Type = AskFieldTypes.Choice,
            Choices = new List<AskOptionDto>
            {
                Opt("101", "Arrow 3mm"), Opt("102", "Linear Style A"), Opt("103", "Linear Style B"), Opt("104", "Linear Style C"),
                Opt("skip", "Skip", AskOptionKinds.Skip),
            },
        };

        private static AskCardModel Form(params AskFieldDto[] fields) => AskCardModel.From(new AskDto
        {
            RequestId = "req_d", Purpose = AskPurposes.Clarify, Title = "Dimension type ZZ-NONE is not in the model",
            Question = "Which dimension type should I use?", Fields = fields.ToList(),
        }, ReceiptLanguage.En);

        [Fact]
        public void OnAOneFieldForm_SkipIsTheWholeAnswer()
        {
            var card = Form(TypeField("dim"));
            Assert.Equal(AskCardKind.Fields, card.Kind);
            Assert.True(card.PickSends("dim", "skip"));
            Assert.False(card.PickSends("dim", "101"));        // a type is picked, then sent with Continue, as before

            card.Pick("dim", "skip");
            Assert.True(card.FooterEnabled);                    // what the card sends at once is a complete answer
            Assert.Equal("Dimension type: Skipped", card.SummaryForDraft());
        }

        [Fact]
        public void OnAOneFieldForm_TheSKeySendsToo()
        {
            var card = Form(TypeField("dim"));
            var r = AskCardKeys.ForChar(card, 'S');
            Assert.Equal(AskKeyKind.PickAndSend, r.Kind);
            AskCardKeys.Apply(card, r);
            Assert.Equal("skip", card.Picks["dim"]);
            Assert.Equal(AskKeyKind.PickField, AskCardKeys.ForChar(card, '1').Kind);
        }

        [Fact]
        public void OnAFormWithSeveralFields_SkipStaysADraft()
        {
            var card = Form(TypeField("a"), TypeField("b"));
            Assert.False(card.PickSends("a", "skip"));
            Assert.Equal(AskKeyKind.PickField, AskCardKeys.ForChar(card, 'S').Kind);
        }

        [Fact]
        public void TheCardSendsWhenThePickSends()
        {
            var root = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (root != null && !File.Exists(Path.Combine(root.FullName, "RevitWebAppSync.csproj"))) root = root.Parent;
            var s = File.ReadAllText(Path.Combine(root!.FullName, "UI/Copilot/Controls/AskCard.cs"));
            Assert.Contains("if (_m.PickSends(f.Id, skip.Id)) SendDraft();", s);
            Assert.Contains("if (r.Kind == AskKeyKind.PickAndSend) { SendDraft(); return; }", s);
            Assert.DoesNotContain("() => { _m.Pick(f.Id, skip.Id); Refresh(); }", s);   // every Skip goes through PickSkip
        }
    }
}
