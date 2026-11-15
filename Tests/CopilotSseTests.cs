#nullable enable
// W5 Task 1: the protocol-3 SSE reader (CopilotSse). Revit-free; runs in the
// macOS scratch harness. Wire format (plan §2.2): "event: <type>\ndata: <json>\n\n",
// every data carrying an integer seq that increases within a turn.

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotSseTests
    {
        private static Stream StreamOf(string wire) => new MemoryStream(Encoding.UTF8.GetBytes(wire));

        private static async Task<List<SseEvent>> ReadAll(string wire)
        {
            var list = new List<SseEvent>();
            await foreach (var e in CopilotSse.Parse(StreamOf(wire)))
                list.Add(e);
            return list;
        }

        [Fact]
        public async Task ParsesEventsAndSeq()
        {
            var wire =
                "event: progress\n" +
                "data: {\"seq\":1,\"kind\":\"thinking\",\"text\":\"Reading\"}\n" +
                "\n" +
                // CRLF line endings and a data value split over two data lines.
                "event: reply_delta\r\n" +
                "data: {\"seq\":2,\r\n" +
                "data:  \"text\":\"Siap\"}\r\n" +
                "\r\n" +
                "event: turn_end\n" +
                "data: {\"seq\":3,\"status\":\"done\",\"session_id\":\"s1\",\"receipt_ids\":[]}\n" +
                "\n";

            var events = await ReadAll(wire);

            Assert.Equal(3, events.Count);
            Assert.Equal("progress", events[0].Type);
            Assert.Equal(1, events[0].Seq);
            Assert.Equal("{\"seq\":1,\"kind\":\"thinking\",\"text\":\"Reading\"}", events[0].Data);

            Assert.Equal("reply_delta", events[1].Type);
            Assert.Equal(2, events[1].Seq);
            // Multi-line data joins with "\n"; one leading space after the colon is dropped.
            Assert.Equal("{\"seq\":2,\n \"text\":\"Siap\"}", events[1].Data);

            Assert.Equal("turn_end", events[2].Type);
            Assert.Equal(3, events[2].Seq);
        }

        [Fact]
        public async Task ToleratesKeepAliveComments()
        {
            var wire =
                ": ping\n" +
                "\n" +
                ":keep-alive\n" +
                "retry: 3000\n" +
                "id: 7\n" +
                "\n" +                         // dispatch with no data: nothing emitted
                "event: progress\n" +
                ": ping between fields\n" +
                "data: {\"seq\":4,\"kind\":\"running\",\"text\":\"x\"}\n" +
                "\n" +
                "\n\n" +
                "event: ask\n" +
                "data: {\"seq\":5,\"request_id\":\"r\",\"question\":\"q\",\"multi\":false,\"purpose\":\"clarify\"}";
            // No trailing blank line on the last frame: complete JSON is still delivered.

            var events = await ReadAll(wire);

            Assert.Equal(2, events.Count);
            Assert.Equal("progress", events[0].Type);
            Assert.Equal(4, events[0].Seq);
            Assert.Equal("ask", events[1].Type);
            Assert.Equal(5, events[1].Seq);
        }

        [Fact]
        public async Task DropsTruncatedFrameAtEndOfStream()
        {
            // The connection dropped mid-frame: the partial JSON is not an event
            // (the client replays it with events?after=seq instead).
            var wire =
                "event: reply_delta\ndata: {\"seq\":1,\"text\":\"a\"}\n\n" +
                "event: tool_request\ndata: {\"seq\":2,\"request_id\":\"r\",\"ki";

            var events = await ReadAll(wire);

            Assert.Single(events);
            Assert.Equal(1, events[0].Seq);
        }

        [Fact]
        public async Task EventLineWithoutBlankLineStartsNextFrame()
        {
            // A missed blank line must not glue two frames' data together.
            var wire =
                "event: reply_delta\ndata: {\"seq\":1,\"text\":\"a\"}\n" +
                "event: reply_delta\ndata: {\"seq\":2,\"text\":\"b\"}\n\n";

            var events = await ReadAll(wire);

            Assert.Equal(2, events.Count);
            Assert.Equal(1, events[0].Seq);
            Assert.Equal(2, events[1].Seq);
        }

        [Fact]
        public async Task MissingOrNonIntegerSeqIsNull()
        {
            var wire =
                "event: error\ndata: {\"code\":\"x\",\"message\":\"y\"}\n\n" +
                "event: error\ndata: {\"seq\":\"3\"}\n\n" +
                "data: not json\n\n";

            var events = await ReadAll(wire);

            Assert.Equal(3, events.Count);
            Assert.Null(events[0].Seq);
            Assert.Null(events[1].Seq);
            Assert.Equal("message", events[2].Type);   // SSE default type
            Assert.Null(events[2].Seq);
        }
    }
}
