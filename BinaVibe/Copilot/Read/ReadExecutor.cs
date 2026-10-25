#nullable enable
// Skeleton by W0; body owned by W2
// Runs a protocol-3 read request on Revit's thread and returns one envelope
// per call, in call order.
//
// All calls of a request run in one McpJob work item (ruling R36): the same
// queue, Idling drainer, idle watchdog (busy / modal dialog -> revit_busy) and
// update gate as every other tool. Before each call:
//   - the active document must be the one the turn's snapshot was taken against
//     (DocIdentity), else failed: doc_changed (spec §14, Review Focus 4);
//   - the tool must be in the catalogue and allowed in a read request.
// Reads run under CallbackScope Read (look under Look; run_revit_code manages
// its own), so nothing they do counts as an external change. A tool that
// throws becomes runtime_error; a stubbed one not_implemented; cancellation
// before a call -> timeout.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using BinaVibe.Mcp;

namespace BinaVibe.Copilot.Read
{
    public sealed class ReadExecutor : IReadExecutor
    {
        private readonly IHandleStore _handles;

        public ReadExecutor(IHandleStore handles)
        {
            _handles = handles ?? throw new ArgumentNullException(nameof(handles));
        }

        public async Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct)
        {
            if (calls == null || calls.Count == 0) return Array.Empty<Envelope>();
            ct.ThrowIfCancellationRequested();

            var job = new McpJob
            {
                Tool = "copilot_read:" + string.Join(",", calls.Select(c => c.Tool)),
                Work = app => RunAll(app, turnId, calls, snapshot, ct),
            };
            McpJobPump.Enqueue(job);

            using (ct.Register(() => job.Abandoned = true))
            {
                var cancelled = Task.Delay(Timeout.Infinite, ct);
                if (await Task.WhenAny(job.Done.Task, cancelled).ConfigureAwait(false) != job.Done.Task)
                    throw new OperationCanceledException(ct);
            }

            if (job.Error == null && job.WorkResult is Envelope[] done && done.Length == calls.Count) return done;
            return calls.Select(_ => Failure(job)).ToArray();
        }

        /// <summary>A work item that never ran (watchdog, update gate, abandoned) or crashed.</summary>
        private static Envelope Failure(McpJob job)
        {
            var message = job.Error ?? "The read returned no result.";
            if (message == RevitWebAppSync.Services.UpdateGate.RefusalMessage)
                return ReadEnvelope.Fail(ErrorCodes.NotAllowed, message);
            if (message.StartsWith("abandoned", StringComparison.Ordinal))
                return ReadEnvelope.TimedOut("The turn was cancelled before this call ran.");
            if (message.IndexOf("not initialised", StringComparison.OrdinalIgnoreCase) >= 0)
                return ReadEnvelope.Fail(ErrorCodes.RuntimeError, "The add-in's Revit tool pump is not running (" + message + ").");
            if (job.TStarted == 0)                    // never reached Revit's thread: busy or a modal dialog
                return ReadEnvelope.Busy(message);
            return ReadEnvelope.Fail(ErrorCodes.RuntimeError, "The read failed in the add-in (" + message + "). Nothing was changed.");
        }

        private Envelope[] RunAll(UIApplication app, string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            var live = doc == null ? "" : SnapshotService.DocFingerprint(doc);
            var results = new Envelope[calls.Count];
            for (var i = 0; i < calls.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    results[i] = ReadEnvelope.TimedOut("The turn was cancelled before this call ran.");
                    continue;
                }
                results[i] = RunOne(app, uidoc, doc, live, turnId, calls[i], snapshot);
            }
            return results;
        }

        private Envelope RunOne(UIApplication app, UIDocument? uidoc, Document? doc, string live, string turnId, ToolCallDto call, SnapshotModel snapshot)
        {
            if (doc == null || uidoc == null)
                return snapshot?.Doc != null
                    ? ReadEnvelope.Fail(ErrorCodes.DocChanged, "The document this turn was about is no longer open. Ask the drafter to open it and resend.")
                    : ReadEnvelope.Fail(ErrorCodes.NotFound, "No Revit document is open.", Array.Empty<string>());
            if (!DocIdentity.Matches(live, snapshot))
                return ReadEnvelope.Fail(ErrorCodes.DocChanged,
                    snapshot?.Doc == null
                        ? "This turn started with no document open; '" + doc.Title + "' is open now. Ask the drafter to resend."
                        : "The active document changed since this turn started (it was '" + snapshot.Doc.Title + "', now '" + doc.Title + "'). Ask the drafter to resend.");

            var tool = CopilotToolCatalog.Find(call.Tool);
            if (tool == null || (tool.Kind != ToolKind.Read && tool.Kind != ToolKind.Code))
                return ReadEnvelope.Fail(ErrorCodes.NotAllowed, "'" + (call.Tool ?? "") + "' is not an add-in read tool that may run here.");

            var ctx = new ToolContext
            {
                App = app,
                Doc = doc,
                UiDoc = uidoc,
                TurnId = turnId ?? "",
                CallId = call.CallId ?? "",
                IdempotencyKey = call.IdempotencyKey ?? "",
                Handles = _handles,
                TurnSnapshot = snapshot ?? new SnapshotModel(),
            };
            try
            {
                using (ScopeFor(doc, tool.Name))
                    return tool.Run(ctx, call.Args)
                           ?? ReadEnvelope.Fail(ErrorCodes.RuntimeError, tool.Name + " returned no result.");
            }
            catch (NotImplementedException)
            {
                return ReadEnvelope.Fail(ErrorCodes.NotImplemented, tool.Name + " is not implemented in this add-in build.");
            }
            catch (Exception ex)
            {
                return ReadEnvelope.Fail(ErrorCodes.RuntimeError,
                    tool.Name + " failed in the add-in (" + ex.GetType().Name + ": " + ex.Message + "). Nothing was changed.");
            }
        }

        // run_revit_code classifies itself and owns its scope (W4).
        private static IDisposable? ScopeFor(Document doc, string tool) => tool switch
        {
            ToolNames.RunRevitCode => null,
            ToolNames.Look => CallbackScope.Enter(doc, CallbackKind.Look),
            _ => CallbackScope.Enter(doc, CallbackKind.Read),
        };
    }
}
