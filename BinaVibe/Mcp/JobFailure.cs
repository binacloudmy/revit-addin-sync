#nullable enable
// Why an McpJob completed with an error rather than a result (AI-298).
//
// The pump and the drainer used to say only a message; the copilot's read
// executor then sorted failures by what the message started with or contained
// ("abandoned...", "...not initialised"). The reason is now set where the job
// is failed, and the message stays what the drafter and the log read.
// Revit-free so ReadFailures (and its tests) can use it.

namespace BinaVibe.Mcp
{
    public enum JobFailure
    {
        /// <summary>The tool threw, or the drainer or watchdog gave only a message (default).</summary>
        Crashed = 0,
        /// <summary>A forced add-in update is pending: nothing may run.</summary>
        UpdateBlocked,
        /// <summary>The caller cancelled the job before it started.</summary>
        Abandoned,
        /// <summary>The Revit tool pump was never started.</summary>
        PumpNotRunning,
    }
}
