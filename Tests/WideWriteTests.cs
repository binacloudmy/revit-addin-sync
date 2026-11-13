#nullable enable
// The "Needs your OK" card for a write wider than the prompt reads (board Confirm): the pure model (texts, counts,
// groups, the three buttons and what each answers, EN and BM) and the board's numbers in the view source.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WideWriteTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static WideTypeRow T(string name, int n) => new WideTypeRow { Name = name, Count = n };

        // The board's own numbers: 235 fixtures, 10 types; 2 downlight types (52) and 8 other (183).
        private static WideWriteModel Board(ReceiptLanguage lang = ReceiptLanguage.En, string? reason = "matches the brochure") => WideWriteModel.From(
            lang,
            "Before I write: this reaches 10 lighting types, and 8 of them are not downlights.",
            "The brochure is for a recessed LED downlight. Nothing has been written yet.",
            new[]
            {
                new WideGroupInput { Label = "Downlights", IsWide = false, Types = { T("dl_1X13W", 38), T("dl_1X8W", 14) } },
                new WideGroupInput { Label = "Other lighting", IsWide = true,
                    Types = { T("a", 126), T("b", 22), T("c", 10), T("d", 10), T("e", 5), T("f", 4), T("g", 3), T("h", 3) } },
            },
            new[] { "Manufacturer", "Model", "Type Comments", "Lamp", "Wattage Comments", "Kadaran_Kuasa", "Kadaran_Arus", "Saiz_Fizikal" },
            "downlight", reason);

        [Fact]
        public void TheBoardsCounts_AndTexts_English()
        {
            var m = Board();

            Assert.Equal("Needs your OK", m.Eyebrow);
            Assert.Equal("The write would reach", m.ReachTitle);
            Assert.Equal(235, m.TotalFixtures);
            Assert.Equal(10, m.TotalTypes);
            Assert.Equal("235 fixtures · 10 types", m.ReachText);
            Assert.Equal("Will set 8 parameters per type", m.ParamsTitle);
            Assert.Equal(8, m.Params.Count);
            Assert.Equal("Downlights · 2 types", m.Groups[0].Title);
            Assert.Equal("52 fixtures", m.Groups[0].CountText);
            Assert.Equal("Other lighting · 8 types", m.Groups[1].Title);
            Assert.Equal("183 fixtures", m.Groups[1].CountText);
            Assert.True(m.Groups[1].IsWide);
            Assert.Equal("Copilot asks only when a write is wider than the prompt reads, deletes elements, or runs code. Ordinary fills apply straight away with Undo.", m.Footnote);
        }

        [Fact]
        public void TypeRows_ShowTheFirstFive_ThenAndNMoreTypes()
        {
            var other = Board().Groups[1];

            Assert.Equal(5, other.Rows.Count);
            Assert.Equal(new[] { "a", "b", "c", "d", "e" }, other.Rows.Select(r => r.Name));
            Assert.Equal(3, other.MoreTypes);
            Assert.Equal(10, other.MoreFixtures);                  // 3 + 4 + 3
            Assert.Equal("and 3 more types", other.MoreText);

            var downlights = Board().Groups[0];
            Assert.Equal(2, downlights.Rows.Count);
            Assert.Equal("", downlights.MoreText);                 // every type is shown
        }

        [Fact]
        public void TheThreeButtons_KeepTheBoardsOrderRolesAndWords()
        {
            var b = Board().Buttons;

            Assert.Equal(new[] { WideRole.Primary, WideRole.Secondary, WideRole.Ghost }, b.Select(x => x.Role));
            Assert.Equal(new[] { WideChoice.Narrow, WideChoice.All, WideChoice.Cancel }, b.Select(x => x.Choice));
            Assert.Equal("Write to the 2 downlight types only", b[0].Label);
            Assert.Equal("52 fixtures · 16 values · matches the brochure", b[0].Detail);
            Assert.Equal("Write to all 10 types", b[1].Label);
            Assert.Equal("235 fixtures · 80 values", b[1].Detail);
            Assert.Equal("Cancel, write nothing", b[2].Label);
            Assert.Equal("", b[2].Detail);
        }

        [Fact]
        public void TheNarrowButton_DropsItsReason_WhenThereIsNone()
        {
            Assert.Equal("52 fixtures · 16 values", Board(reason: null).Buttons[0].Detail);
        }

        [Fact]
        public void Malay_HasEveryText()
        {
            var m = Board(ReceiptLanguage.Ms, "sepadan dengan brosur");

            Assert.Equal("Perlu persetujuan anda", m.Eyebrow);
            Assert.Equal("235 fixtur · 10 jenis", m.ReachText);
            Assert.Equal("Akan tetapkan 8 parameter bagi setiap jenis", m.ParamsTitle);
            Assert.Equal("Tulis ke 2 jenis downlight sahaja", m.Buttons[0].Label);
            Assert.Equal("52 fixtur · 16 nilai · sepadan dengan brosur", m.Buttons[0].Detail);
            Assert.Equal("Tulis ke semua 10 jenis", m.Buttons[1].Label);
            Assert.Equal("Batal, jangan tulis apa-apa", m.Buttons[2].Label);
            Assert.Equal("dan 3 jenis lagi", m.Groups[1].MoreText);
            Assert.Contains("Pengisian biasa", m.Footnote);
        }

        [Fact]
        public void WithNothingNarrower_OnlyAllAndCancelAreOffered_AllBecomesPrimary()
        {
            var m = WideWriteModel.From(ReceiptLanguage.En, "Sure?", null,
                new[] { new WideGroupInput { Label = "Doors", IsWide = true, Types = { T("D1", 4), T("D2", 2) } } },
                new[] { "Mark" }, "door");

            Assert.Equal(new[] { WideChoice.All, WideChoice.Cancel }, m.Buttons.Select(x => x.Choice));
            Assert.Equal(WideRole.Primary, m.Buttons[0].Role);
            Assert.Equal("Write to all 2 types", m.Buttons[0].Label);
            Assert.Equal("6 fixtures · 2 values", m.Buttons[0].Detail);
        }

        [Fact]
        public void EachButtonAnswersUnderItsOwnId_AndCancelAnswersNoWrite()
        {
            var m = WideWriteModel.From(ReceiptLanguage.En, "h", null,
                new[]
                {
                    new WideGroupInput { Label = "A", Types = { T("t1", 1) } },
                    new WideGroupInput { Label = "B", IsWide = true, Types = { T("t2", 2) } },
                },
                new[] { "P" }, "a", null, narrowId: "n1", allId: "a1", cancelId: "c1");

            Assert.Equal(new[] { "n1" }, m.AnswerFor(WideChoice.Narrow).Values.Keys);
            Assert.Equal(new[] { "a1" }, m.AnswerFor(WideChoice.All).Values.Keys);
            Assert.Equal(new[] { "c1" }, m.AnswerFor(WideChoice.Cancel).Values.Keys);
            Assert.True(m.AnswerFor(WideChoice.Cancel).Values["c1"].GetBoolean());
        }

        [Fact]
        public void EmptyOrBlankInput_DoesNotThrow_AndDrawsNoEmptyGroup()
        {
            var m = WideWriteModel.From(ReceiptLanguage.En, "h", null,
                new[] { new WideGroupInput { Label = "Empty" }, new WideGroupInput { Label = "Blank", Types = { T(" ", 3) } } },
                null, "x");

            Assert.Empty(m.Groups);
            Assert.Equal("", m.ParamsTitle);
            Assert.Equal(WideChoice.Cancel, m.Buttons.Last().Choice);
        }

        // ---- the server's gate: an ordinary ask event with options write_all / write_cancel ----

        private static List<AskOptionModel> Gate(string all = "Write to all (300 elements)", string cancel = "Cancel, write nothing") =>
            new List<AskOptionModel>
            {
                new AskOptionModel { Id = "write_all", Label = all },
                new AskOptionModel { Id = "write_cancel", Label = cancel },
            };

        [Fact]
        public void TheGate_WithGroups_English_ParsesHeadlineRowsAndMore()
        {
            var q = "Before I write: 2 changes reach 300 elements.\n• Doors: 150\n• Windows: 100\n• Walls: 20\n• Floors: 20\n• Roofs: 5\nand 3 more";

            var m = WideWriteModel.FromAsk(q, Gate(), ReceiptLanguage.En)!;

            Assert.Equal("Before I write: 2 changes reach 300 elements.", m.Headline);
            Assert.Equal("Needs your OK", m.Eyebrow);
            var g = Assert.Single(m.Groups);
            Assert.Equal(new[] { "Doors", "Windows", "Walls", "Floors", "Roofs" }, g.Rows.Select(r => r.Name));
            Assert.Equal(new[] { 150, 100, 20, 20, 5 }, g.Rows.Select(r => r.Count));
            Assert.Equal(3, g.MoreTypes);
            Assert.Equal("and 3 more", g.MoreText);
            Assert.Equal(-1, g.MoreFixtures);                       // the server gives no count for the rest
            Assert.Equal("", m.Detail);
        }

        [Fact]
        public void TheGate_Malay_UsesDanKLagi()
        {
            var q = "Sebelum saya tulis: Comments = WIDE-TEST pada 150 elemen.\n• Pintu: 100\n• Tingkap: 50\ndan 4 lagi";

            var m = WideWriteModel.FromAsk(q, Gate("Tulis ke semua (150 elemen)", "Batal, jangan tulis apa-apa"), ReceiptLanguage.Ms)!;

            Assert.Equal("Sebelum saya tulis: Comments = WIDE-TEST pada 150 elemen.", m.Headline);
            Assert.Equal("Perlu persetujuan anda", m.Eyebrow);
            Assert.Equal(new[] { "Pintu", "Tingkap" }, m.Groups[0].Rows.Select(r => r.Name));
            Assert.Equal(4, m.Groups[0].MoreTypes);
            Assert.Equal("dan 4 lagi", m.Groups[0].MoreText);
            Assert.Equal("Tulis ke semua (150 elemen)", m.Buttons[0].Label);
        }

        [Fact]
        public void TheGate_WithoutBullets_IsJustTheHeadline_AndTwoButtons()
        {
            var m = WideWriteModel.FromAsk("Before I write: Comments = WIDE-TEST on 150 elements.", Gate("Write to all (150 elements)"), ReceiptLanguage.En)!;

            Assert.Equal("Before I write: Comments = WIDE-TEST on 150 elements.", m.Headline);
            Assert.Empty(m.Groups);                                 // no reach card to draw
            Assert.Equal(new[] { WideChoice.All, WideChoice.Cancel }, m.Buttons.Select(b => b.Choice));
            Assert.Equal(new[] { WideRole.Primary, WideRole.Ghost }, m.Buttons.Select(b => b.Role));
            Assert.Equal(new[] { "write_all", "write_cancel" }, m.Buttons.Select(b => b.Id));
            Assert.Equal("Write to all (150 elements)", m.Buttons[0].Label);
            Assert.Equal("Cancel, write nothing", m.Buttons[1].Label);
        }

        [Fact]
        public void TheGate_WhenTheCountIsUnknown_KeepsTheRunItLabel()
        {
            var m = WideWriteModel.FromAsk("Before I run code that deletes 12 elements.", Gate("Run it"), ReceiptLanguage.En)!;

            Assert.Equal("Run it", m.Buttons[0].Label);
            Assert.Equal("Before I run code that deletes 12 elements.", m.Headline);
        }

        [Fact]
        public void TheGate_KeepsRoomForAWriteNarrowOption_AsThePrimary()
        {
            var opts = Gate();
            opts.Insert(0, new AskOptionModel { Id = "write_narrow", Label = "Write to the 2 downlight types only" });

            var m = WideWriteModel.FromAsk("Before I write: this is wide.", opts, ReceiptLanguage.En)!;

            Assert.Equal(new[] { WideRole.Primary, WideRole.Secondary, WideRole.Ghost }, m.Buttons.Select(b => b.Role));
            Assert.Equal(new[] { "write_narrow", "write_all", "write_cancel" }, m.Buttons.Select(b => b.Id));
        }

        [Fact]
        public void NotTheGate_WhenEitherIdIsMissing()
        {
            Assert.Null(WideWriteModel.FromAsk("q", new List<AskOptionModel> { new AskOptionModel { Id = "write_all", Label = "Write to all" } }, ReceiptLanguage.En));
            Assert.Null(WideWriteModel.FromAsk("q", new List<AskOptionModel> { new AskOptionModel { Id = "a", Label = "A" }, new AskOptionModel { Id = "b", Label = "B" } }, ReceiptLanguage.En));
        }

        private static BinaVibe.Copilot.Contracts.AskDto GateAsk(string question, string all, string cancel) => new BinaVibe.Copilot.Contracts.AskDto
        {
            RequestId = "r", Question = question, Purpose = BinaVibe.Copilot.Contracts.AskPurposes.Clarify,
            Options = new List<BinaVibe.Copilot.Contracts.AskOptionDto>
            {
                new BinaVibe.Copilot.Contracts.AskOptionDto { Id = "write_all", Label = all },
                new BinaVibe.Copilot.Contracts.AskOptionDto { Id = "write_cancel", Label = cancel },
            },
        };

        [Fact]
        public void TheAskModel_KeepsTheGatesTwoOptions_EvenWhenTheLabelReadsLikeApproval()
        {
            // Malay "Jalankan" and English "Apply" are approval words the filter drops everywhere else.
            var card = AskCardModel.From(GateAsk("Sebelum saya jalankan kod.", "Jalankan", "Batal, jangan tulis"), ReceiptLanguage.Ms);

            Assert.Equal(new[] { "write_all", "write_cancel" }, card.Options.Select(o => o.Id));
            Assert.NotNull(card.Wide);
            Assert.False(card.IsChoiceLook);                        // the full card, not the generic choice look
            var yes = card.AnswerForOptions(new[] { "write_all" });
            Assert.True(yes.Values["write_all"].GetBoolean());
            var no = card.AnswerForOptions(new[] { "write_cancel" });
            Assert.True(no.Values["write_cancel"].GetBoolean());
        }

        [Fact]
        public void TheExemption_IsOnlyForTheGatesTwoIds_TogetherAndNotForOtherOptions()
        {
            // write_all without write_cancel is not the gate: the approval filter still drops it.
            var lone = AskCardModel.From(new BinaVibe.Copilot.Contracts.AskDto
            {
                RequestId = "r", Question = "Which?", Purpose = BinaVibe.Copilot.Contracts.AskPurposes.Clarify,
                Options = new List<BinaVibe.Copilot.Contracts.AskOptionDto>
                {
                    new BinaVibe.Copilot.Contracts.AskOptionDto { Id = "write_all", Label = "Yes, apply" },
                    new BinaVibe.Copilot.Contracts.AskOptionDto { Id = "l2", Label = "Level 2" },
                },
            }, ReceiptLanguage.En);
            Assert.Empty(lone.Options);
            Assert.Null(lone.Wide);

            // With the gate present, an extra approval-like option under another id is still dropped.
            var ask = GateAsk("Before I write.", "Write to all (3 elements)", "Cancel, write nothing");
            ask.Options!.Add(new BinaVibe.Copilot.Contracts.AskOptionDto { Id = "other", Label = "Approve everything" });
            var card = AskCardModel.From(ask, ReceiptLanguage.En);
            Assert.Equal(new[] { "write_all", "write_cancel" }, card.Options.Select(o => o.Id));
            Assert.True(card.SomethingWasDropped);
        }

        [Fact]
        public void TheCredentialRule_StillWinsOverTheGate()
        {
            var card = AskCardModel.From(GateAsk("Paste your password first.", "Write to all", "Cancel"), ReceiptLanguage.En);

            Assert.True(card.AsksForCredential);
            Assert.Null(card.Wide);
            Assert.Empty(card.Options);
        }

        [Fact]
        public void TheAskCard_RendersTheWideCardForTheGate()
        {
            var s = Src("UI/Copilot/Controls/AskCard.cs");

            Assert.Contains("m.Wide != null", s);
            Assert.Contains("WideWriteCard.Build(m.Wide,", s);
        }

        // ---- the view draws the board's numbers, from tokens only ----

        [Fact]
        public void TheView_KeepsTheBoardsSizes_AndTokens()
        {
            var s = Src("UI/Copilot/Controls/WideWriteCard.cs");

            Assert.Contains("Height = 24", s);                       // eyebrow chip
            Assert.Contains("new CornerRadius(12)", s);
            Assert.Contains("CardKit.Text(m.Headline, 17", s);       // the sentence
            Assert.Contains("Height = 22", s);                       // parameter chips
            Assert.Contains("new CornerRadius(5)", s);
            Assert.Contains("hasDetail ? 56 : 44", s);               // two-line button 56, else 44 (ghost 44)
            Assert.Contains("new CornerRadius(10)", s);              // the board's button radius
            Assert.Contains("new Thickness(14, 9, 14, 9)", s);
            Assert.Contains("CardKit.Primary(", s);
            Assert.Contains("CardKit.Secondary(", s);
            Assert.Contains("CardKit.Ghost(", s);
            foreach (var token in new[] { "Cp.Bg", "Cp.Line", "Cp.Raised", "Cp.Sunken", "Cp.AmberBg", "Cp.AmberFg", "Cp.Green", "Cp.TextSecondary", "Cp.Faint", "Cp.Muted", "Cp.AccentContrast" })
                Assert.Contains("\"" + token + "\"", s);
            Assert.DoesNotContain("Storyboard", s);
            Assert.DoesNotContain("FlatButton", s);
            Assert.DoesNotContain("CardKit.Chip(", s);
        }

        [Fact]
        public void TheAskCard_UsesTheNeedsYourOkLook_ForTwoOrThreeOptionsOnly()
        {
            var s = Src("UI/Copilot/Controls/AskCard.cs");

            Assert.Contains("m.IsChoiceLook) return BuildChoiceLook(", s);
            Assert.Contains("AskCardModel.IsDeclineLike(o.Label) ? WideRole.Ghost", s);
            Assert.Contains("i == 0 ? WideRole.Primary", s);
            Assert.Contains("WideWriteModel.FootnoteText(ms)", s);
        }
    }
}
