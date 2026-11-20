#nullable enable
// r17, tester 20 Nov: "check the MENTION (@) function". The picked mentions travel with the turn as structured context (kind, name, id).
using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MentionWireR17Tests
    {
        [Fact]
        public void For_KeepsKindNameAndId_OncePerMention()
        {
            var wire = MentionWire.For(new List<Mention>
            {
                new Mention("view", "PTBR_PELAN LANTAI ARAS 01", 847784),
                new Mention("view", "PTBR_PELAN LANTAI ARAS 01", 847784),
                new Mention("level", "PTBR_Aras 01", 311),
                new Mention("type", "Pintu Tunggal: PTh300a (door type)"),
                new Mention("", "x"),
            })!;
            Assert.Equal(3, wire.Count);
            Assert.Equal(847784L, wire[0].Id);
            Assert.Null(wire[2].Id);
            Assert.Null(MentionWire.For(null));
            Assert.Null(MentionWire.For(new List<Mention>()));
        }

        [Fact]
        public void InText_DropsMentionsTheMessageNoLongerHolds()
        {
            var wire = MentionWire.For(new List<Mention> { new Mention("view", "Level 1", 5), new Mention("level", "Level 2", 6), new Mention("selection", "the 3 selected walls") })!;
            var kept = MentionWire.InText(wire, "open Level 1")!;
            Assert.Equal(new[] { "Level 1", "the 3 selected walls" }, kept.ConvertAll(m => m.Name).ToArray());
            Assert.Null(MentionWire.InText(wire, ""));
        }

        [Fact]
        public void TurnRequest_SendsMentions_OnlyWhenThereAreSome()
        {
            var none = JsonSerializer.Serialize(new TurnRequestDto { TurnId = "t", Text = "x" });
            Assert.DoesNotContain("mentions", none);
            var some = JsonSerializer.Serialize(new TurnRequestDto { TurnId = "t", Text = "open Level 1", Mentions = new List<MentionDto> { new MentionDto { Kind = "view", Name = "Level 1", Id = 5 } } });
            Assert.Contains("\"mentions\":[{\"kind\":\"view\",\"name\":\"Level 1\",\"id\":5}]", some);
        }

        [Fact]
        public void Records_CarryThePickersElementId()
        {
            var chips = new MentionChips();
            var node = new MentionNode("view", "Level 1") { ElementId = 42 };
            var (text, _) = chips.Insert("open @", 5, 6, new MentionItem { Kind = "view", Name = "Level 1", Node = node });
            var rec = chips.Records();
            Assert.Single(rec);
            Assert.Equal(42L, rec[0].Id);
            Assert.Equal("view", rec[0].Kind);
            Assert.Contains("@", text);
        }
    }
}
