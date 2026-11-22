#nullable enable
// W5 Task 2: the compiled-in per-turn budget (spec §7.1, §13): 60 tool calls,
// 2 MB returned, 8 images per turn. The cloud may narrow these, never raise
// them. A refused read never ran; a committed write is never rewritten as a
// failure (only its bulky data is dropped). Revit-free.

using System.Linq;
using System.Text;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TurnBudgetTests
    {
        private static ToolCallDto Call(string id, string tool = "inspect") => new ToolCallDto { CallId = id, Tool = tool };

        private static Envelope Read(string summary = "ok") => new Envelope { Ok = true, Status = "done", Phase = "read", Summary = summary };

        private static Envelope Image() => new Envelope
        {
            Ok = true, Status = "done", Phase = "read", Summary = "image",
            Image = new ImageDto { Base64 = "iVBORw0KGgo=", Width = 64, Height = 32, Caption = "c" },
        };

        [Fact]
        public void CompiledInLimits()
        {
            Assert.Equal(60, TurnBudget.MaxCalls);
            Assert.Equal(2L * 1024 * 1024, TurnBudget.MaxBytes);
            Assert.Equal(8, TurnBudget.MaxImages);
            var b = new TurnBudget();
            Assert.Equal(60, b.CallLimit);
            Assert.Equal(TurnBudget.MaxBytes, b.ByteLimit);
            Assert.Equal(8, b.ImageLimit);
        }

        [Fact]
        public void Admit_StopsAtTheCallLimit()
        {
            var b = new TurnBudget();
            var first = b.Admit(Enumerable.Range(0, 58).Select(i => Call("a" + i)).ToList());
            Assert.All(first, Assert.True);

            var second = b.Admit(new[] { Call("x"), Call("y"), Call("z") });

            Assert.Equal(new[] { true, true, false }, second);
            Assert.Equal(60, b.CallsUsed);
            Assert.False(b.Admit(new[] { Call("w") })[0]);
        }

        [Fact]
        public void Narrow_NeverRaises()
        {
            var b = new TurnBudget();
            b.Narrow(calls: 100, bytes: 10L * 1024 * 1024, images: 50);
            Assert.Equal(60, b.CallLimit);
            Assert.Equal(TurnBudget.MaxBytes, b.ByteLimit);
            Assert.Equal(8, b.ImageLimit);

            b.Narrow(calls: 10, bytes: 1000, images: 2);
            Assert.Equal(10, b.CallLimit);
            Assert.Equal(1000, b.ByteLimit);
            Assert.Equal(2, b.ImageLimit);

            b.Narrow(calls: 20, bytes: null, images: -1);   // a later raise is ignored; nonsense too
            Assert.Equal(10, b.CallLimit);
            Assert.Equal(2, b.ImageLimit);
        }

        [Fact]
        public void Account_ImagesBeyondTheLimit_ReadsFailNotAllowed()
        {
            var b = new TurnBudget();
            b.Narrow(calls: null, bytes: null, images: 1);

            var first = b.Account(Image(), isWrite: false);
            var second = b.Account(Image(), isWrite: false);

            Assert.NotNull(first.Image);
            Assert.False(second.Ok);
            Assert.Equal(ErrorCodes.NotAllowed, second.Error!.Code);
            Assert.Null(second.Image);
            Assert.Equal(1, b.ImagesUsed);
        }

        [Fact]
        public void Account_BytesBeyondTheLimit_ReadFailsButCommittedWriteStaysTrue()
        {
            var b = new TurnBudget();
            b.Narrow(calls: null, bytes: 600, images: null);
            var big = new string('x', 800);

            var listing = Read("listing");
            SetData(listing, big);
            var read = b.Account(listing, isWrite: false);
            Assert.False(read.Ok);
            Assert.Equal(ErrorCodes.NotAllowed, read.Error!.Code);

            var write = new Envelope
            {
                Ok = true, Status = "done", Phase = "commit", Summary = "Set 34 ceilings.",
                Verified = new VerifiedDto { Method = "param_readback", Checked = 34 },
                Image = new ImageDto { Base64 = big, Width = 1, Height = 1, Caption = "" },
            };
            SetData(write, big);
            var kept = b.Account(write, isWrite: true);

            Assert.True(kept.Ok);                       // never rewritten as a failure
            Assert.Equal("done", kept.Status);
            Assert.Equal("commit", kept.Phase);
            Assert.Equal("Set 34 ceilings.", kept.Summary);
            Assert.Null(kept.Data);                     // bulky parts dropped
            Assert.Null(kept.Image);
            Assert.NotNull(kept.Verified);
        }

        [Fact]
        public void Describe_ShowsUseAgainstLimits()
        {
            var b = new TurnBudget();
            b.Admit(new[] { Call("a"), Call("b") });
            b.Account(Image(), isWrite: false);

            var text = b.Describe();

            Assert.Contains("2/60", text);
            Assert.Contains("1/8", text);
            Assert.Contains("MB", text);
        }

        private static void SetData(Envelope e, string payload) =>
            e.Data = System.Text.Json.JsonSerializer.SerializeToElement(new { blob = payload });
    }
}
