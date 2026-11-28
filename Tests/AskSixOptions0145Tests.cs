#nullable enable
// The backend's ask_user now offers 2–6 options (protocol3 ask_option maxItems 6). The add-in's contract copies are mirrored
// byte for byte; a 5 or 6 option question is drawn as the option tiles with key hints 1..6, and the 2–3 option choice look
// is unchanged.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskSixOptions0145Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        private static AskCardModel Ask(int n) => AskCardModel.From(new AskDto
        {
            RequestId = "r", Question = "Which level?", Purpose = AskPurposes.Clarify,
            Options = Enumerable.Range(1, n).Select(i => new AskOptionDto { Id = "l" + i, Label = "Aras " + i }).ToList(),
        }, ReceiptLanguage.En);

        [Theory]
        [InlineData(5)]
        [InlineData(6)]
        public void FiveOrSixOptions_AreTiles_WithANumberKeyEach(int n)
        {
            var card = Ask(n);
            Assert.Equal(n, card.Options.Count);                 // none dropped
            Assert.Equal(AskCardKind.Options, card.Kind);
            Assert.False(card.IsChoiceLook);                       // the tiles, not the 2–3 choice look
            for (var i = 0; i < n; i++)
            {
                Assert.Equal((i + 1).ToString(), AskCardModel.OptionKeyHint(card.Options[i], i));
                Assert.Equal("l" + (i + 1), card.OptionForKey((char)('1' + i))!.Id);
            }
            Assert.Null(card.OptionForKey((char)('1' + n)));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void TwoOrThreeOptions_KeepTheChoiceLook(int n) => Assert.True(Ask(n).IsChoiceLook);

        [Fact]
        public void TheContractCopies_AllowSix()
        {
            Assert.Contains("\"options\": { \"type\": \"array\", \"minItems\": 2, \"maxItems\": 6, \"items\": { \"$ref\": \"#/$defs/ask_option\" } }",
                Src("contracts/copilot/protocol3.schema.json"));
            Assert.Contains("give 2–6 options", Src("contracts/copilot/tools.json"));
        }
    }
}
