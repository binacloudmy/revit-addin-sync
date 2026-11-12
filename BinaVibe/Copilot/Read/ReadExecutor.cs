#nullable enable
// Runs a protocol-3 read request on Revit's thread and returns one envelope
// per call, in call order.
//
// All calls of a request run in one RevitThread work item (ruling R36): the same
// queue, Idling drainer, watchdog (busy / modal dialog -> revit_busy) and
// update gate as every other tool. Before each call:
//   - the active document must be the one the turn's snapshot was taken against
//     (DocIdentity), else failed: doc_changed (spec §14, Review Focus 4);
//   - the tool must be in the catalogue and allowed in a read request.
// Reads run under CallbackScope Read (look under Look; run_revit_code manages
// its own), so nothing they do counts as an external change. A tool that
// throws becomes runtime_error; one that is not implemented answers not_implemented; cancellation
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
using BinaVibe.Copilot.Writes;

namespace BinaVibe.Copilot.Read
{
    public sealed class ReadExecutor : IReadExecutor
    {
        private readonly IHandleStore _handles;
        // The same read-mode code asked again on the same model, view and selection does not run again (AI-327).
        private readonly ReadDedupe _dedupe = new ReadDedupe();

        public ReadExecutor(IHandleStore handles)
        {
            _handles = handles ?? throw new ArgumentNullException(nameof(handles));
        }

        public async Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct)
        {
            if (calls == null || calls.Count == 0) return Array.Empty<Envelope>();
            ct.ThrowIfCancellationRequested();

            var work = RevitThread.RunAsync(app => RunAll(app, turnId, calls, snapshot, ct), ct);
            if (await Task.WhenAny(work, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false) != work)
                throw new OperationCanceledException(ct);

            try
            {
                var done = await work.ConfigureAwait(false);
                if (done != null && done.Length == calls.Count) return done;
                return calls.Select(_ => Failure(null)).ToArray();
            }
            catch (OperationCanceledException)
            {
                return calls.Select(_ => ReadEnvelope.TimedOut("The turn was cancelled before this call ran.")).ToArray();
            }
            catch (Exception ex)
            {
                return calls.Select(_ => Failure(ex)).ToArray();
            }
        }

        /// <summary>A work item that never ran (watchdog, update gate) or crashed.</summary>
        private static Envelope Failure(Exception? ex)
        {
            var message = ex?.Message ?? "The read returned no result.";
            if (message == RevitWebAppSync.Services.UpdateGate.RefusalMessage)
                return ReadEnvelope.Fail(ErrorCodes.NotAllowed, message);
            if (ex is RevitBusyException)             // never reached Revit's thread: busy or a modal dialog
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
            string? dedupeKey = null;
            if (tool.Name == ToolNames.RunRevitCode)
            {
                try
                {
                    var selection = string.Join(",", uidoc.Selection.GetElementIds().Select(id => id.Value).OrderBy(v => v).Take(2000));
                    dedupeKey = ReadDedupe.Key(turnId ?? "", call.Args.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : call.Args.GetRawText(),
                        SnapshotService.ChangeSeqOf(doc), uidoc.ActiveView?.Id.Value ?? 0, selection);
                    if (_dedupe.TryGet(turnId ?? "", dedupeKey, out var again) && again != null) return again;
                }
                catch (Exception) { dedupeKey = null; /* no dedupe, never a failure */ }
            }
            try
            {
                using (ScopeFor(doc, tool.Name))
                {
                    var run = tool.Run(ctx, call.Args)
                              ?? ReadEnvelope.Fail(ErrorCodes.RuntimeError, tool.Name + " returned no result.");
                    if (dedupeKey != null) _dedupe.Put(turnId ?? "", dedupeKey, run);
                    return run;
                }
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
