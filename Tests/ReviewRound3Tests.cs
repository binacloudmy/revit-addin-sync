#nullable enable
// pr checker's review of the local chain 754206cc..: no card for "No net change" crashed, items on a card-less turn were
// dropped, a sheet button could open a wall, the Home card flickered, Created rows could count twice, "Analysis" lost its s,
// "Leave as is" left stale marks, the banner said "removed", and the router-side step number had no test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReviewRound3Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static ToolCallDto Call(string tool) =>
            new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse("{}").RootElement.Clone() };
        private static ToolRequestDto Request(params ToolCallDto[] calls) =>
            new ToolRequestDto { RequestId = "r", Kind = ToolRequestKinds.Write, Calls = calls.ToList() };

        // ---- 1: "No net change" has no group ----

        private static ResultCardModel NoNetChange()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set Mark", Count = 1,
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
                {
                    new FieldChangeDto { Id = 1, Field = "Mark", Before = "A", After = "A" },     // written, equal to what it held
                } } },
                Data = JsonDocument.Parse("{\"param\":\"Mark\",\"type_param\":false,\"instances_category\":\"Doors\"}").RootElement.Clone(),
            };
            var ev = new TurnEvidence();
            ev.Add(Request(Call(ToolNames.SetParameters)), new[] { env });
            return ResultCardModel.From(ev, ReceiptLanguage.En);
        }

        [Fact]
        public void ANoNetChangeCard_HasNoGroups_SoNothingMayIndexTheFirstOne()
        {
            var m = NoNetChange();

            Assert.Equal(ResultKind.Changes, m.Kind);
            Assert.Equal("No net change", m.Headline);
            Assert.Empty(m.Groups);
            Assert.Null(m.FirstGroup);
            Assert.Empty(ChangeTable.Rows(m, ReceiptLanguage.En));
            Assert.Equal("Nothing else changed.", UndoBanner.For(m, ReceiptLanguage.En).Detail);
        }

        [Fact]
        public void TheCardView_GuardsTheEmptyCase_AndNeverIndexesGroupsDirectly()
        {
            var s = Src("UI/Copilot/Controls/ResultCardView.cs");

            Assert.Contains("if (card.FirstGroup == null) return;", s);
            Assert.DoesNotContain("Groups[0]", s);
        }

        // ---- 2: the server's rows on a turn the envelopes gave no card for ----

        private static TurnResultDto Server(string kind, Action<TurnResultDto>? more = null)
        {
            var r = new TurnResultDto { Reply = "22 sheets created.", Kind = kind };
            more?.Invoke(r);
            return r;
        }

        [Fact]
        public void ItemsOnACardlessTurn_BecomeACreatedCard_UnderTheServersSentence()
        {
            var card = new ResultCardModel().WithServerResult(Server("created", r =>
                r.Items = new List<ResultItemDto> { new ResultItemDto { Kind = "sheet", Title = "Cover", Ids = new List<JsonElement> { JsonDocument.Parse("5").RootElement.Clone() } } }));

            Assert.Equal(ResultKind.Created, card.Kind);
            Assert.Equal("22 sheets created.", card.Headline);
            Assert.Equal(1, card.Items!.ItemCount);
        }

        [Fact]
        public void ChecksOrNextAlone_AlsoKeepTheCard_ButNothingAtAll_DoesNot()
        {
            Assert.Equal(ResultKind.Created, new ResultCardModel().WithServerResult(Server("changes", r => r.Next = new List<string> { "Go on" })).Kind);
            Assert.Equal(ResultKind.Created, new ResultCardModel().WithServerResult(Server("changes", r =>
                r.Checks = new List<ResultCheckDto> { new ResultCheckDto { Severity = "info", Text = "Look" } })).Kind);
            Assert.Equal(ResultKind.None, new ResultCardModel().WithServerResult(Server("changes")).Kind);
            Assert.Equal(ResultKind.None, new ResultCardModel().WithServerResult(null).Kind);
        }

        [Fact]
        public void ACardTheEnvelopesBuilt_KeepsItsKind_WhateverTheServerSays()
        {
            var card = new ResultCardModel { Kind = ResultKind.Selection, Headline = "52 selected" }
                .WithServerResult(Server("created", r => r.Next = new List<string> { "Go on" }));

            Assert.Equal(ResultKind.Selection, card.Kind);
            // the server's one-sentence reply is the headline (Server() sends "22 sheets created."), the kind stays
            Assert.Equal("22 sheets created.", card.Headline);
            Assert.True(card.ReplacesProse);
        }

        // ---- 3: open only what the id really is, in the model it came from ----

        [Fact]
        public void OpenGuard_ChecksTheModel_ThenTheClassOfTheElement()
        {
            string? Why(string? expected, string? active, bool sheet, bool found, bool isSheet, bool isView, bool isTemplate = false) =>
                OpenGuard.Why(expected, active, sheet, found, isSheet, isView, isTemplate);

            Assert.Null(Why("doc-a", "doc-a", true, true, true, true));                  // a sheet is a View too
            Assert.Null(Why(null, "doc-b", false, true, false, true));                   // no key known: not checked
            Assert.Contains("another model", Why("doc-a", "doc-b", true, true, true, true));
            Assert.Contains("another model", Why("doc-a", null, true, true, true, true));
            Assert.Contains("not in the model", Why("doc-a", "doc-a", true, false, false, false));
            Assert.Contains("not a sheet", Why("doc-a", "doc-a", true, true, false, true));       // a plan where a sheet was named
            Assert.Contains("not a sheet", Why("doc-a", "doc-a", true, true, false, false));      // a wall
            Assert.Contains("not a view", Why("doc-a", "doc-a", false, true, true, true));        // a sheet where a view was named
            Assert.Contains("not a view", Why("doc-a", "doc-a", false, true, false, false));      // a wall
            Assert.Contains("not a view", Why("doc-a", "doc-a", false, true, false, true, isTemplate: true));
            Assert.Contains("bukan sheet", OpenGuard.Why("a", "a", true, true, false, false, false, ReceiptLanguage.Ms));
        }

        [Fact]
        public void AButtonShowsOnlyForTheClassItNeeds()
        {
            Assert.True(OpenGuard.Allows(ItemButton.OpenSheet, ItemClass.Sheet));
            Assert.False(OpenGuard.Allows(ItemButton.OpenSheet, ItemClass.View));
            Assert.False(OpenGuard.Allows(ItemButton.OpenSheet, ItemClass.Other));
            Assert.True(OpenGuard.Allows(ItemButton.OpenView, ItemClass.View));
            Assert.False(OpenGuard.Allows(ItemButton.OpenView, ItemClass.Sheet));
            Assert.False(OpenGuard.Allows(ItemButton.OpenView, ItemClass.Template));
            Assert.True(OpenGuard.Allows(ItemButton.Select, ItemClass.Other));
            Assert.False(OpenGuard.Allows(ItemButton.Select, ItemClass.Missing));
            Assert.False(OpenGuard.Allows(ItemButton.None, ItemClass.Sheet));
        }

        [Fact]
        public void ARowsButton_NeedsItsSampledIdsToBeWhatItSays()
        {
            var sheet = new ItemRow { Kind = "sheet", Title = "x", Ids = { 10 } };
            var tags = new ItemRow { Kind = "tag", Title = "t", Ids = { 1, 2, 3 } };
            var none = new ItemRow { Kind = "sheet", Title = "y" };

            Assert.True(ResultItems.ShowButton(sheet, new Dictionary<long, ItemClass> { [10] = ItemClass.Sheet }));
            Assert.False(ResultItems.ShowButton(sheet, new Dictionary<long, ItemClass> { [10] = ItemClass.Other }));   // the id of a wall
            Assert.False(ResultItems.ShowButton(sheet, new Dictionary<long, ItemClass>()));                             // not looked up
            Assert.True(ResultItems.ShowButton(tags, new Dictionary<long, ItemClass> { [1] = ItemClass.Missing, [2] = ItemClass.Other }));
            Assert.False(ResultItems.ShowButton(tags, new Dictionary<long, ItemClass> { [1] = ItemClass.Missing }));
            Assert.False(ResultItems.ShowButton(none, new Dictionary<long, ItemClass>()));
            Assert.Equal(new long[] { 10 }, ResultItems.SampleIds(sheet).ToArray());
            Assert.Equal(20, ResultItems.SampleIds(new ItemRow { Kind = "element", Title = "e", Ids = Enumerable.Range(1, 90).Select(i => (long)i).ToList() }).Count);
        }

        [Fact]
        public void TheItemsCard_HidesEveryButtonUntilRevitVouchesForIt_AndOpensWithTheSheetFlag()
        {
            var s = Src("UI/Copilot/Controls/ItemsCard.cs");

            Assert.Contains("chip.Visibility = Visibility.Collapsed;", s);
            Assert.Contains("actions.ClassifyIds", s);
            Assert.Contains("ResultItems.ShowButton(row, map)", s);
            Assert.Contains("actions.OpenView(ids[0], button == ItemButton.OpenSheet)", s);
            var ra = Src("UI/Copilot/Ports/RevitActions.cs");
            Assert.Contains("OpenGuard.Why(", ra);
        }

        [Fact]
        public void ShowHeaders_WhenThereAreSeveralSectionsOrANamedOne()
        {
            var named = new ResultItems { Sections = { new ItemSection { Name = "Plans" } } };
            var bare = new ResultItems { Sections = { new ItemSection { Name = "" } } };
            var two = new ResultItems { Sections = { new ItemSection { Name = "" }, new ItemSection { Name = "" } } };

            Assert.True(ResultItems.ShowHeaders(named));
            Assert.False(ResultItems.ShowHeaders(bare));
            Assert.True(ResultItems.ShowHeaders(two));
        }

        // ---- 4: Home summary cache ----

        private static ViewSummary Summary(int n) => ViewSummary.Build(Enumerable.Repeat(("Doors", "Single"), n))!;

        [Fact]
        public void TheSummaryCache_ServesAGoodReadForTenSeconds_ThenAsksAgain()
        {
            var c = new SummaryCache();
            var t0 = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
            Assert.False(c.TryGet("doc|view", t0, out _));

            c.Put("doc|view", t0, Summary(5), complete: true);

            Assert.True(c.TryGet("doc|view", t0.AddSeconds(9), out var v));
            Assert.Equal(5, v!.Total);
            Assert.False(c.TryGet("doc|view", t0.AddSeconds(10), out _));
            Assert.False(c.TryGet("other|view", t0.AddSeconds(1), out _));          // another view or document
        }

        [Fact]
        public void ARead_ThatRanOutOfTime_NeverReplacesAGoodOne_SoTheCardDoesNotFlicker()
        {
            var c = new SummaryCache();
            var t0 = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
            c.Put("k", t0, Summary(5), complete: true);

            var shown = c.Put("k", t0.AddSeconds(11), null, complete: false);       // the re-read ran out of time

            Assert.Equal(5, shown!.Total);                                           // the last good card stays
            Assert.True(c.TryGet("k", t0.AddSeconds(11 + 59), out var v));          // and is not re-read for a minute
            Assert.Equal(5, v!.Total);
            Assert.False(c.TryGet("k", t0.AddSeconds(11 + 60), out _));
        }

        [Fact]
        public void ABigViewWithNoGoodRead_IsNoCard_AndIsNotReReadEveryFewSeconds()
        {
            var c = new SummaryCache();
            var t0 = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

            Assert.Null(c.Put("big", t0, null, complete: false));

            Assert.True(c.TryGet("big", t0.AddSeconds(30), out var v));
            Assert.Null(v);
            Assert.False(c.TryGet("big", t0.AddSeconds(60), out _));
        }

        [Fact]
        public void AGoodRead_AlwaysReplaces_EvenWithNoElementsLeft()
        {
            var c = new SummaryCache();
            var t0 = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
            c.Put("k", t0, Summary(5), complete: true);

            Assert.Null(c.Put("k", t0.AddSeconds(20), null, complete: true));      // the view really is empty now
            Assert.Null(c.Last("k"));
        }

        [Fact]
        public void Home_UsesTheCache_AndReportsWhetherTheReadFinished()
        {
            var s = Src("UI/Copilot/Screens/ChatView.Home.cs");

            Assert.Contains("SummaryReads.TryGet(", s);
            Assert.Contains("SummaryReads.Put(key, now, read, complete)", s);
            Assert.Contains("complete = true;", s);
        }

        // ---- 5: created rows count once; names that only look plural ----

        [Fact]
        public void TouchedAndSideEffectsAreDisjoint_SoSixSheetsWithTheirViewportsAreNotCountedTwice()
        {
            var all = new ChangeSet();
            for (long id = 1; id <= 6; id++) { all.Added.Add(id); all.CategoryOf[id] = "Sheets"; }
            for (long id = 11; id <= 16; id++) { all.Added.Add(id); all.CategoryOf[id] = "Viewports"; }
            var (touched, side) = ChangeSetMath.Split(all, Enumerable.Range(1, 6).Select(i => (long)i).ToList(), Array.Empty<long>());
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Created", Count = 6,
                Changes = new ChangeReportDto { Touched = touched, SideEffects = side, TouchedByCategory = BinaVibe.Copilot.Writes.EnvelopeChanges.ByCategory(touched, all.CategoryOf) },
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
            };

            var by = CreatedSummary.From(new[] { env }, null, ReceiptLanguage.En)!;

            Assert.Equal(6, by.Groups.Single(g => g.Category == "Sheets").Count);
            Assert.Equal(6, by.Groups.Single(g => g.Category == "Viewports").Count);
            Assert.Equal(12, by.Total);
        }

        [Theory]
        [InlineData("Analysis", "1 Analysis")]
        [InlineData("Status", "1 Status")]
        [InlineData("Glass", "1 Glass")]
        [InlineData("Sheets", "1 Sheet")]
        [InlineData("Lighting Fixtures", "1 Lighting Fixture")]
        [InlineData("Entries", "1 Entry")]
        [InlineData("Ceilings", "1 Ceiling")]
        public void ASingleOne_KeepsRevitsSpellingWhenTheNameOnlyLooksPlural(string category, string label)
        {
            Assert.Equal(label, CreatedSummary.Label(new CreatedGroup { Category = category, Count = 1 }));
        }

        // ---- 6: "Leave as is" ----

        [Fact]
        public void LeavingATypeAsIs_TakesItsChangedBackMarkOffTheHeadline()
        {
            var ev = new TurnEvidence();
            ev.Add(Request(Call(ToolNames.SetParameters)), new[] { TypeWrite(1, "A", "B", live: true) });
            ev.Add(Request(Call(ToolNames.SetParameters)), new[] { TypeWrite(2, "T5", "10 W") });
            ev.Add(Request(Call(ToolNames.SetParameters)), new[] { TypeWrite(2, "10 W", "T5") });
            var m = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(1, m.ChangedBackOpen);
            Assert.Contains("1 changed back", m.Detail);

            m.Groups.Single(g => g.ChangedBack).LeftAsIs = true;
            m.RefreshHeadline(ReceiptLanguage.En);

            Assert.Equal(0, m.ChangedBackOpen);
            Assert.Equal(1, m.ChangedBackTypes);                 // it is still a type that was put back
            Assert.DoesNotContain("changed back", m.Detail);
            Assert.DoesNotContain("changed back", m.Counts);
        }

        private static Envelope TypeWrite(long type, string before, string after, bool live = false) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set P", Count = 1,
            Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new List<FieldChangeDto>
            {
                new FieldChangeDto { Id = type, Field = "P" + type, Before = before, After = after },
            } } },
            Data = JsonDocument.Parse("{\"param\":\"P" + type + "\",\"type_param\":true,\"types\":[{\"id\":" + type + ",\"name\":\"Type " + type + "\",\"instances_in_model\":3}],\"instances_affected\":3}").RootElement.Clone(),
        };

        [Fact]
        public void TheCardViewRedrawsAfterLeave_AndTheRetainedCardKeyKnowsIt()
        {
            // The Changes body lives in ChangesBody.cs; a put-back type's tile ring is amber until it is left as is.
            Assert.Contains("actions.Redraw?.Invoke();", Src("UI/Copilot/Controls/ChangesBody.cs"));
            Assert.Contains("card.Groups.Count(g => g.LeftAsIs)", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
            Assert.Contains("(g.AnyChangedBack && !g.LeftAsIs) || g.Rows.Any(r => r.NeedsCheck)", Src("UI/Copilot/Controls/ChangesBody.cs"));
        }

        // ---- 7: banner wording is in Round2Tests; 8: the step number on the Turn the chat session keeps ----

        [Fact]
        public void ModelStep_ReachesTheStepRowOfTheSessionsTurn()
        {
            var s = new ChatSession();
            var t = s.Begin("t1", "How many doors?", ReceiptLanguage.En);

            s.OnProgress(t.Id, new ProgressDto { Seq = 2, Kind = "running", Text = "Reading", Step = 4 });
            s.OnProgress(t.Id, new ProgressDto { Seq = 3, Kind = "thinking", Text = "Planning" });

            Assert.Equal(4, t.Steps.Single(x => x.Label == "Reading").ModelStep);
            Assert.Equal(0, t.Steps.Single(x => x.Label == "Planning").ModelStep);
        }
    }
}
