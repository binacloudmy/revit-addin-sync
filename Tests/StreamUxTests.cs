#nullable enable
// Stream UX (24 Nov): the backend sends the whole answer in one reply_delta right before turn_end (loop.py keeps a step's text until the
// step ends). The pane used to paint that prose under the running card, then the card replaced it with a jump. The live row now keeps
// the running card until the result arrives, and the result rises in. These pin when that rise happens.
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class StreamUxTests
    {
        private static ChatMessage Of(CpMsgKind kind) => new ChatMessage { Role = "ai", Kind = kind };

        [Fact]
        public void TheLiveRowReplacedByTheResult_Settles()
        {
            Assert.True(LiveSwap.Settles(Of(CpMsgKind.Thinking), Of(CpMsgKind.AiReply)));
            Assert.True(LiveSwap.Settles(Of(CpMsgKind.Thinking), Of(CpMsgKind.Clarify)));
            Assert.True(LiveSwap.Settles(Of(CpMsgKind.Thinking), Of(CpMsgKind.Attention)));
        }

        [Fact]
        public void ALiveTick_OrAnyOtherReplace_DoesNotSettle()
        {
            Assert.False(LiveSwap.Settles(Of(CpMsgKind.Thinking), Of(CpMsgKind.Thinking)));   // a stream tick redraws in place
            Assert.False(LiveSwap.Settles(Of(CpMsgKind.AiReply), Of(CpMsgKind.AiReply)));     // a card redrawn (undo, a type left as is)
            Assert.False(LiveSwap.Settles(Of(CpMsgKind.Clarify), Of(CpMsgKind.Clarify)));     // an answered question collapses
            Assert.False(LiveSwap.Settles(null, Of(CpMsgKind.AiReply)));
            Assert.False(LiveSwap.Settles(Of(CpMsgKind.Thinking), null));
        }
    }
}
