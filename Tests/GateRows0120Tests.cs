#nullable enable
// The server's gate question for a wide write: one row per listed type, "and K more" kept even when the server puts it on the end of the
// last listed line, and any other line of the question on its own line (not run together into one paragraph).
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class GateRows0120Tests
    {
        private static List<AskOptionModel> Gate() => new List<AskOptionModel>
        {
            new AskOptionModel { Id = "write_all", Label = "Write to all (300 elements)" },
            new AskOptionModel { Id = "write_cancel", Label = "Cancel, write nothing" },
        };

        [Fact]
        public void AMoreTailOnTheLastListedLine_IsARowAndTheMoreLine()
        {
            var q = "Before I write: 2 changes reach 300 elements.\n• Doors: 150\n• Windows: 100, and 3 more";
            var m = WideWriteModel.FromAsk(q, Gate(), ReceiptLanguage.En)!;
            var g = Assert.Single(m.Groups);
            Assert.Equal(new[] { "Doors", "Windows" }, g.Rows.Select(r => r.Name));
            Assert.Equal(new[] { 150, 100 }, g.Rows.Select(r => r.Count));
            Assert.Equal(3, g.MoreTypes);
            Assert.Equal("and 3 more", g.MoreText);
            Assert.Equal("", m.Detail);
        }

        [Fact]
        public void TheMalayMoreTail_IsKeptToo()
        {
            var m = WideWriteModel.FromAsk("Sebelum saya tulis.\n• Pintu: 100, dan 4 lagi", Gate(), ReceiptLanguage.Ms)!;
            Assert.Equal(4, m.Groups[0].MoreTypes);
            Assert.Equal("dan 4 lagi", m.Groups[0].MoreText);
        }

        [Fact]
        public void OtherLinesOfTheQuestion_StayOnTheirOwnLines()
        {
            var q = "Before I run code that changes elements.\nChange wall types in the whole model\nRunning: 2 other batches\n• Walls: 40";
            var m = WideWriteModel.FromAsk(q, Gate(), ReceiptLanguage.En)!;
            Assert.Equal("Change wall types in the whole model\nRunning: 2 other batches", m.Detail);
            Assert.Equal("Walls", m.Groups[0].Rows[0].Name);
        }

        [Fact]
        public void ATailOnALineThatIsNotARow_StaysAsWritten()
        {
            var m = WideWriteModel.FromAsk("Before I write.\nSome note, and 2 more", Gate(), ReceiptLanguage.En)!;
            Assert.Equal("Some note, and 2 more", m.Detail);
        }
    }
}
