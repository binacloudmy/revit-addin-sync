#nullable enable
// Skeleton by W0; body owned by W2
// Runs a protocol-3 read request on Revit's thread and returns one envelope
// per call, in call order.
//
// All calls of a request run in one Revit-thread work item (RevitThread, the
// copilot read pump: Idling drain + busy watchdog). Before each call:
//   - the active document must be the one the turn's snapshot was taken against
//     (DocIdentity), else failed: doc_changed (spec §14, Review Focus 4);
//   - the tool must be in the catalogue and allowed in a read request.
// Reads run under CallbackScope Read (look under Look; run_revit_code manages
// its own), so nothing they do counts as an external change. A tool that
// throws becomes runtime_error; a stubbed one not_implemented; Revit busy
// becomes revit_busy for every call; cancellation before a call -> timeout.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;

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
            try
            {
                return await RevitThread.RunAsync(app => RunAll(app, turnId, calls, snapshot, ct), ct).ConfigureAwait(false);
            }
            catch (RevitBusyException ex)
            {
                return calls.Select(_ => ReadEnvelope.Busy(ex.Message)).ToArray();
            }
            catch (UpdateBlockedException ex)
            {
                return calls.Select(_ => ReadEnvelope.Fail(ErrorCodes.NotAllowed, ex.Message)).ToArray();
            }
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
                return EnvelopeFactory.NotAllowed(call.Tool ?? "");

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
                           ?? EnvelopeFactory.Failed(ErrorCodes.RuntimeError, tool.Name + " returned no result.");
            }
            catch (NotImplementedException)
            {
                return EnvelopeFactory.NotImplemented(tool.Name);
            }
            catch (Exception ex)
            {
                return EnvelopeFactory.Failed(ErrorCodes.RuntimeError,
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
