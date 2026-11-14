#nullable enable
// Live 14 Nov (SP-4): one set_parameters on 60 doors; the add-in wrote and read back 60, but the envelope lists at most 50
// rows (EnvelopeLimits.MaxRows) and the card counted rows: "Read back from Revit: 50 of 50 match", "Elements written · 50",
// while the in-model key said 56 (the doors in the view). The card now says what was written (60, 60 of 60 read back),
// "50 of 60 listed" where its rows were cut, and the key says how many of the 60 are in this view. Invented names only.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CutRowsCardTests
    {
        private const ReceiptLanguage En = ReceiptLanguage.En;

        private static ToolRequestDto Request() => new ToolRequestDto
        {
            RequestId = "r", Kind = ToolRequestKinds.Write,
            Calls = new List<ToolCallDto> { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = ToolNames.SetParameters, Args = JsonDocument.Parse("{}").RootElement.Clone() } },
        };

        /// <summary>An instance commit as the add-in sends it: <paramref name="written"/> doors, the first 50 as rows, the
        /// per-category count of all of them, and the read-back of all of them with <paramref name="mismatches"/> different.</summary>
        private static Envelope DoorsCommit(int written, int mismatches = 0, bool keepFullList = false)
        {
            var ids = Enumerable.Range(0, written).Select(i => 7000L + i).ToList();
            var env = new Envelope
            {
                Ok = true, Status = mismatches > 0 ? EnvelopeStatus.Partial : EnvelopeStatus.Done, Phase = EnvelopePhase.Commit,
                Summary = "Set Comments = Checked on " + written + " Doors; read back " + (written - mismatches) + " of " + written + ".",
                Count = written,
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto
                    {
                        Modified = ids.Take(EnvelopeLimits.MaxRows).Select(id => new FieldChangeDto
                        {
                            Id = id, Field = "Comments", Before = "", After = "Checked", Scope = "instance", Element = "Sample Door 900",
                            Source = new ResultSourceDto { Kind = ResultSourceKinds.Prompt },
                        }).ToList(),
                    },
                    TouchedByCategory = new SideEffectsDto { ModifiedByCategory = new Dictionary<string, int> { ["Doors"] = written } },
                },
                Verified = new VerifiedDto
                {
                    Method = VerifyMethods.ParamReadback, Checked = written,
                    Mismatches = ids.Skip(written - mismatches).Select(id => new MismatchDto { Id = id, Field = "Comments", Expected = "Checked", Actual = "" }).ToList(),
                },
                Handle = "h_sample",
                Data = JsonDocument.Parse("{\"param\":\"Comments\",\"type_param\":false,\"matched\":" + written + ",\"changed\":" + written + ",\"unchanged\":0}").RootElement.Clone(),
            };
            if (keepFullList)
                CommitIds.Remember(env, new ChangesDto { Modified = ids.Select(id => new FieldChangeDto { Id = id, Field = "Comments" }).ToList() },
                    ids.ToDictionary(id => id, id => "Doors"));
            return env;
        }

        private static ResultCardModel Card(Envelope env)
        {
            var ev = new TurnEvidence();
            ev.Add(Request(), new[] { env });
            return ResultCardModel.From(ev, En);
        }

        [Fact]
        public void SixtyWritten_FiftyRowsListed_TheCardSaysSixty()
        {
            var m = Card(DoorsCommit(60));
            var g = Assert.Single(m.Groups);
            Assert.False(g.IsType);
            Assert.Equal("Doors", g.TypeName);
            Assert.Equal(60, g.Instances);
            Assert.Equal(50, g.Listed);
            Assert.True(g.ListCut);
            Assert.Equal(60, m.InstanceCount);
            Assert.Equal((60, 60), m.ReadBack());
            Assert.Equal("Read back from Revit: 60 of 60 match", m.ReadBackText(En));
            Assert.Equal("60 elements", ChangesBodyText.CountLabel(m.InstanceCount, En));
            Assert.Equal("60 elements · 50 of 60 listed", ChangesBodyText.TileCount(g, En));
            Assert.Equal("60 elemen · 50 daripada 60 disenaraikan", ChangesBodyText.TileCount(g, ReceiptLanguage.Ms));
            Assert.StartsWith("1 change applied", m.Headline);
            Assert.Equal("60 elements", m.Counts);
            // The card holds the 50 ids it was given: select says what it selects.
            Assert.Equal(50, g.Selectable);
        }

        [Fact]
        public void SixtyWritten_FiftyEightReadBack_TwoMismatches()
        {
            var m = Card(DoorsCommit(60, mismatches: 2));
            var g = Assert.Single(m.Groups);
            Assert.Equal(60, g.Instances);
            Assert.Equal(2, m.Mismatches);
            Assert.Equal((58, 60), m.ReadBack());
            Assert.Equal("Read back from Revit: 58 of 60 match", m.ReadBackText(En));
            Assert.Contains("2 mismatches", m.Detail);
        }

        [Fact]
        public void TheFullListKeptThisSession_GivesTheGroupEveryId()
        {
            var m = Card(DoorsCommit(60, keepFullList: true));
            var g = Assert.Single(m.Groups);
            Assert.Equal(60, g.Ids.Count);
            Assert.Equal(60, g.Instances);
            Assert.Equal(50, g.Listed);
            Assert.Equal(60, g.Selectable);
            Assert.Equal("60 elements · 50 of 60 listed", ChangesBodyText.TileCount(g, En));
        }

        [Fact]
        public void AWriteThatListsEveryElement_IsCountedByItsRows_AsBefore()
        {
            var m = Card(DoorsCommit(12));
            var g = Assert.Single(m.Groups);
            Assert.Equal(12, g.Instances);
            Assert.Equal(12, g.Listed);
            Assert.False(g.ListCut);
            Assert.Equal("12 elements", ChangesBodyText.TileCount(g, En));
            Assert.Equal("Read back from Revit: 12 of 12 match", m.ReadBackText(En));
        }

        [Fact]
        public void ATypeWrite_IsUnchanged()
        {
            var data = new
            {
                param = "Rated_Current", type_param = true, matched = 52, changed = 2, unchanged = 0,
                types = new[]
                {
                    new { id = 5001L, name = "Standard", family = "Alpha Downlight", category = "Lighting Fixtures", instances_in_model = 38 },
                    new { id = 5002L, name = "Standard", family = "Beta Downlight", category = "Lighting Fixtures", instances_in_model = 14 },
                },
                instances_affected = 52, instances_category = "Lighting Fixtures",
            };
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Count = 2, Summary = "Set Rated_Current",
                Data = JsonDocument.Parse(JsonSerializer.Serialize(data)).RootElement.Clone(),
                Changes = new ChangeReportDto { Touched = new ChangesDto { Modified = new[] { 5001L, 5002L }.Select(id => new FieldChangeDto
                    { Id = id, Field = "Rated_Current", Before = "0 A", After = "0.04 A", Scope = ResultTargetScopes.Type, Element = "Standard" }).ToList() } },
                Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 2, Mismatches = new List<MismatchDto>() },
            };
            var m = Card(env);
            Assert.Equal(2, m.Groups.Count);
            Assert.All(m.Groups, g => { Assert.True(g.IsType); Assert.Equal(0, g.Listed); Assert.False(g.ListCut); });
            Assert.Equal(new[] { 38, 14 }, m.Groups.Select(g => g.Instances));
            Assert.Equal("Read back from Revit: 2 of 2 types match · affects 52 Lighting Fixtures", m.ReadBackText(En));
            Assert.Equal("38 elements", ChangesBodyText.TileCount(m.Groups[0], En));
            Assert.Equal(38, m.Groups[0].Selectable);
        }

        // ---- the key: its own meaning (marks in this view), the card's total ----

        private static List<ElementPlace> Places(HighlightSet set, int inView)
        {
            var group = set.Kinds.FindIndex(k => k.Outcome == MarkOutcome.Changed);
            return set.Kinds[group].Ids.Select((id, i) => new ElementPlace { Id = id, Group = group, InActiveView = i < inView }).ToList();
        }

        [Fact]
        public void TheKey_SaysHowManyOfTheSixtyAreInThisView()
        {
            var env = DoorsCommit(60, keepFullList: true);
            var card = Card(env);
            var set = HighlightSet.From(card, new[] { env });
            var key = MarkKey.Build(set, Places(set, 56), "Level 1 Plan");
            Assert.Equal(60, key.Total);
            Assert.Equal(56, key.Here);
            Assert.Equal("60 doors changed", key.Title);
            Assert.Equal("56 of 60 are visible in this view", key.Where);
        }

        [Fact]
        public void TheKey_FromACardWithNoFullList_NeverCountsFewerThanTheCard()
        {
            var env = DoorsCommit(60);
            var card = Card(env);
            var set = HighlightSet.From(card, new[] { env });
            var changed = set.Kinds.Single(k => k.Outcome == MarkOutcome.Changed);
            Assert.Equal(50, changed.Ids.Count);
            Assert.Equal(60, changed.Count);
            var key = MarkKey.Build(set, Places(set, 46), "Level 1 Plan");
            Assert.Equal(60, key.Total);
            Assert.Equal(46, key.Here);
        }
    }
}
