#nullable enable
// r13 (0.0.147): a multi choice looks multi (live turn 951374ce on 0.0.146: the brochure form's type field had "multi": true but
// drew numbered single-pick tiles with a disc tick). A checkbox mark per tile, a "Pick any" / "Pilih satu atau lebih" hint under
// the question, and the Send button counts the ticks ("Send (2)"). Single-choice tiles and labels are unchanged.
using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskMultiLook0147Tests
    {
        private static AskOptionDto Opt(string id, string label, string? kind = null) => new AskOptionDto { Id = id, Label = label, Kind = kind };

        // The live form, cut down: a multi type field and a single product field.
        private static AskCardModel Form(bool multi, ReceiptLanguage lang = ReceiptLanguage.En) =>
            AskCardModel.From(new AskDto
            {
                RequestId = "req_02", Question = "Which type?; Which product?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "type", Label = "Which type?", Type = AskFieldTypes.Choice, Multi = multi,
                        Choices = new List<AskOptionDto> { Opt("t1", "1X13W: Normal"), Opt("t2", "1X8W: Normal"), Opt("all", "All of them") } },
                    new AskFieldDto { Id = "product", Label = "Which product?", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto> { Opt("p1", "Adrino 601"), Opt("p2", "Adrino 801"), Opt("Skip", "Skip", "skip") } },
                },
            }, lang);

        private static AskCardModel Options(bool multi, ReceiptLanguage lang = ReceiptLanguage.En) =>
            AskCardModel.From(new AskDto
            {
                RequestId = "r", Question = "Which levels?", Purpose = AskPurposes.Clarify, Multi = multi,
                Options = new List<AskOptionDto> { Opt("a", "Level 1"), Opt("b", "Level 2"), Opt("c", "Level 3"), Opt("d", "Level 4") },
            }, lang);

        [Fact]
        public void TileMark_SingleKeepsTheDisc_MultiDrawsABox()
        {
            Assert.Equal(AskTileMark.Disc, AskCardModel.TileMark(multi: false, ticked: false));
            Assert.Equal(AskTileMark.Disc, AskCardModel.TileMark(multi: false, ticked: true));
            Assert.Equal(AskTileMark.BoxEmpty, AskCardModel.TileMark(multi: true, ticked: false));
            Assert.Equal(AskTileMark.BoxTicked, AskCardModel.TileMark(multi: true, ticked: true));
        }

        [Fact]
        public void TheHint_FollowsTheTurnLanguage_AndOnlyMultiQuestionsShowIt()
        {
            Assert.Equal("Pick any", Form(true).MultiHint);
            Assert.Equal("Pilih satu atau lebih", Form(true, ReceiptLanguage.Ms).MultiHint);
            var form = Form(true);
            Assert.True(AskCardModel.FieldShowsMultiHint(form.Fields[0]));
            Assert.False(AskCardModel.FieldShowsMultiHint(form.Fields[1]));          // the single product field: no hint
            Assert.False(AskCardModel.FieldShowsMultiHint(Form(false).Fields[0]));
            Assert.False(form.ShowsMultiHint);                                       // a form's hint is per field, not under the card
            Assert.True(Options(true).ShowsMultiHint);
            Assert.False(Options(false).ShowsMultiHint);
        }

        [Fact]
        public void TheFormsSendButton_CountsTheTicks()
        {
            var card = Form(true);
            Assert.True(card.HasMulti);
            Assert.Equal("Answer 2 more", card.FooterLabel);                           // unchanged until the form can be sent
            card.Choose("type", "t1");
            card.Choose("product", "p2");
            Assert.Equal(1, card.TickCount);
            Assert.Equal("Send (1)", card.FooterLabel);
            card.Choose("type", "t2");
            Assert.Equal(2, card.TickCount);
            Assert.Equal("Send (2)", card.FooterLabel);
            Assert.True(card.FooterEnabled);
            card.Choose("product", "Skip");                                              // a skip on a single field is no tick
            Assert.Equal("Send (2)", card.FooterLabel);
            card.Choose("type", "t1"); card.Choose("type", "t2");                        // untick both
            Assert.Equal(0, card.TickCount);
            Assert.Equal("Answer 1 more", card.FooterLabel);
        }

        [Fact]
        public void ASingleChoiceForm_KeepsContinue()
        {
            var card = Form(false);
            Assert.False(card.HasMulti);
            card.Choose("type", "t1"); card.Choose("product", "p1");
            Assert.Equal(0, card.TickCount);
            Assert.Equal("Continue", card.FooterLabel);
        }

        [Fact]
        public void AMultiOptionsCard_SaysSend_ThenCountsTheTicks()
        {
            var card = Options(true);
            Assert.Equal("Send", card.FooterLabel);
            Assert.False(card.FooterEnabled);
            card.ToggleMulti("b"); card.ToggleMulti("d");
            Assert.Equal("Send (2)", card.FooterLabel);
            Assert.True(card.FooterEnabled);
            card.ToggleMulti("b");
            Assert.Equal("Send (1)", card.FooterLabel);
            Assert.Equal("Continue", Options(false).FooterLabel);                       // single options: unchanged
            Assert.Equal(0, Options(false).TickCount);
        }

        // ─── Tokens the box draws with, in both themes ─────────────────────────────────────────────────────

        private static double Lum(string hex)
        {
            var h = hex.TrimStart('#');
            if (h.Length == 8) h = h.Substring(2);                                       // these tokens are opaque
            double F(int i) { var c = Convert.ToInt32(h.Substring(i * 2, 2), 16) / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * F(0) + 0.7152 * F(1) + 0.0722 * F(2);
        }

        private static double Contrast(string a, string b)
        {
            double x = Lum(a), y = Lum(b);
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheBox_ReadsInBothThemes(bool dark)
        {
            string T(string k) => ThemeTokens.Hex(k, dark)!;
            Assert.True(Contrast(T("Cp.Muted"), T("Cp.Bg")) >= 3, "empty box edge on the card");                 // non-text control: 3:1
            Assert.True(Contrast(T("Cp.Accent"), T("Cp.Bg")) >= 3, "ticked box on the card");
            Assert.True(Contrast(T("Cp.AccentContrast"), T("Cp.Accent")) >= 3, "tick inside the box");
            Assert.True(Contrast(T("Cp.Faint"), T("Cp.Bg")) >= 4.5, "the hint line");
        }
    }
}
