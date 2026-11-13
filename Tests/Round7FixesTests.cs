#nullable enable
// Round 7 (live run of 13 Nov 2026 inside Revit): the seven fixes to the question card, the result card and the
// header's status pill. Every decision is in the model and tested here; whether the focus, the scroll and the width
// behave inside Revit only a live run shows (source pins check the wiring).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Round7FixesTests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        private static AskOptionDto Opt(string id, string label, string? kind = null) => new AskOptionDto { Id = id, Label = label, Kind = kind };

        /// <summary>The s01 decisions card of live C6b: a plan group first (Aras Tanah, two plans), then two levels with
        /// no floor plan.</summary>
        private static AskCardModel SheetsCard()
        {
            var card = AskCardModel.From(new AskDto
            {
                RequestId = "req_s01", Question = "3 of 6 levels need a decision.", Purpose = AskPurposes.Clarify, Title = "Before I make the sheets",
                Fields = new List<AskFieldDto>
                {
                    new AskFieldDto { Id = "tanah", Label = "Aras Tanah", Type = AskFieldTypes.Choice, Group = "Choose a plan", Note = "2 floor plans",
                        Choices = new List<AskOptionDto> { Opt("wip", "Aras Tanah (01w_WIP)"), Opt("tapak", "Pelan Tapak"), Opt("skip_t", "Skip this level", AskOptionKinds.Skip) } },
                    new AskFieldDto { Id = "pentas", Label = "Aras Pentas", Type = AskFieldTypes.Choice, Group = "No floor plan",
                        Choices = new List<AskOptionDto> { Opt("c1", "Create plan", AskOptionKinds.Create), Opt("s1", "Skip", AskOptionKinds.Skip) } },
                    new AskFieldDto { Id = "langkan", Label = "Aras Langkan", Type = AskFieldTypes.Choice, Group = "No floor plan",
                        Choices = new List<AskOptionDto> { Opt("c2", "Create plan", AskOptionKinds.Create), Opt("s2", "Skip", AskOptionKinds.Skip) } },
                },
            }, ReceiptLanguage.En);
            card.ArrivedLive = true;
            return card;
        }

        // ─── 1. The first arrow on a card that took the focus by itself only arms it ───────────────────────────────

        [Fact]
        public void NotArmed_TheFirstArrowDoesNotMove_LaterArrowsDo()
        {
            Assert.False(AskCardFocus.ArrowMoves(AskSinkMode.NotArmed));
            Assert.True(AskCardFocus.ArrowMoves(AskSinkMode.Armed));
            Assert.True(AskCardFocus.ArrowMoves(AskSinkMode.Plain));
        }

        /// <summary>The view's arrow handler, as a sequence on the model: mode before, arm, move only when it may.</summary>
        private static AskSinkMode Arrow(AskCardModel m, AskSinkMode mode, bool down)
        {
            var before = mode;
            mode = AskCardFocus.AfterArrow(mode);
            if (AskCardFocus.ArrowMoves(before)) AskCardKeys.Apply(m, AskCardKeys.ForArrow(m, down));
            return mode;
        }

        [Fact]
        public void LiveC6b_DownThenTheDigitOfThePlan_PicksThePlan()
        {
            var m = SheetsCard();
            var mode = AskSinkMode.NotArmed;          // took the focus from the empty message box
            Assert.Equal(0, m.Active);                // the outline is on the Aras Tanah plan group

            mode = Arrow(m, mode, down: true);        // Down: armed, the outline stays on the first field
            Assert.Equal(AskSinkMode.Armed, mode);
            Assert.Equal(0, m.Active);

            var two = AskCardKeys.ForChar(m, '2');    // the plan's digit acts on the plan group
            Assert.Equal(AskSinkText.Card, AskCardFocus.ForText(mode, "2", false, two));
            Assert.Equal(AskKeyKind.PickField, two.Kind);
            Assert.Equal("tanah", two.Field!.Id);
            Assert.Equal("tapak", two.Choice!.Id);
            AskCardKeys.Apply(m, two);
            Assert.Equal("tapak", m.Picks["tanah"]);

            mode = Arrow(m, mode, down: true);        // a later arrow moves as before
            Assert.NotEqual(0, m.Active);
        }

        [Fact]
        public void UpAlsoOnlyArms()
        {
            var m = SheetsCard();
            var mode = Arrow(m, AskSinkMode.NotArmed, down: false);
            Assert.Equal(AskSinkMode.Armed, mode);
            Assert.Equal(0, m.Active);
        }

        [Fact]
        public void TheViewSkipsTheMoveOnTheArmingArrow()
        {
            var view = Src("UI/Copilot/Controls/AskCard.cs");
            Assert.Contains("var modeBefore = _mode;", view);
            Assert.Contains("if (!AskCardFocus.ArrowMoves(modeBefore))", view);
        }

        // ─── 2. A card that arrives after the previous card was answered with keys ─────────────────────────────────

        [Fact]
        public void FocusInTheAnsweredCardsSink_TakesTheFocus_Armed()
        {
            Assert.True(AskCardFocus.ShouldTakeFocus(AskFocusPlace.AnsweredCardSink, "", cardWaiting: true));
            Assert.Equal(AskSinkMode.Armed, AskCardFocus.ModeOnArrival(AskFocusPlace.AnsweredCardSink));
            Assert.Equal(AskSinkMode.NotArmed, AskCardFocus.ModeOnArrival(AskFocusPlace.MessageBox));
        }

        [Fact]
        public void LiveC3_TheDigitAfterTheArrival_PicksOnTheNewCard()
        {
            var m = SheetsCard();
            var mode = AskCardFocus.ModeOnArrival(AskFocusPlace.AnsweredCardSink);
            var two = AskCardKeys.ForChar(m, '2');
            Assert.Equal(AskSinkText.Card, AskCardFocus.ForText(mode, "2", false, two));
            Assert.True(AskCardFocus.EnterActs(mode));
        }

        [Fact]
        public void TheRestStays_TextInTheBox_NotWaiting_Elsewhere_Outside()
        {
            Assert.False(AskCardFocus.ShouldTakeFocus(AskFocusPlace.AnsweredCardSink, "semua", cardWaiting: true));
            Assert.False(AskCardFocus.ShouldTakeFocus(AskFocusPlace.AnsweredCardSink, "", cardWaiting: false));
            Assert.False(AskCardFocus.ShouldTakeFocus(AskFocusPlace.OutsidePane, "", cardWaiting: true));
            Assert.False(AskCardFocus.ShouldTakeFocus(AskFocusPlace.ElsewhereInPane, "", cardWaiting: true));
        }

        [Theory]
        // inBox, onAnsweredSink, inPane, inTextBox, pending → place
        [InlineData(true, false, true, true, true, AskFocusPlace.MessageBox)]
        [InlineData(false, true, false, true, false, AskFocusPlace.AnsweredCardSink)]   // still on the answered sink (taken out of the pane)
        [InlineData(false, true, true, true, true, AskFocusPlace.AnsweredCardSink)]
        [InlineData(false, false, true, false, true, AskFocusPlace.AnsweredCardSink)]   // WPF moved it to the chat's scroll viewer
        [InlineData(false, false, true, true, true, AskFocusPlace.ElsewhereInPane)]     // a text box the drafter chose
        [InlineData(false, false, true, false, false, AskFocusPlace.ElsewhereInPane)]   // no key answer: as before
        [InlineData(false, false, false, false, true, AskFocusPlace.OutsidePane)]       // the Revit canvas: never
        [InlineData(false, false, false, false, false, AskFocusPlace.OutsidePane)]
        public void Classify(bool inBox, bool onSink, bool inPane, bool inTextBox, bool pending, AskFocusPlace expected) =>
            Assert.Equal(expected, AskCardFocus.Classify(inBox, onSink, inPane, inTextBox, pending));

        [Fact]
        public void TheWideWriteCardAndAHistoryCard_StillNeverTake()
        {
            var history = SheetsCard();
            history.ArrivedLive = false;
            Assert.False(AskCardFocus.IsWaiting(history, live: true));
            var wide = AskCardModel.From(new AskDto
            {
                RequestId = "r", Question = "Before I write to 340 elements.", Purpose = AskPurposes.Clarify,
                Options = new List<AskOptionDto> { Opt("write_all", "Write to all"), Opt("write_cancel", "Cancel, write nothing") },
            }, ReceiptLanguage.En);
            wide.ArrivedLive = true;
            Assert.False(AskCardFocus.IsWaiting(wide, live: true));
        }

        [Fact]
        public void TheViewMarksKeyAnswersAndUsesTheArrivalMode()
        {
            var view = Src("UI/Copilot/Controls/AskCard.cs");
            Assert.Contains("if (_byKey && _sink.IsKeyboardFocused) MarkKeyAnswer();", view);
            Assert.Contains("_mode = AskCardFocus.ModeOnArrival(place);", view);
            Assert.Contains("AskCardFocus.Classify(", view);
            Assert.Contains("KeyAnswers.Remove(box);", view);
        }

        // ─── 3. A new question card arrives with its top visible ──────────────────────────────────────────────────

        [Fact]
        public void ATallCard_ArrivesWithItsTitleAtTheTopOfTheVisibleArea()
        {
            // Card top at 1200 in a 2000-high thread, 440 visible: the bottom would be 1560 (title off screen).
            Assert.Equal(1192, AskCardKeys.ArrivalOffset(cardTop: 1200, viewport: 440, extent: 2000));
        }

        [Fact]
        public void ACardThatFits_ArrivesAtTheBottom_AsNow()
        {
            // Card top at 1700, thread 2000, 440 visible: the bottom (1560) shows the top already.
            Assert.Equal(1560, AskCardKeys.ArrivalOffset(cardTop: 1700, viewport: 440, extent: 2000));
        }

        [Fact]
        public void TheOffsetStaysInRange()
        {
            Assert.Equal(0, AskCardKeys.ArrivalOffset(cardTop: 4, viewport: 440, extent: 2000));
            Assert.Equal(0, AskCardKeys.ArrivalOffset(cardTop: 100, viewport: 440, extent: 300));   // nothing to scroll
            Assert.Equal(1560, AskCardKeys.ArrivalOffset(cardTop: 1200, viewport: 0, extent: 1560)); // no viewport yet: the end
        }

        [Fact]
        public void ThePaneUsesTheArrivalRuleOnlyForANewLiveQuestion()
        {
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("new System.Action(FollowNewContent)", chat);
            Assert.Contains("AskCardKeys.ArrivalOffset(top, sv.ViewportHeight, sv.ExtentHeight)", chat);
            Assert.Contains("if (askLive && m.Ask.ArrivedLive && !m.Ask.ArrivalShown)", chat);
            Assert.Contains("sv.ScrollToEnd();", chat);   // every other message: the bottom, as before
        }

        // ─── 4. The card follows the pane's width ─────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(206, 174)]    // live C4: the pane dragged to about 238 px
        [InlineData(530, 498)]
        [InlineData(10, 0)]
        public void TheFullColumn_IsTheBodyLessTheThreadMargins(double body, double column) =>
            Assert.Equal(column, PaneWidths.FullColumn(body));

        [Theory]
        [InlineData(206, 174)]    // narrow: never wider than the column (the old 320 px floor clipped it)
        [InlineData(400, 320)]    // the 320 px floor while it fits
        [InlineData(900, 721)]    // 85% less the gutter
        [InlineData(0, 360)]      // before layout
        public void TheReplyColumn_NeverPassesTheThreadsColumn(double body, double expected) =>
            Assert.Equal(expected, PaneWidths.Bubble(body));

        [Fact]
        public void FullWidthColumnsStretch_NoFixedWidth()
        {
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("if (cardShown || askShown) FullWidth(col);", chat);
            Assert.Contains("col.HorizontalAlignment = HorizontalAlignment.Stretch;", chat);
            Assert.DoesNotContain("col.Width = col.MaxWidth;", chat);
            Assert.DoesNotContain("System.Math.Max(320, BodyHost.ActualWidth - 32)", chat);
        }

        // ─── 5. A failed turn reads as failed and offers no "Save as command" ─────────────────────────────────────

        [Fact]
        public void IsFailed()
        {
            Assert.True(TurnStatus.IsFailed(null));
            Assert.True(TurnStatus.IsFailed(new RouteResult { Failed = true, FailureKind = RouteFailure.Other }));
            Assert.True(TurnStatus.IsFailed(new RouteResult { EndedFailed = true, Reply = "Partly done." }));
            Assert.False(TurnStatus.IsFailed(new RouteResult { Reply = "Made 5 sheets." }));
            Assert.False(TurnStatus.IsFailed(new RouteResult { Failed = true, Superseded = true }));
        }

        [Fact]
        public void LiveT06_ThePillReadsFailed_NotDone()
        {
            // IsSending=false had already set "Done"; the result then corrects it.
            Assert.Equal("Failed", TurnStatus.AfterResult("Done", failed: true, caution: false));
            Assert.Equal("Failed", TurnStatus.AfterResult("Done", failed: true, caution: true));
            Assert.Equal("Check receipt", TurnStatus.AfterResult("Done", failed: false, caution: true));
            Assert.Equal("Done", TurnStatus.AfterResult("Done", failed: false, caution: false));
            Assert.Equal("Stopped", TurnStatus.AfterResult("Stopped", failed: false, caution: false));
        }

        [Fact]
        public void LiveT06_AFailedReply_OffersNoSaveAsCommand()
        {
            const string prompt = "Tag all doors in the Pelan Tapak floor plan.";
            Assert.False(TurnStatus.CanSaveAsCommand(true, 1, prompt, interrupted: false, failed: true));
            Assert.True(TurnStatus.CanSaveAsCommand(true, 1, prompt, interrupted: false, failed: false));
            // The rest as before: no tools, a slash command, an interrupted turn, not a reply.
            Assert.False(TurnStatus.CanSaveAsCommand(true, 0, prompt, false, false));
            Assert.False(TurnStatus.CanSaveAsCommand(true, 1, "/tag-doors", false, false));
            Assert.False(TurnStatus.CanSaveAsCommand(true, 1, prompt, interrupted: true, failed: false));
            Assert.False(TurnStatus.CanSaveAsCommand(false, 1, prompt, false, false));
        }

        [Fact]
        public void TheWiring_RouterVmChatAndPill()
        {
            Assert.Contains("r.EndedFailed = end.Status == TurnEndStatus.Failed;", Src("UI/Copilot/CopilotTurnRouter.cs"));
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("Failed = TurnStatus.IsFailed(rr),", vm);
            Assert.Contains("RunStatus = TurnStatus.AfterResult(RunStatus, reply.Failed, caution);", vm);
            Assert.Contains("RunStatus = TurnStatus.Failed;", vm);
            Assert.Contains("TurnStatus.CanSaveAsCommand(m.Kind == CpMsgKind.AiReply, m.ToolsUsed?.Count ?? 0, m.SourcePrompt, m.Interrupted, m.Failed)", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
            var xaml = Src("UI/Copilot/Screens/ChatView.xaml");
            Assert.Contains("<DataTrigger Binding=\"{Binding RunStatus}\" Value=\"Failed\">", xaml);
            Assert.Contains("Cp.Tile.RedBg", xaml);
        }

        // ─── 6. The result card lists what the drafter got ────────────────────────────────────────────────────────

        private static Envelope Commit(Dictionary<string, int> touched, Dictionary<string, int>? side, string? created, int addedIds, long idBase = 7000)
        {
            var data = created == null ? "{\"mode\":\"write\"}" : "{\"mode\":\"write\",\"created\":[" + created + "]}";
            return new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Created", Count = addedIds,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Added = Enumerable.Range(0, addedIds).Select(i => new ElementRef(idBase + i, null)).ToList() },
                    TouchedByCategory = new SideEffectsDto { AddedByCategory = touched },
                    SideEffects = side == null ? null : new SideEffectsDto { AddedByCategory = side },
                },
                Data = JsonDocument.Parse(data).RootElement.Clone(),
            };
        }

        private static string Item(string category, string name) => "{\"id\":1,\"category\":\"" + category + "\",\"name\":\"" + name + "\"}";

        /// <summary>The two commits of live C1 (counts exactly as the envelopes carried them).</summary>
        private static Envelope[] LiveC1() => new[]
        {
            Commit(new() { ["Views"] = 1, ["class:Element"] = 3, ["Sun Path"] = 2, ["Work Plane Grid"] = 1, ["class:SketchPlane"] = 2 },
                new() { ["Viewports"] = 1 }, Item("Views", "Aras Pentas"), 9),
            Commit(new() { ["Sheets"] = 5, ["class:Element"] = 30, ["class:SketchPlane"] = 5, ["Title Blocks"] = 5, ["Viewports"] = 5, ["Schedule Graphics"] = 5, ["class:GraphicsStyle"] = 5 },
                new() { ["Viewports"] = 10 },
                string.Join(",", new[] { "A-101 - Pelan Tapak", "A-102 - Aras 01", "A-103 - Aras Pentas", "A-104 - Aras 02", "A-105 - Aras Bumbung" }.Select(n => Item("Sheets", n))), 60, idBase: 8000),
        };

        [Fact]
        public void LiveC1_FiveSheetsAndOneView_AndOneQuietLine()
        {
            var s = CreatedSummary.From(LiveC1(), null, ReceiptLanguage.En)!;
            Assert.Equal(new[] { "Sheets 5", "Views 1" }, s.Groups.Select(g => g.Category + " " + g.Count));
            Assert.Equal("5 Sheets · 1 View", s.Headline(ReceiptLanguage.En));
            Assert.Equal(74, s.Supporting);   // 80 added in all, less the 6 shown
            Assert.Equal("and 74 supporting elements", s.SupportingLine(ReceiptLanguage.En));
            Assert.Equal(5, s.Groups[0].Names.Count);
        }

        [Fact]
        public void LiveC5_OneSheetAndTheNewCeilingPlan()
        {
            var s = CreatedSummary.From(new[]
            {
                Commit(new() { ["Views"] = 1, ["class:Element"] = 3, ["Sun Path"] = 2, ["Work Plane Grid"] = 1, ["class:SketchPlane"] = 1 },
                    new() { ["Viewports"] = 1 }, Item("Views", "Aras Bumbung (07d_Dokumen)"), 8),
                Commit(new() { ["Sheets"] = 1, ["class:Element"] = 6, ["class:SketchPlane"] = 1, ["Title Blocks"] = 1, ["Viewports"] = 1, ["Schedule Graphics"] = 1, ["class:GraphicsStyle"] = 1 },
                    new() { ["Viewports"] = 2 }, Item("Sheets", "A-101 - Aras Bumbung (07d_Dokumen)"), 12),
            }, null, ReceiptLanguage.Ms)!;
            Assert.Equal(new[] { "Sheets 1", "Views 1" }, s.Groups.Select(g => g.Category + " " + g.Count));
            Assert.Equal("dan 21 elemen sokongan", s.SupportingLine(ReceiptLanguage.Ms));
            Assert.DoesNotContain(s.Groups, g => g.Category == "Sun Path" || g.Category == "Other" || g.Category.Contains("Sketch"));
        }

        [Fact]
        public void NoCreatedList_RowsFromTheTouchedCategories_WithoutByProducts()
        {
            // An older add-in (no data.created) placing views on sheets: the viewports are what the drafter asked for.
            var s = CreatedSummary.From(new[]
            {
                Commit(new() { ["Viewports"] = 4, ["class:SketchPlane"] = 2, ["Schedule Graphics"] = 1, ["class:Element"] = 3 }, new() { ["Viewports"] = 8 }, null, 10),
            }, null, ReceiptLanguage.En)!;
            Assert.Equal(new[] { "Viewports 4" }, s.Groups.Select(g => g.Category + " " + g.Count));
            Assert.Equal(14, s.Supporting);
        }

        [Fact]
        public void ACreatedListAtItsCap_UsesTheCategorysWholeCount()
        {
            var items = string.Join(",", Enumerable.Range(1, CreatedSummary.CreatedListCap).Select(i => Item("Sheets", "S-" + i)));
            var s = CreatedSummary.From(new[] { Commit(new() { ["Sheets"] = 64 }, null, items, 64) }, null, ReceiptLanguage.En)!;
            Assert.Equal(64, s.Groups.Single().Count);
            Assert.Equal(0, s.Supporting);
            Assert.Null(s.SupportingLine(ReceiptLanguage.En));
        }

        [Theory]
        [InlineData("class:SketchPlane", true)]
        [InlineData("class:GraphicsStyle", true)]
        [InlineData("class:Element", true)]
        [InlineData("Other", true)]
        [InlineData("Sun Path", true)]
        [InlineData("Work Plane Grid", true)]
        [InlineData("Schedule Graphics", true)]
        [InlineData("Sheets", false)]
        [InlineData("Viewports", false)]
        [InlineData("Doors", false)]
        public void ByProducts(string category, bool byProduct) => Assert.Equal(byProduct, CreatedSummary.IsByProduct(category));

        [Fact]
        public void UndoStillCoversEverythingTheCommitsAdded()
        {
            var ev = new TurnEvidence();
            ev.Add(new ToolRequestDto
            {
                RequestId = "r", Kind = ToolRequestKinds.Write,
                Calls = new List<ToolCallDto>
                {
                    new ToolCallDto { CallId = "c1", IdempotencyKey = "k1", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{}").RootElement.Clone() },
                    new ToolCallDto { CallId = "c2", IdempotencyKey = "k2", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{}").RootElement.Clone() },
                },
            }, LiveC1());
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(ResultKind.Created, m.Kind);
            Assert.Equal("5 Sheets · 1 View", m.Headline);
            Assert.Equal(69, m.Created.Count);   // every touched id the commits added, as before: what "Show them" and Undo use
        }

        [Fact]
        public void TheCardDrawsTheQuietLine()
        {
            Assert.Contains("by.SupportingLine(lang) is string supporting", Src("UI/Copilot/Controls/CreatedCard.cs"));
        }

        // ─── 7. The headline is shown whole ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void LiveC3_The130CharacterReply_IsKeptWhole()
        {
            const string reply = "Empat sheet A1 dibuat: A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung dengan title block (i205a) A1 Landscape.";
            Assert.Equal(reply, CardText.Headline(reply));
            var twoHundred = string.Join(" ", Enumerable.Repeat("sheets", 34)).Trim();
            Assert.True(twoHundred.Length > 200);
            Assert.Equal(twoHundred, CardText.Headline(twoHundred));
            Assert.EndsWith("…", CardText.Headline(new string('x', 20) + " " + string.Join(" ", Enumerable.Repeat("filled", 80))));
        }
    }
}
