#nullable enable
// The decision card's logic (card spec of 12 Nov 2026, approved mockup): field shapes, "N left", the outcome
// lines, the button label, bulk create/skip, keys, the answer payload and the collapsed record. Revit-free and
// WPF-free; AskCard only draws what these return.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskCardDecisionTests
    {
        private static AskOptionDto Opt(string id, string label, string? kind = null, string? meta = null) =>
            new AskOptionDto { Id = id, Label = label, Kind = kind, Meta = meta };

        /// <summary>The sheets question of live round 3, in the new frame: one level with two plans, two with none.</summary>
        private static AskDto SheetsAsk(bool withNoun = true) => new AskDto
        {
            RequestId = "req_s", Question = "3 of 6 levels need a decision.", Purpose = AskPurposes.Clarify,
            Title = "Before I make the sheets", Noun = withNoun ? "sheet" : null,
            Ready = new List<AskReadyDto>
            {
                new AskReadyDto { Label = "Aras 01", Detail = "Aras 01 (07d_Dokumen)" },
                new AskReadyDto { Label = "Aras 02" },
                new AskReadyDto { Label = "Aras Bumbung" },
            },
            Fields = new List<AskFieldDto>
            {
                new AskFieldDto { Id = "tanah", Label = "Aras Tanah", Type = AskFieldTypes.Choice, Group = "Choose a plan", Note = "2 floor plans",
                    Choices = new List<AskOptionDto>
                    {
                        Opt("Pelan Tapak", "Pelan Tapak", meta: "1:200"),
                        Opt("Aras Tanah (01w_WIP)", "Aras Tanah (01w_WIP)", meta: "1:100"),
                        Opt("Skip Aras Tanah", "Skip this level", AskOptionKinds.Skip),
                    } },
                new AskFieldDto { Id = "pentas", Label = "Aras Pentas", Type = AskFieldTypes.Choice, Group = "No floor plan",
                    Choices = new List<AskOptionDto> { Opt("Create a FloorPlan for Aras Pentas", "Create plan", AskOptionKinds.Create), Opt("Skip Aras Pentas", "Skip", AskOptionKinds.Skip) } },
                new AskFieldDto { Id = "langkan", Label = "Aras Langkan", Type = AskFieldTypes.Choice, Group = "No floor plan",
                    Choices = new List<AskOptionDto> { Opt("Create a FloorPlan for Aras Langkan", "Create plan", AskOptionKinds.Create), Opt("Skip Aras Langkan", "Skip", AskOptionKinds.Skip) } },
            },
        };

        private static AskCardModel Card(AskDto ask) => AskCardModel.From(ask, ReceiptLanguage.En);

        // ─── The frame's additions ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheFramesOptionalAdditions_ReachTheModel()
        {
            var card = Card(SheetsAsk());

            Assert.Equal("Before I make the sheets", card.HeaderTitle);
            Assert.Equal("sheet", card.Noun);
            Assert.Equal(new[] { "Aras 01", "Aras 02", "Aras Bumbung" }, card.Ready.Select(r => r.Label));
            Assert.Equal("Aras 01 (07d_Dokumen)", card.Ready[0].Detail);
            Assert.Equal("Choose a plan", card.Fields[0].Group);
            Assert.Equal("2 floor plans", card.Fields[0].Note);
            Assert.Equal("1:200", card.Fields[0].Choices[0].Meta);
            Assert.Equal(AskOptionKinds.Skip, card.Fields[0].Choices[2].Kind);
            Assert.True(card.Fields[1].Choices[0].IsCreate);
        }

        [Fact]
        public void AFrameWithoutThem_StillGivesACard()
        {
            var card = Card(new AskDto
            {
                RequestId = "r", Question = "Which level?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "lvl", Label = "Level", Type = AskFieldTypes.Choice,
                        Choices = new List<AskOptionDto> { Opt("l1", "Level 1"), Opt("l2", "Level 2") } },
                },
            });

            Assert.Equal("Which level?", card.HeaderTitle);         // no filler heading: the question is the heading
            Assert.Null(card.HeaderBody);
            Assert.Null(card.Noun);
            Assert.Empty(card.Ready);
            Assert.Null(card.Fields[0].Group);
            Assert.Equal(AskFieldShape.Tiles, AskCardModel.ShapeOf(card.Fields[0]));
            Assert.False(card.ShowsPreview);                       // nothing to make, no noun: no "What you will get"
            Assert.Equal("Answer 1 more", card.FooterLabel);
            card.Pick("lvl", "l2");
            Assert.Equal("Continue", card.FooterLabel);            // no noun
            Assert.True(card.FooterEnabled);
        }

        [Fact]
        public void AnUnknownKind_IsAnOrdinaryPick()
        {
            var card = Card(new AskDto
            {
                RequestId = "r", Question = "q", Purpose = AskPurposes.Clarify,
                Options = new List<AskOptionDto> { Opt("a", "Level 1", "delete"), Opt("b", "Level 2") },
            });
            Assert.Null(card.Options[0].Kind);
        }

        [Fact]
        public void TheNewProperties_AreOptionalOnTheWire()
        {
            var json = "{\"seq\":3,\"request_id\":\"r\",\"question\":\"q\",\"multi\":false,\"purpose\":\"clarify\","
                + "\"options\":[{\"id\":\"a\",\"label\":\"A\"},{\"id\":\"b\",\"label\":\"B\"}]}";
            var dto = CopilotJson.Deserialize<AskDto>(json)!;
            var again = CopilotJson.Serialize(dto);
            Assert.DoesNotContain("\"title\"", again);
            Assert.DoesNotContain("\"meta\"", again);
            Assert.DoesNotContain("\"kind\"", again);
            Assert.DoesNotContain("\"ready\"", again);

            var full = "{\"seq\":4,\"request_id\":\"r\",\"question\":\"q\",\"multi\":false,\"purpose\":\"clarify\",\"title\":\"T\",\"noun\":\"sheet\","
                + "\"ready\":[{\"label\":\"Aras 01\",\"detail\":\"Aras 01 - Dokumen\"}],"
                + "\"fields\":[{\"id\":\"f\",\"label\":\"Aras Pentas\",\"type\":\"choice\",\"group\":\"No floor plan\",\"note\":\"n\","
                + "\"choices\":[{\"id\":\"Create a FloorPlan for Aras Pentas\",\"label\":\"Create plan\",\"kind\":\"create\",\"meta\":\"1:100\"},{\"id\":\"Skip Aras Pentas\",\"label\":\"Skip\",\"kind\":\"skip\"}]}]}";
            var read = CopilotJson.Deserialize<AskDto>(full)!;
            Assert.Equal("T", read.Title);
            Assert.Equal("Aras 01 - Dokumen", read.Ready![0].Detail);
            Assert.Equal("No floor plan", read.Fields![0].Group);
            Assert.Equal("create", read.Fields[0].Choices![0].Kind);
            Assert.Equal("1:100", read.Fields[0].Choices![0].Meta);
        }

        // ─── Shapes ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Shapes_SwitchTilesListTextNumber()
        {
            var card = Card(SheetsAsk());
            Assert.Equal(AskFieldShape.Tiles, AskCardModel.ShapeOf(card.Fields[0]));     // 2 plans + a skip
            Assert.Equal(AskFieldShape.Switch, AskCardModel.ShapeOf(card.Fields[1]));    // one create, one skip

            var many = new AskFieldModel { Id = "tb", Label = "Title block", Type = AskFieldTypes.Choice,
                Choices = Enumerable.Range(1, 6).Select(i => new AskOptionModel { Id = "t" + i, Label = "TB " + i }).ToList() };
            Assert.Equal(AskFieldShape.List, AskCardModel.ShapeOf(many));
            many.Choices = many.Choices.Take(4).Concat(new[] { new AskOptionModel { Id = "s", Label = "Skip", Kind = AskOptionKinds.Skip } }).ToList();
            Assert.Equal(AskFieldShape.Tiles, AskCardModel.ShapeOf(many));              // 4 picks + skip: still tiles

            // Two ordinary choices are tiles, not a switch; a create and a skip plus a third choice is tiles too.
            var two = new AskFieldModel { Type = AskFieldTypes.Choice, Choices = { new AskOptionModel { Id = "a" }, new AskOptionModel { Id = "b" } } };
            Assert.Equal(AskFieldShape.Tiles, AskCardModel.ShapeOf(two));
            Assert.Equal(AskFieldShape.Number, AskCardModel.ShapeOf(new AskFieldModel { Type = AskFieldTypes.Number }));
            Assert.Equal(AskFieldShape.Text, AskCardModel.ShapeOf(new AskFieldModel { Type = AskFieldTypes.Text }));
        }

        [Fact]
        public void Filter_MatchesLabelOrMeta_AndSkipIsNotARow()
        {
            var f = new AskFieldModel { Type = AskFieldTypes.Choice, Choices =
            {
                new AskOptionModel { Id = "1", Label = "(i205a) A1 Landscape", Meta = "A1" },
                new AskOptionModel { Id = "2", Label = "(i210a) A3 Landscape", Meta = "A3" },
                new AskOptionModel { Id = "3", Label = "(03a) A2 Portrait", Meta = "A2" },
                new AskOptionModel { Id = "s", Label = "Skip", Kind = AskOptionKinds.Skip },
            } };
            Assert.Equal(3, AskCardModel.Filter(f, "").Count);
            Assert.Equal(new[] { "1", "2" }, AskCardModel.Filter(f, "landscape").Select(c => c.Id));
            Assert.Equal(new[] { "2" }, AskCardModel.Filter(f, "a3").Select(c => c.Id));
            Assert.Empty(AskCardModel.Filter(f, "skip"));
        }

        // ─── Groups and counts ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Groups_KeepFirstAppearanceOrder_AndCountWhatIsLeft()
        {
            var card = Card(SheetsAsk());
            var groups = card.Groups();
            Assert.Equal(new[] { "Choose a plan", "No floor plan" }, groups.Select(g => g.Heading));
            Assert.Equal(2, groups[1].Fields.Count);
            Assert.Equal("2 left", AskCardModel.CountLabel(card.LeftIn(groups[1])));

            card.Pick("pentas", "Skip Aras Pentas");
            Assert.Equal("1 left", AskCardModel.CountLabel(card.LeftIn(groups[1])));
            card.Pick("langkan", "Create a FloorPlan for Aras Langkan");
            Assert.Equal("done", AskCardModel.CountLabel(card.LeftIn(groups[1])));
            Assert.Equal(new[] { false, true, true }, card.Segments);
        }

        [Fact]
        public void APickThatIsNotAChoice_DoesNotCount()
        {
            var card = Card(SheetsAsk());
            card.Pick("pentas", "Skip Aras Langkan");   // another field's id
            Assert.False(card.IsAnswered(card.Fields[1]));
            Assert.Equal(3, card.Unanswered);
        }

        // ─── Bulk ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Bulk_OnlyWithTwoOrMoreSwitchRows_AndOverwritesEveryRow()
        {
            var card = Card(SheetsAsk());
            var groups = card.Groups();
            Assert.False(card.CanBulk(groups[0]));
            Assert.True(card.CanBulk(groups[1]));

            card.Pick("pentas", "Create a FloorPlan for Aras Pentas");
            card.Bulk(groups[1], AskOptionKinds.Skip);
            Assert.Equal("Skip Aras Pentas", card.Picks["pentas"]);
            Assert.Equal("Skip Aras Langkan", card.Picks["langkan"]);
            Assert.False(card.Picks.ContainsKey("tanah"));                     // another group is untouched

            card.Bulk(groups[1], AskOptionKinds.Create);
            Assert.Equal("Create a FloorPlan for Aras Langkan", card.Picks["langkan"]);
            Assert.Equal(0, card.Active);                                      // the keyboard goes to the open field
        }

        // ─── Outcome and the button ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void Outcome_ReadyThenFields_WaitingSkippedNew()
        {
            var card = Card(SheetsAsk());
            var open = card.Outcome();
            Assert.Equal(6, open.Count);
            Assert.All(open.Take(3), l => Assert.Equal(AskOutcomeState.Ready, l.State));
            Assert.All(open.Skip(3), l => Assert.Equal(AskOutcomeState.Waiting, l.State));
            Assert.Equal(AskCardModel.WaitingText, open[3].Detail);

            card.Pick("tanah", "Pelan Tapak");
            card.Pick("pentas", "Create a FloorPlan for Aras Pentas");
            card.Pick("langkan", "Skip Aras Langkan");
            var done = card.Outcome();
            Assert.Equal(AskOutcomeState.Picked, done[3].State);
            Assert.Equal("Pelan Tapak", done[3].Detail);
            Assert.Equal(AskOutcomeState.New, done[4].State);
            Assert.Equal("New plan", done[4].Detail);              // what it makes, not the button's "Create plan"
            Assert.Equal(AskOutcomeState.Skipped, done[5].State);
            Assert.Equal("Aras Langkan", done[5].Label);
        }

        [Fact]
        public void Button_AnswerKMore_ThenMakeNSheets()
        {
            var card = Card(SheetsAsk());
            Assert.Equal("Answer 3 more", card.FooterLabel);
            Assert.False(card.FooterEnabled);
            Assert.Equal("3 sheets so far", card.PreviewCount);

            card.Pick("tanah", "Pelan Tapak");
            card.Pick("pentas", "Create a FloorPlan for Aras Pentas");
            Assert.Equal("Answer 1 more", card.FooterLabel);
            card.Pick("langkan", "Skip Aras Langkan");
            Assert.True(card.FooterEnabled);
            Assert.Equal("Make 5 sheets", card.FooterLabel);                   // 3 ready + plan + new plan; the skip makes none
            Assert.Equal("5 sheets", card.PreviewCount);
        }

        [Fact]
        public void Button_Singular_Nothing_AndContinueWithoutANoun()
        {
            var one = SheetsAsk();
            one.Ready = new List<AskReadyDto>();                               // an empty ready list: the total is known
            var card = Card(one);
            card.Pick("tanah", "Skip Aras Tanah");
            card.Pick("pentas", "Skip Aras Pentas");
            card.Pick("langkan", "Skip Aras Langkan");
            Assert.Equal("Make nothing", card.FooterLabel);
            Assert.True(card.FooterEnabled);                                   // skipping everything is an answer
            card.Pick("pentas", "Create a FloorPlan for Aras Pentas");
            Assert.Equal("Make 1 sheet", card.FooterLabel);

            var noNoun = Card(SheetsAsk(withNoun: false));
            noNoun.Bulk(noNoun.Groups()[1], AskOptionKinds.Skip);
            noNoun.Pick("tanah", "Pelan Tapak");
            Assert.Equal("Continue", noNoun.FooterLabel);
            Assert.Null(noNoun.PreviewCount);
        }

        [Theory]
        [InlineData("sheet", "sheets")]
        [InlineData("view", "views")]
        [InlineData("box", "boxes")]
        [InlineData("schedule", "schedules")]
        [InlineData("family", "families")]
        [InlineData("key", "keys")]
        public void Plural(string noun, string plural) => Assert.Equal(plural, AskCardModel.Plural(noun));

        [Fact]
        public void OtherKinds_ButtonStates()
        {
            var free = Card(new AskDto { RequestId = "r", Question = "Which room?", Purpose = AskPurposes.Clarify });
            Assert.Equal(AskCardKind.FreeText, free.Kind);
            Assert.False(free.FooterEnabled);
            free.FreeTextDraft = "the lobby";
            Assert.True(free.FooterEnabled);
            Assert.Equal("Continue", free.FooterLabel);

            var multi = Card(new AskDto { RequestId = "r", Question = "Which?", Purpose = AskPurposes.Clarify, Multi = true,
                Options = new List<AskOptionDto> { Opt("a", "Level 1"), Opt("b", "Level 2") } });
            Assert.False(multi.FooterEnabled);
            multi.ToggleMulti("b");
            Assert.True(multi.FooterEnabled);
            multi.ToggleMulti("b");
            Assert.False(multi.FooterEnabled);
        }

        // ─── Keys ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Keys_NumberCreateSkip()
        {
            var card = Card(SheetsAsk());
            var tanah = card.Fields[0];
            Assert.Equal("Pelan Tapak", AskCardModel.ChoiceForKey(tanah, '1')!.Id);
            Assert.Equal("Aras Tanah (01w_WIP)", AskCardModel.ChoiceForKey(tanah, '2')!.Id);
            Assert.Null(AskCardModel.ChoiceForKey(tanah, '3'));                      // the skip is not a numbered tile
            Assert.Equal("Skip Aras Tanah", AskCardModel.ChoiceForKey(tanah, 'S')!.Id);
            Assert.Null(AskCardModel.ChoiceForKey(tanah, 'c'));                      // no create choice here
            Assert.Equal("Create a FloorPlan for Aras Pentas", AskCardModel.ChoiceForKey(card.Fields[1], 'c')!.Id);
            Assert.Null(AskCardModel.ChoiceForKey(new AskFieldModel { Type = AskFieldTypes.Text }, '1'));

            // In a filtered list the number is the Nth row shown.
            var shown = new[] { tanah.Choices[1] };
            Assert.Equal("Aras Tanah (01w_WIP)", AskCardModel.ChoiceForKey(tanah, '1', shown)!.Id);
        }

        [Fact]
        public void Keys_SingleQuestion()
        {
            var card = Card(new AskDto
            {
                RequestId = "r", Question = "Aras Bumbung has no ceiling plan", Purpose = AskPurposes.Clarify,
                Options = new List<AskOptionDto>
                {
                    Opt("Create a CeilingPlan for Aras Bumbung", "Create a ceiling plan, then the sheet", AskOptionKinds.Create, "1 new view · 1 sheet"),
                    Opt("Skip Aras Bumbung", "Skip it", AskOptionKinds.Skip, "Nothing is made"),
                },
            });
            Assert.Equal(AskCardKind.Options, card.Kind);
            Assert.Equal("C", AskCardModel.OptionKeyHint(card.Options[0], 0));
            Assert.Equal("S", AskCardModel.OptionKeyHint(card.Options[1], 1));
            Assert.Equal("Skip Aras Bumbung", card.OptionForKey('s')!.Id);
            Assert.Equal("Create a CeilingPlan for Aras Bumbung", card.OptionForKey('1')!.Id);
            Assert.Null(card.OptionForKey('3'));
            Assert.Equal("1 new view · 1 sheet", card.Options[0].Meta);
            Assert.Equal("2", AskCardModel.OptionKeyHint(new AskOptionModel(), 1));
        }

        [Fact]
        public void UpDown_Wrap_AndAPickMovesToTheNextOpenField()
        {
            var card = Card(SheetsAsk());
            Assert.Equal(0, card.Active);
            card.MoveActive(-1);
            Assert.Equal(2, card.Active);
            card.MoveActive(1);
            Assert.Equal(0, card.Active);

            card.Pick("tanah", "Pelan Tapak");
            Assert.Equal(1, card.Active);
            card.Pick("langkan", "Skip Aras Langkan");
            Assert.Equal(1, card.Active);                                      // pentas is still open
            Assert.Same(card.Fields[1], card.ActiveField);
        }

        // ─── Answer and record ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TheAnswer_IsFieldIdsToChoiceIds_TheLongCanonicalTexts()
        {
            var card = Card(SheetsAsk());
            card.Pick("tanah", "Pelan Tapak");
            card.Pick("pentas", "Create a FloorPlan for Aras Pentas");
            card.Pick("langkan", "Skip Aras Langkan");

            var answer = card.AnswerForDraft();
            Assert.Equal(new[] { "tanah", "pentas", "langkan" }, answer.Values.Keys);
            Assert.Equal("Pelan Tapak", answer.Values["tanah"].GetString());
            Assert.Equal("Create a FloorPlan for Aras Pentas", answer.Values["pentas"].GetString());
            Assert.Equal("Skip Aras Langkan", answer.Values["langkan"].GetString());   // the id, never the short label "Skip"
        }

        [Fact]
        public void TheCollapsedRecord_OneLinePerField()
        {
            var card = Card(SheetsAsk());
            card.Pick("tanah", "Pelan Tapak");
            card.Pick("pentas", "Create a FloorPlan for Aras Pentas");
            card.Pick("langkan", "Skip Aras Langkan");

            var lines = card.LinesForDraft();
            Assert.Equal(new[] { "Aras Tanah", "Aras Pentas", "Aras Langkan" }, lines.Select(l => l.Label));
            Assert.Equal(new[] { "Pelan Tapak", "Create plan", AskCardModel.SkippedText }, lines.Select(l => l.Pick));
            Assert.Equal("Aras Tanah: Pelan Tapak · Aras Pentas: Create plan · Aras Langkan: Skipped", card.SummaryForDraft());

            card.RecordLines(lines);
            Assert.Equal(3, card.AnsweredLines!.Count);
        }

        [Fact]
        public void TypedFields_AnswerAsBefore_NumbersAsNumbers()
        {
            var card = Card(new AskDto
            {
                RequestId = "r", Question = "Ceiling?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "h", Label = "Height", Type = AskFieldTypes.Number, Unit = "mm" },
                    new AskFieldDto { Id = "n", Label = "Name", Type = AskFieldTypes.Text },
                },
            });
            Assert.Equal("Continue", card.FooterLabel);             // text and number fields are never "left"
            Assert.False(card.FooterEnabled);                        // but there is nothing to send yet
            card.SetText("h", "2,700");
            card.SetText("n", "  Office  ");
            Assert.True(card.FooterEnabled);
            var answer = card.AnswerForDraft();
            Assert.Equal(JsonValueKind.Number, answer.Values["h"].ValueKind);
            Assert.Equal(2700, answer.Values["h"].GetDouble());
            Assert.Equal("Office", answer.Values["n"].GetString());
            Assert.Equal("Height: 2,700 mm · Name: Office", card.SummaryForDraft());
            Assert.Equal(AskOutcomeState.Picked, card.Outcome()[0].State);
        }

        [Fact]
        public void TheGuardsStillApply_WithTheNewProperties()
        {
            var ask = SheetsAsk();
            ask.Fields!.Add(new AskFieldDto { Id = "pw", Label = "Your BINA password", Type = AskFieldTypes.Text, Group = "No floor plan" });
            ask.Fields[0].Choices!.Add(Opt("ok", "OK, proceed"));
            var card = Card(ask);
            Assert.DoesNotContain(card.Fields, f => f.Id == "pw");
            Assert.DoesNotContain(card.Fields[0].Choices, c => c.Id == "ok");
            Assert.True(card.SomethingWasDropped);
        }

        [Fact]
        public void Kinds()
        {
            Assert.Equal(AskCardKind.Credential, Card(new AskDto { RequestId = "r", Question = "Paste your API key", Purpose = AskPurposes.Clarify }).Kind);
            Assert.Equal(AskCardKind.ProjectNote, Card(new AskDto { RequestId = "r", Question = "Save?", Purpose = AskPurposes.ProjectNote, NoteText = "x" }).Kind);
            Assert.Equal(AskCardKind.Fields, Card(SheetsAsk()).Kind);
            Assert.Equal(AskCardKind.FreeText, Card(new AskDto { RequestId = "r", Question = "Which room?", Purpose = AskPurposes.Clarify }).Kind);
        }

        // ─── Round 1: when the card can be sent ───────────────────────────────────────────────────────────

        /// <summary>The backend's levels question: one choice field, or type several names.</summary>
        private static AskCardModel LevelsCard() => Card(new AskDto
        {
            RequestId = "req_03", Question = "Which levels should get a sheet?", Purpose = AskPurposes.Clarify, Title = "Before I make the sheets",
            Fields = new List<AskFieldDto>
            {
                new AskFieldDto { Id = "levels", Label = "One level, or all", Type = AskFieldTypes.Choice,
                    Choices = new[] { "All levels", "Aras Dasar", "Aras 1", "Aras Selang", "Aras Pagar", "Aras 2", "Aras Atap" }.Select(x => Opt(x, x)).ToList() },
                new AskFieldDto { Id = "several", Label = "Or type several levels", Type = AskFieldTypes.Text },
            },
        });

        [Fact]
        public void ChoicePicked_TextEmpty_CanSend()
        {
            var card = LevelsCard();
            card.Pick("levels", "All levels");
            Assert.True(card.CanSend);
            Assert.True(card.FooterEnabled);
            Assert.Equal("Continue", card.FooterLabel);
            Assert.Equal(new[] { "levels" }, card.AnswerForDraft().Values.Keys);   // the empty box sends nothing
        }

        [Fact]
        public void TextTyped_ChoiceUnpicked_CanSend()
        {
            var card = LevelsCard();
            card.SetText("several", "Aras 1, Aras 2");
            Assert.True(card.CanSend);
            Assert.Equal("Continue", card.FooterLabel);
            Assert.Equal("Aras 1, Aras 2", card.AnswerForDraft().Values["several"].GetString());
            Assert.False(card.AnswerForDraft().Values.ContainsKey("levels"));
        }

        [Fact]
        public void Nothing_IsBlocked_KCountsChoiceFieldsOnly()
        {
            var card = LevelsCard();
            Assert.False(card.CanSend);
            Assert.Equal("Answer 1 more", card.FooterLabel);                     // the text box is never "left"
            Assert.Equal("1 left", AskCardModel.CountLabel(card.LeftIn(card.Groups()[0])));
            card.SetText("several", "   ");
            Assert.False(card.CanSend);                                          // whitespace is not text

            // A form with only text and number fields: never "Answer K more"; blocked until something is typed.
            var typed = Card(new AskDto { RequestId = "r", Question = "Ceiling?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto> { new AskFieldDto { Id = "h", Label = "Height", Type = AskFieldTypes.Number } } });
            Assert.False(typed.FooterEnabled);
            Assert.Equal("Continue", typed.FooterLabel);
            Assert.False(AskCardModel.CountsIn(typed.Groups()[0]));
            typed.SetText("h", "2700");
            Assert.True(typed.FooterEnabled);
        }

        [Fact]
        public void TheThreeLevelCard_IsUnchanged()
        {
            var card = Card(SheetsAsk());
            Assert.Equal("Answer 3 more", card.FooterLabel);
            Assert.False(card.CanSend);
            card.Pick("tanah", "Pelan Tapak");
            card.Pick("pentas", "Create a FloorPlan for Aras Pentas");
            Assert.Equal("Answer 1 more", card.FooterLabel);
            Assert.False(card.FooterEnabled);
            card.Pick("langkan", "Skip Aras Langkan");
            Assert.Equal("Make 5 sheets", card.FooterLabel);
            Assert.True(card.FooterEnabled);
        }

        // ─── Round 1: long cards ──────────────────────────────────────────────────────────────────────────

        private static AskCardModel ManyLevels(int rows, int ready)
        {
            var ask = new AskDto
            {
                RequestId = "r", Question = "12 of 30 levels have no floor plan.", Purpose = AskPurposes.Clarify, Title = "Before I make the sheets", Noun = "sheet",
                Ready = Enumerable.Range(1, ready).Select(i => new AskReadyDto { Label = "L" + i }).ToList(),
                Fields = Enumerable.Range(1, rows).Select(i => new AskFieldDto
                {
                    Id = "B" + i, Label = "B" + i, Type = AskFieldTypes.Choice, Group = "No floor plan",
                    Choices = new List<AskOptionDto> { Opt("Create a FloorPlan for B" + i, "Create plan", AskOptionKinds.Create), Opt("Skip B" + i, "Skip", AskOptionKinds.Skip) },
                }).ToList(),
            };
            return Card(ask);
        }

        [Fact]
        public void AGroupShowsFiveRows_BulkStillActsOnAll()
        {
            var card = ManyLevels(12, 18);
            var g = card.Groups()[0];
            Assert.Equal(7, AskCardModel.HiddenIn(g));
            Assert.Equal(0, AskCardModel.HiddenIn(Card(SheetsAsk()).Groups()[1]));
            Assert.Equal(0, AskCardModel.HiddenIn(ManyLevels(5, 0).Groups()[0]));
            Assert.Equal(1, AskCardModel.HiddenIn(ManyLevels(6, 0).Groups()[0]));

            card.Bulk(g, AskOptionKinds.Skip);
            Assert.All(card.Fields, f => Assert.True(card.Picked(f)!.IsSkip));   // hidden rows too
            Assert.Equal("Make 18 sheets", card.FooterLabel);
        }

        [Fact]
        public void ReadyPills_SixThenMore_AndTheHeaderCountsPastEight()
        {
            var card = ManyLevels(12, 18);
            Assert.Equal(12, card.HiddenReady);
            Assert.Equal(0, Card(SheetsAsk()).HiddenReady);
            Assert.True(card.ShowsProgressCount);
            Assert.Equal("0 / 12", card.ProgressText);
            card.Pick("B1", "Skip B1");
            card.Pick("B2", "Skip B2");
            card.Pick("B3", "Create a FloorPlan for B3");
            Assert.Equal("3 / 12", card.ProgressText);
            Assert.False(ManyLevels(8, 0).ShowsProgressCount);
            Assert.True(ManyLevels(9, 0).ShowsProgressCount);
            Assert.Single(LevelsCard().Segments);                                 // progress counts choice fields only
        }

        // ─── Round 1: heading and outcome wording ─────────────────────────────────────────────────────────

        [Fact]
        public void WithoutATitle_TheQuestionIsTheHeading()
        {
            Assert.Equal(("Which level?", (string?)null), AskCardModel.SplitQuestion("Which level?"));
            Assert.Equal(("Which level should the ceilings go on?", (string?)null), AskCardModel.SplitQuestion("Which **level** should the ceilings go on?"));
            var (h, rest) = AskCardModel.SplitQuestion("I found two ceiling types.\n\n- CL-02A\n- CL-20\n\nWhich one?");
            Assert.Equal("I found two ceiling types.", h);
            Assert.Equal("- CL-02A\n- CL-20\n\nWhich one?", rest);
            Assert.True(AskCardModel.NeedsMarkdown(rest));
            Assert.Equal("Pick a type:", AskCardModel.SplitQuestion("Pick a type:\n- A\n- B").Heading);
            Assert.Equal(("", (string?)null), AskCardModel.SplitQuestion("  "));
            Assert.False(AskCardModel.NeedsMarkdown("Nothing is made yet."));

            var note = Card(new AskDto { RequestId = "r", Question = "Save this as a project note?", Purpose = AskPurposes.ProjectNote, NoteText = "x" });
            Assert.Equal("Save this as a project note?", note.HeaderTitle);
            var titled = Card(SheetsAsk());
            Assert.Equal("Before I make the sheets", titled.HeaderTitle);
            Assert.Equal("3 of 6 levels need a decision.", titled.HeaderBody);
        }

        [Theory]
        [InlineData("Create plan", "New plan")]
        [InlineData("Create a ceiling plan", "New ceiling plan")]
        [InlineData("Create a ceiling plan, then the sheet", "New ceiling plan")]
        [InlineData("Create a FloorPlan for Aras Pentas", "New FloorPlan")]
        [InlineData("Create", "Create")]
        [InlineData("Make a plan", "Make a plan")]
        public void NewLabel(string label, string expected) => Assert.Equal(expected, AskCardModel.NewLabel(label));

        // ─── Round 2: the outcome list on long cards, expand and fold ─────────────────────────────────────

        [Theory]
        [InlineData(3, 3, false)]     // 4 or fewer ready items: one line each
        [InlineData(4, 4, false)]
        [InlineData(5, 1, true)]      // more: one summary line
        [InlineData(18, 1, true)]
        public void Outcome_ReadyLines(int ready, int readyLines, bool summed)
        {
            var card = ManyLevels(3, ready);
            var (lines, hidden) = card.OutcomeView(expanded: false);
            var r = lines.Where(l => l.State == AskOutcomeState.Ready).ToList();
            Assert.Equal(readyLines, r.Count);
            if (summed) Assert.Equal(ready + " sheets ready to make", r[0].Label);
            else Assert.Equal(card.Ready.Select(x => x.Label), r.Select(x => x.Label));
            Assert.Equal(0, hidden);
            Assert.Equal(3, lines.Count(l => l.State == AskOutcomeState.Waiting));
            Assert.Equal(ready + " sheets so far", card.PreviewCount);                 // the count is unchanged
        }

        [Fact]
        public void Outcome_ReadySummaryWithoutANoun()
        {
            var ask = SheetsAsk(withNoun: false);
            ask.Ready = Enumerable.Range(1, 7).Select(i => new AskReadyDto { Label = "L" + i }).ToList();
            Assert.Equal("7 ready", Card(ask).OutcomeView(false).Lines[0].Label);
        }

        [Theory]
        [InlineData(4, 4, 0)]
        [InlineData(7, 6, 1)]
        [InlineData(12, 6, 6)]
        public void Outcome_FieldLinesCapAtSix_ThenShowMore(int rows, int shown, int hidden)
        {
            var card = ManyLevels(rows, 18);
            var folded = card.OutcomeView(expanded: false);
            Assert.Equal(1 + shown, folded.Lines.Count);                              // the summary line + the field lines
            Assert.Equal(hidden, folded.Hidden);
            var open = card.OutcomeView(expanded: true);
            Assert.Equal(1 + rows, open.Lines.Count);
            Assert.Equal(hidden, open.Hidden);                                       // the view still knows there is something to fold
            Assert.Equal("B" + rows, open.Lines.Last().Label);

            card.Bulk(card.Groups()[0], AskOptionKinds.Skip);                         // struck lines are capped the same way
            Assert.All(card.OutcomeView(false).Lines.Skip(1), l => Assert.Equal(AskOutcomeState.Skipped, l.State));
            Assert.Equal(1 + shown, card.OutcomeView(false).Lines.Count);
        }

        [Fact]
        public void GroupsAndReadyPills_ExpandAndFold()
        {
            var card = ManyLevels(12, 18);
            var g = card.Groups()[0];
            Assert.Equal(5, AskCardModel.VisibleIn(g, expanded: false));
            Assert.Equal(12, AskCardModel.VisibleIn(g, expanded: true));
            Assert.Equal(5, AskCardModel.VisibleIn(g, expanded: false));              // folded again
            Assert.Equal("Show 7 more", AskCardModel.ShowMoreLabel(AskCardModel.HiddenIn(g)));
            Assert.Equal(3, AskCardModel.VisibleIn(Card(SheetsAsk()).Groups()[0], expanded: false) + AskCardModel.VisibleIn(Card(SheetsAsk()).Groups()[1], false));

            Assert.Equal(6, card.VisibleReady(expanded: false));
            Assert.Equal(18, card.VisibleReady(expanded: true));
            Assert.Equal("+12 more", AskCardModel.ReadyToggleLabel(card.HiddenReady, expanded: false));
            Assert.Equal("Show fewer", AskCardModel.ReadyToggleLabel(card.HiddenReady, expanded: true));
            Assert.Equal(3, Card(SheetsAsk()).VisibleReady(false));
        }

        // ─── Round 3: counts need the ready list ──────────────────────────────────────────────────────────

        [Fact]
        public void NounWithReady_CountsAsBefore()
        {
            var card = Card(SheetsAsk());
            Assert.True(card.KnowsTotal);
            Assert.True(card.ShowsPreview);
            Assert.Equal("3 sheets so far", card.PreviewCount);
            card.Bulk(card.Groups()[1], AskOptionKinds.Create);
            card.Pick("tanah", "Pelan Tapak");
            Assert.Equal("Make 6 sheets", card.FooterLabel);
        }

        [Fact]
        public void NounWithoutReady_SaysContinue_AndCountsNothing()
        {
            var ask = SheetsAsk();
            ask.Ready = null;                                                     // live R4: the backend could not derive it
            var card = Card(ask);
            Assert.False(card.KnowsTotal);
            Assert.False(card.ShowsPreview);
            Assert.Null(card.PreviewCount);
            Assert.Equal("Answer 3 more", card.FooterLabel);
            card.Bulk(card.Groups()[1], AskOptionKinds.Skip);
            card.Pick("tanah", "Pelan Tapak");
            Assert.Equal("Continue", card.FooterLabel);                           // never "Make 1 sheet"
            Assert.True(card.FooterEnabled);
        }

        [Fact]
        public void NeitherNounNorReady_AsToday()
        {
            var ask = SheetsAsk(withNoun: false);
            ask.Ready = null;
            var card = Card(ask);
            Assert.False(card.ShowsPreview);
            Assert.Null(card.PreviewCount);
            card.Bulk(card.Groups()[1], AskOptionKinds.Skip);
            card.Pick("tanah", "Pelan Tapak");
            Assert.Equal("Continue", card.FooterLabel);
        }

        // ─── Round 3: the key map (the sink feeds it; only a live run proves the keys arrive in Revit) ────

        [Fact]
        public void Keys_OnAForm_PickMoveAndSendOnlyWhenComplete()
        {
            var card = Card(SheetsAsk());
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForEnter(card).Kind);       // not complete: Enter does nothing

            var r = AskCardKeys.ForChar(card, '1');
            Assert.Equal(AskKeyKind.PickField, r.Kind);
            Assert.Equal("Pelan Tapak", r.Choice!.Id);
            AskCardKeys.Apply(card, r);
            Assert.Equal("Pelan Tapak", card.Picks["tanah"]);
            Assert.Same(card.Fields[1], card.ActiveField);                       // moved on to the next open field

            AskCardKeys.Apply(card, AskCardKeys.ForChar(card, 'C'));
            Assert.Equal("Create a FloorPlan for Aras Pentas", card.Picks["pentas"]);
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForChar(card, 'x').Kind);
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForChar(card, '9').Kind);   // no 9th choice
            AskCardKeys.Apply(card, AskCardKeys.ForChar(card, 's'));
            Assert.Equal("Skip Aras Langkan", card.Picks["langkan"]);

            Assert.Equal(AskKeyKind.Send, AskCardKeys.ForEnter(card).Kind);

            var up = AskCardKeys.ForArrow(card, down: false);
            Assert.Equal(AskKeyKind.MoveUp, up.Kind);
            var before = card.Active;
            AskCardKeys.Apply(card, up);
            Assert.Equal((before + 2) % 3, card.Active);

            card.AnsweredSummary = "sent";
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForEnter(card).Kind);       // an answered card takes no keys
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForChar(card, '1').Kind);
        }

        [Fact]
        public void Keys_OnASingleQuestion_SendTheOption_EnterDoesNothing()
        {
            var card = Card(new AskDto
            {
                RequestId = "r", Question = "q", Purpose = AskPurposes.Clarify, Title = "Aras Atap has no ceiling plan",
                Options = new List<AskOptionDto> { Opt("Create a CeilingPlan for Aras Atap", "Create a ceiling plan", AskOptionKinds.Create), Opt("Skip Aras Atap", "Skip this level", AskOptionKinds.Skip) },
            });
            var r = AskCardKeys.ForChar(card, 'c');
            Assert.Equal(AskKeyKind.SendOption, r.Kind);
            Assert.Equal("Create a CeilingPlan for Aras Atap", r.Choice!.Id);
            Assert.Equal(AskKeyKind.SendOption, AskCardKeys.ForChar(card, '2').Kind);
            Assert.False(AskCardKeys.HasSendButton(card));
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForEnter(card).Kind);
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForArrow(card, true).Kind);
        }

        [Fact]
        public void Keys_MultiSelect_ToggleThenEnterSends()
        {
            var card = Card(new AskDto { RequestId = "r", Question = "Which?", Purpose = AskPurposes.Clarify, Multi = true,
                Options = new List<AskOptionDto> { Opt("a", "Level 1"), Opt("b", "Level 2") } });
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForEnter(card).Kind);
            var r = AskCardKeys.ForChar(card, '2');
            Assert.Equal(AskKeyKind.ToggleOption, r.Kind);
            AskCardKeys.Apply(card, r);
            Assert.Contains("b", card.MultiPicks);
            Assert.Equal(AskKeyKind.Send, AskCardKeys.ForEnter(card).Kind);
        }

        [Fact]
        public void Keys_FreeTextAndNotes_NoShortcuts()
        {
            var free = Card(new AskDto { RequestId = "r", Question = "Which room?", Purpose = AskPurposes.Clarify });
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForChar(free, '1').Kind);
            free.FreeTextDraft = "lobby";
            Assert.Equal(AskKeyKind.Send, AskCardKeys.ForEnter(free).Kind);
            var note = Card(new AskDto { RequestId = "r", Question = "Save?", Purpose = AskPurposes.ProjectNote, NoteText = "x" });
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForChar(note, 's').Kind);
            Assert.Equal(AskKeyKind.None, AskCardKeys.ForEnter(note).Kind);
        }
    }
}
