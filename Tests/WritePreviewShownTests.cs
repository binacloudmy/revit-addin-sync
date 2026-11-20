// r18 force fix (a), live 4e70cf53: "What I will write" showed Width 11.811023622 and Height 7.874015748 for "300 x 200" on a 14"x4"
// duct: the cloud sends a size as the bare number in the parameter's display unit (inches here), and now also the value as asked
// (args.shown), which the card shows. Pure.
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WritePreviewShownTests
    {
        private static ToolCallDto Call(string args) =>
            new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = ToolNames.SetParameters, Args = JsonDocument.Parse(args).RootElement.Clone() };

        [Fact]
        public void AConvertedSize_ShowsTheValueAsAsked()
        {
            var calls = new[]
            {
                Call("{\"param\":\"Width\",\"value\":\"11.811023622\",\"shown\":\"300 mm\",\"target\":{\"ids\":[1382112]}}"),
                Call("{\"param\":\"Height\",\"value\":\"7.874015748\",\"shown\":\"200 mm\",\"target\":{\"ids\":[1382112]}}"),
            };
            var records = WritePreviewText.Records(calls, ms: false);
            Assert.Equal("300 mm", records[0].Value);
            Assert.Equal("200 mm", records[1].Value);
            Assert.Equal("Width: 300 mm\nHeight: 200 mm", WritePreviewText.Lines(calls, ms: false));
        }

        [Fact]
        public void WithoutShown_TheValueIsShownAsBefore()
        {
            var calls = new[] { Call("{\"param\":\"Comments\",\"value\":\"Checked\",\"target\":{\"ids\":[1]}}"), Call("{\"param\":\"Width\",\"value\":\"12\",\"shown\":\"  \",\"target\":{\"ids\":[1]}}") };
            Assert.Equal("Checked", WritePreviewText.Records(calls, ms: false)[0].Value);
            Assert.Equal("12", WritePreviewText.Records(calls, ms: false)[1].Value);
        }
    }
}
