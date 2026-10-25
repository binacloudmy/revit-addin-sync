#nullable enable
// Routes protocol-3 tool_request calls (plan §4 Task 0.2; owned by W0).
// Revit-free: it routes by name, so it links into Tests and the scratch harness.
//
//   RunReadsAsync  -> IReadExecutor      (W2's ReadExecutor)
//   RunWritesAsync -> IWriteBatchRunner  (W3's WriteBatchRunner)
//
// Both are settable properties; W5 wires them at integration. Before anything
// runs, every call is checked against the catalogue (ToolNames.AddIn, the same
// 7 names as CopilotToolCatalog.All):
//   - a name outside the catalogue          -> NotAllowed
//   - set_parameters in a read request      -> NotAllowed (writes must reach the confirm card)
//   - a read tool in a write request        -> NotAllowed
// run_revit_code may arrive in either; W4 classifies it. An unwired executor
// (null) answers not_implemented, because nothing ran. A read executor that
// throws NotImplementedException also answers not_implemented; a write runner
// that throws anything answers runtime_error "may or may not have been
// applied". Cancellation propagates. Results keep call order, one per call.
//
// The plain `Envelope` below, in namespace BinaVibe.Copilot with a top-of-file
// using, is deliberate: it stops compiling if a namespace named
// BinaVibe.Copilot.Envelope ever comes back (CS0118; ruling R8).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot
{
    public sealed class CopilotToolHost
    {
        /// <summary>Runs read requests; null until W2's ReadExecutor is wired.</summary>
        public IReadExecutor? ReadExecutor { get; set; }

        /// <summary>Runs write requests; null until W3's WriteBatchRunner is wired.</summary>
        public IWriteBatchRunner? WriteRunner { get; set; }

        public Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct) =>
            RouteAsync(calls, AllowedInReads, write: false, async allowed =>
            {
                var executor = ReadExecutor;
                if (executor == null) return NotImplemented(allowed);
                return await executor.RunAsync(turnId, allowed, snapshot, ct).ConfigureAwait(false);
            });

        public Task<Envelope[]> RunWritesAsync(WriteBatchRequest req, CancellationToken ct) =>
            RouteAsync(req.Calls, AllowedInWrites, write: true, async allowed =>
            {
                var runner = WriteRunner;
                if (runner == null) return NotImplemented(allowed);
                var batch = new WriteBatchRequest
                {
                    TurnId = req.TurnId,
                    RequestId = req.RequestId,
                    Calls = allowed,
                    Description = req.Description,
                    Snapshot = req.Snapshot,
                };
                return await runner.RunAsync(batch, ct).ConfigureAwait(false);
            });

        private static bool AllowedInReads(string tool) =>
            ToolNames.AddIn.Contains(tool) && ToolNames.KindOf(tool) is "read" or "code";

        private static bool AllowedInWrites(string tool) =>
            ToolNames.AddIn.Contains(tool) && ToolNames.KindOf(tool) is "write" or "code";

        private static Envelope[] NotImplemented(IReadOnlyList<ToolCallDto> calls) =>
            calls.Select(c => EnvelopeFactory.NotImplemented(c.Tool)).ToArray();

        private static async Task<Envelope[]> RouteAsync(
            IReadOnlyList<ToolCallDto> calls,
            Func<string, bool> allowedHere,
            bool write,
            Func<IReadOnlyList<ToolCallDto>, Task<Envelope[]>> run)
        {
            var results = new Envelope?[calls.Count];
            var allowed = new List<ToolCallDto>();
            var slots = new List<int>();
            for (var i = 0; i < calls.Count; i++)
            {
                if (allowedHere(calls[i].Tool ?? "")) { allowed.Add(calls[i]); slots.Add(i); }
                else results[i] = EnvelopeFactory.NotAllowed(calls[i].Tool ?? "");
            }

            if (allowed.Count > 0)
            {
                Envelope[] ran;
                try
                {
                    ran = await run(allowed).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (NotImplementedException) when (!write)
                {
                    // A stubbed read tool changed nothing: not_implemented is the truth.
                    ran = NotImplemented(allowed);
                }
                catch (Exception ex)
                {
                    // Not silent: the model and the pane see the failure. A write
                    // runner that threw (even NotImplementedException) may already
                    // have committed part of the batch, so never call it clean.
                    var message = write
                        ? "The write runner failed (" + ex.GetType().Name + ": " + ex.Message + "). Changes may or may not have been applied; inspect before retrying."
                        : "The read failed in the add-in (" + ex.GetType().Name + ": " + ex.Message + ").";
                    ran = allowed.Select(_ => EnvelopeFactory.Failed(ErrorCodes.RuntimeError, message)).ToArray();
                }

                for (var j = 0; j < slots.Count; j++)
                    results[slots[j]] = j < ran.Length && ran[j] != null
                        ? ran[j]
                        : EnvelopeFactory.Failed(ErrorCodes.RuntimeError, "The add-in returned no result for this call.");
            }

            return results.Select(r => r!).ToArray();
        }
    }
}
