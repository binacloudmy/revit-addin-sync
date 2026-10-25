#nullable enable
// Protocol-3 SSE reader (lean-harness spec §9, plan §2.2; W5 Task 1). Revit-free.
//
// Wire format: "event: <type>\ndata: <json>\n\n". Every data object carries an
// integer seq that increases within a turn; CopilotTurnClient uses it to drop
// duplicates and to resume with GET .../events?after=<seq> when a stream drops.
//
// Parsing follows the WHATWG event-stream rules (comment lines start with ':',
// one leading space after "field:" is dropped, data lines join with "\n", a
// blank line dispatches, an event with no data is discarded) plus two
// tolerances learned from the old ToolLoopService client:
//   - an "event:" line that arrives while data is already buffered closes the
//     previous frame first, so a missed blank line cannot glue two frames;
//   - at end of stream a pending frame is delivered only when its data is
//     complete JSON. A frame cut off mid-JSON is dropped; the client replays it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RevitWebAppSync.Services
{
    /// <summary>One dispatched SSE frame.</summary>
    public sealed class SseEvent
    {
        public SseEvent(string type, string data, long? seq)
        {
            Type = type;
            Data = data;
            Seq = seq;
        }

        /// <summary>The "event:" field; "message" when the frame had none.</summary>
        public string Type { get; }

        /// <summary>The joined "data:" lines.</summary>
        public string Data { get; }

        /// <summary>The integer "seq" of the data object; null when the data is not
        /// a JSON object or has no integer seq.</summary>
        public long? Seq { get; }
    }

    public static class CopilotSse
    {
        public const string DefaultEventType = "message";

        /// <summary>Reads frames until the stream ends. Never closes the stream.</summary>
        public static async IAsyncEnumerable<SseEvent> Parse(
            Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            string? type = null;
            var data = new StringBuilder();
            var hasData = false;

            SseEvent? Take()
            {
                var ev = hasData ? Build(type, data.ToString()) : null;
                type = null;
                data.Clear();
                hasData = false;
                return ev;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                if (line.Length == 0)
                {
                    var ev = Take();
                    if (ev != null) yield return ev;
                    continue;
                }
                if (line[0] == ':') continue;   // comment / keep-alive

                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line.Substring(0, colon);
                var value = colon < 0 ? "" : line.Substring(colon + 1);
                if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);

                switch (field)
                {
                    case "event":
                        if (hasData)
                        {
                            var ev = Take();
                            if (ev != null) yield return ev;
                        }
                        type = value;
                        break;
                    case "data":
                        if (hasData) data.Append('\n');
                        data.Append(value);
                        hasData = true;
                        break;
                    default:
                        break;   // id, retry and unknown fields carry nothing we use
                }
            }

            if (hasData && IsCompleteJson(data.ToString()))
            {
                var last = Take();
                if (last != null) yield return last;
            }
        }

        private static SseEvent Build(string? type, string data) =>
            new SseEvent(string.IsNullOrEmpty(type) ? DefaultEventType : type!, data, SeqOf(data));

        /// <summary>The integer seq of a JSON object, or null.</summary>
        public static long? SeqOf(string data)
        {
            if (string.IsNullOrEmpty(data) || data.TrimStart().Length == 0 || data.TrimStart()[0] != '{') return null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                       && doc.RootElement.TryGetProperty("seq", out var s)
                       && s.ValueKind == JsonValueKind.Number
                       && s.TryGetInt64(out var v)
                    ? v
                    : (long?)null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsCompleteJson(string data)
        {
            try
            {
                using var _ = JsonDocument.Parse(data);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
