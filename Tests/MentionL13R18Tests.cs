#nullable enable
// r18 check of the 58a4138b mention fix against live L13 (8cae1ff0 on 0.0.155, a build without that commit): "open " + the @ button +
// REFERENCE > Views > Structural Plans > PTBR_PELAN ASAS, then Send. The DB turn_start had text "open PTBR_PELAN ASAS" and no mention. On
// this branch the chip travels as data: the picker's tree node carries the view's element id, MentionChips.Records keeps it, MentionWire
// sends it, and the turn request's JSON has the mention with its id as a JSON number (the backend's copilot_mentions keeps an id only when
// it is an int, and only a mention whose name is in the message text).
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MentionL13R18Tests
    {
        private const string View = "PTBR_PELAN ASAS";
        private const long ViewId = 1289777;   // the view the live turn opened (ui applied view 1289777)

        private static List<MentionNode> Tree() => new List<MentionNode>
        {
            new MentionNode("view", "PTBR_PELAN TIANG ARAS 01") { IsActive = true, ElementId = 1289000 },
            MentionNode.Group("Views", new[]
            {
                MentionNode.Group("Structural Plans", new[] { new MentionNode("view", View) { ElementId = ViewId } }),
            }),
        };

        [Fact]
        public void APickedViewFromTheTree_ReachesTheTurnRequest_WithItsIdAsANumber()
        {
            var item = MentionSearch.Index(Tree()).Single(i => i.Kind == "view" && i.Name == View);
            Assert.Equal(ViewId, item.Node?.ElementId);

            var chips = new MentionChips();
            var (shown, _) = chips.Insert("open @", 5, 6, item);
            var sent = chips.Expand(shown).Trim();
            Assert.Equal("open " + View, sent);   // the message text names it (the backend keeps only mentions named in the text)

            var wire = MentionWire.For(chips.Records());
            var json = CopilotJson.Serialize(new TurnRequestDto { TurnId = "t", Text = sent, Mentions = wire });
            using var doc = JsonDocument.Parse(json);
            var m = Assert.Single(doc.RootElement.GetProperty("mentions").EnumerateArray());
            Assert.Equal("view", m.GetProperty("kind").GetString());
            Assert.Equal(View, m.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Number, m.GetProperty("id").ValueKind);
            Assert.Equal(ViewId, m.GetProperty("id").GetInt64());
            Assert.Contains(m.GetProperty("name").GetString()!, doc.RootElement.GetProperty("text").GetString());
        }

        [Fact]
        public void AViewNameSharedInOneGroup_GoesWithoutAnId_ButStillAsAMention()
        {
            // RevitMentionProvider gives no id when two views of one type share the name: the backend then gets the name only.
            var chips = new MentionChips();
            var (shown, _) = chips.Insert("open @", 5, 6, new MentionItem { Kind = "view", Name = View, Node = new MentionNode("view", View) });
            chips.Expand(shown);
            var wire = MentionWire.For(chips.Records());
            var json = CopilotJson.Serialize(new TurnRequestDto { TurnId = "t", Text = "open " + View, Mentions = wire });
            Assert.Contains("\"mentions\":[{\"kind\":\"view\",\"name\":\"PTBR_PELAN ASAS\"}]", json);
        }
    }
}
