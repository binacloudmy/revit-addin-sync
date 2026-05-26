// McpJob — one tool-call unit of work pushed from the HTTP listener
// thread to the Revit main thread.
//
// The HTTP thread fills `Tool` + `Args` then waits on `Completed`. The
// Revit main thread (via McpExternalEventHandler) reads the queue,
// invokes the tool implementation, fills `Result` or `Error`, and
// signals `Completed`.

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace BinaVibe.Mcp
{
    public sealed class McpJob
    {
        public string Tool { get; init; } = "";
        public JsonElement Args { get; init; }
        public ManualResetEventSlim Completed { get; } = new(initialState: false);

        // Result of the call. Exactly one of these is non-null when
        // Completed is set.
        public Dictionary<string, object?>? Result { get; set; }
        public string? Error { get; set; }
    }
}
