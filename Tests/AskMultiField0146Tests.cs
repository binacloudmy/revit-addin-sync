#nullable enable
// 0.0.146 (backend core b8aaa4e7): an ask_field of type choice with "multi": true is drawn as tick boxes, none ticked at first, and
// answered as ONE string: the ticked choice ids joined with " | " ({"type": "t1 | t2"}). Single-choice fields are unchanged.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskMultiField0146Tests
    {
        private static AskCardModel Card(bool multi, bool withH = true)
        {
            var fields = new List<AskFieldDto>
            {
                new AskFieldDto { Id = "type", Label = "Door types", Type = AskFieldTypes.Choice, Multi = multi,
                    Choices = new List<AskOptionDto> { new AskOptionDto { Id = "t1", Label = "FD1" }, new AskOptionDto { Id = "t2", Label = "FD2" }, new AskOptionDto { Id = "t3", Label = "FD3" } } },
            };
            if (withH) fields.Add(new AskFieldDto { Id = "h", Label = "Height", Type = AskFieldTypes.Number, Unit = "mm" });
            return AskCardModel.From(new AskDto { RequestId = "r", Question = "Which types?", Purpose = AskPurposes.Clarify, Fields = fields }, ReceiptLanguage.En);
        }

        [Fact]
        public void TheMultiFlag_IsReadFromTheFrame()
        {
            var ask = JsonSerializer.Deserialize<AskFieldDto>("{\"id\":\"type\",\"label\":\"Types\",\"type\":\"choice\",\"multi\":true}")!;
            Assert.True(ask.Multi);
            Assert.True(Card(true).Fields[0].Multi);
            Assert.False(Card(false).Fields[0].Multi);
        }

        [Fact]
        public void AnswerForFields_JoinsTheTickedIds_WithSpacePipeSpace()
        {
            var card = Card(true);
            var answer = card.AnswerForFields(new Dictionary<string, string> { ["type"] = "t3 | t1", ["h"] = "2700" });
            Assert.Equal("t1 | t3", answer.Values["type"].GetString());   // one string, ids in the field's order
            Assert.Equal(2700, answer.Values["h"].GetDouble());
            // one tick is still one string; labels and unknown parts map or drop
            Assert.Equal("t2", card.AnswerForFields(new Dictionary<string, string> { ["type"] = "t2" }).Values["type"].GetString());
            Assert.Equal("t1 | t2", card.AnswerForFields(new Dictionary<string, string> { ["type"] = "FD1|x|t2" }).Values["type"].GetString());
            Assert.False(card.AnswerForFields(new Dictionary<string, string> { ["type"] = "" }).Values.ContainsKey("type"));
        }

        [Fact]
        public void SingleChoice_IsUnchanged()
        {
            var card = Card(false);
            Assert.Equal("t2", card.AnswerForFields(new Dictionary<string, string> { ["type"] = "t2" }).Values["type"].GetString());
            Assert.False(card.AnswerForFields(new Dictionary<string, string> { ["type"] = "t1 | t2" }).Values.ContainsKey("type"));
            card.Choose("type", "t1"); card.Choose("type", "t2");
            Assert.Equal("t2", card.Picks["type"]);                          // a pick replaces, never ticks
        }

        [Fact]
        public void Ticks_StartEmpty_Toggle_AndGateSend()
        {
            var card = Card(true, withH: false);
            var f = card.Fields[0];
            Assert.False(card.Picks.ContainsKey("type"));                    // no pre-tick
            Assert.False(card.IsTicked(f, "t1"));
            Assert.False(card.CanSend);                                      // nothing ticked: Submit stays off
            card.Choose("type", "t2"); card.Choose("type", "t1");
            Assert.True(card.IsTicked(f, "t1") && card.IsTicked(f, "t2") && !card.IsTicked(f, "t3"));
            Assert.True(card.CanSend);
            Assert.False(card.PickSends("type", "t1"));                      // a tick on a one-field form never sends at once
            Assert.Equal("t1 | t2", card.AnswerForDraft().Values["type"].GetString());
            Assert.Equal("Door types: FD1, FD2", card.SummaryForDraft());
            card.Choose("type", "t1"); card.Choose("type", "t2");            // untick both
            Assert.False(card.CanSend);
            Assert.Equal(AskFieldShape.Tiles, AskCardModel.ShapeOf(f));
        }

        [Fact]
        public void ARequiredMultiField_WithNothingTicked_IsMissing()
        {
            var card = Card(true);
            card.Fields[0].Required = true;
            Assert.Equal(new[] { "type" }, card.MissingRequired(new Dictionary<string, string> { ["type"] = "", ["h"] = "1" }));
            Assert.Empty(card.MissingRequired(new Dictionary<string, string> { ["type"] = "t1 | t2" }));
        }
    }
}
