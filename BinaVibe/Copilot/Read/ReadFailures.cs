#nullable enable
// The envelope a copilot read answers when its work item never produced one
// (AI-298). Revit-free: ReadExecutor passes in what the job says (a typed
// JobFailure, whether Revit's thread ever started it, and the message), and
// nothing here reads the message to decide.

using BinaVibe.Copilot.Contracts;
using BinaVibe.Mcp;

namespace BinaVibe.Copilot.Read
{
    public static class ReadFailures
    {
        /// <param name="kind">Why the job failed (set where it was failed).</param>
        /// <param name="message">The job's error text, or null when it had none.</param>
        /// <param name="reachedRevitThread">The drainer picked the job up (McpJob.TStarted != 0).</param>
        public static Envelope For(JobFailure kind, string? message, bool reachedRevitThread)
        {
            var text = message ?? "The read returned no result.";
            switch (kind)
            {
                case JobFailure.UpdateBlocked:
                    return ReadEnvelope.Fail(ErrorCodes.NotAllowed, text);
                case JobFailure.Abandoned:
                    return ReadEnvelope.TimedOut("The turn was cancelled before this call ran.");
                case JobFailure.PumpNotRunning:
                    return ReadEnvelope.Fail(ErrorCodes.RuntimeError, "The add-in's Revit tool pump is not running (" + text + ").");
            }
            if (!reachedRevitThread)            // never reached Revit's thread: busy or a modal dialog
                return ReadEnvelope.Busy(text);
            return ReadEnvelope.Fail(ErrorCodes.RuntimeError, "The read failed in the add-in (" + text + "). Nothing was changed.");
        }
    }
}
