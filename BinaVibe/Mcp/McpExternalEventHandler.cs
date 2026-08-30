// McpExternalEventHandler — owns the McpJob queue and runs each tool
// implementation on Revit's main thread.
//
// Two drivers share this one queue + DrainOnce:
//   - McpJobPump (primary): a permanently-subscribed Idling handler that drains
//     and forces continuous idling via SetRaiseWithoutDelay until the queue is
//     empty. This is what guarantees prompt execution from the modeless pane.
//   - This ExternalEvent (Execute, below): a one-shot drain of the same queue,
//     retained for any caller that raises it. NOTE: McpServer no longer uses
//     this path — it enqueues via McpJobPump.Enqueue (shared pump + watchdog);
//     the private handler McpServer used to construct was never pump-drained.
//
// Job completion goes through McpJob.SetResult / SetError (idempotent, CAS-
// guarded) so a late drain that dequeues an already-timed-out / watchdog-failed
// job is a safe no-op.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Mcp
{
    public sealed class McpExternalEventHandler : IExternalEventHandler
    {
        public ConcurrentQueue<McpJob> Pending { get; } = new();

        /// <summary>Retained for source compatibility with the callers that
        /// set it (McpServer / McpTunnelClient). Unused — the handler no
        /// longer re-raises itself.</summary>
        public ExternalEvent? Event { get; set; }

        public string GetName() => "BinaVibe.Mcp.ExternalEventHandler";

        // ExternalEvent path (gated inbound MCP / tunnel): one-shot drain.
        public void Execute(UIApplication app) => McpJobPump.DrainViaExternalEvent(app);

        /// <summary>Drain every queued job once, on the Revit UI thread. Returns
        /// the number completed (so the pump can decrement its in-flight count).
        /// Skips jobs already completed by the watchdog/timeout, and abandoned
        /// (cancelled) jobs.</summary>
        public int DrainOnce(UIApplication app)
        {
            // Turn-receipt recorder must exist BEFORE the first mutate tx of
            // the first batch commits (idempotent, cheap).
            RevitWebAppSync.Services.TurnReceiptService.EnsureSubscribed(app);
            // Document revision tracking (spec §8.4) — flag-gated, additive.
            bool revisionTracking = false;
            try { revisionTracking = BinaVibe.Policy.VibeFlags.Load().RevisionTracking; } catch { }
            if (revisionTracking) BinaVibe.DocState.DocumentRevisionTracker.EnsureSubscribed(app);
            int n = 0;
            while (Pending.TryDequeue(out var job))
            {
                if (job.IsCompleted) { n++; continue; }   // watchdog/timeout already finished it

                if (job.Abandoned)
                {
                    job.SetError("abandoned: request was cancelled before execution");
                    n++;
                    continue;
                }

                job.TStarted = System.Diagnostics.Stopwatch.GetTimestamp();   // t1
                try
                {
                    var liveDoc = app.ActiveUIDocument?.Document;
                    // Stale check BEFORE any transaction: a mutation planned
                    // against a revision the drafter has since moved past is
                    // refused with a typed stale_document result (§8.4).
                    if (revisionTracking && liveDoc != null && job.Mutate && job.ExpectedRevision.HasValue)
                    {
                        var stale = BinaVibe.DocState.DocumentRevisionTracker.StaleError(
                            liveDoc, job.ExpectedRevision.Value, job.DocumentFingerprint);
                        if (stale != null)
                        {
                            job.TFinished = System.Diagnostics.Stopwatch.GetTimestamp();
                            job.SetResult(stale);
                            n++;
                            continue;
                        }
                    }
                    // Reconnect reconciliation (spec §8.5): a MUTATE key that
                    // already started/completed is never executed twice —
                    // answer from the ledger (cached result or "ambiguous").
                    var ledgerKey = job.Mutate ? job.IdempotencyKey : "";
                    if (!BinaVibe.DocState.OperationLedger.Instance.TryBegin(ledgerKey, DateTime.UtcNow, out var cachedResult))
                    {
                        job.TFinished = System.Diagnostics.Stopwatch.GetTimestamp();
                        job.SetResult(cachedResult);
                        n++;
                        continue;
                    }
                    // Arm the ambient scan-progress sink for THIS job only —
                    // tools tick McpProgress.Report from their element loops.
                    Dictionary<string, object?> result;
                    McpProgress.Begin(job.Progress);
                    try { result = ToolRegistry.Invoke(app, job.Tool, job.Args); }
                    finally { McpProgress.End(); }
                    if (revisionTracking && liveDoc != null && result != null)
                        BinaVibe.DocState.DocumentRevisionTracker.Stamp(liveDoc, result);
                    job.TFinished = System.Diagnostics.Stopwatch.GetTimestamp();   // t2
                    LogTimings(job);
                    bool toolOk = result == null || !(result.TryGetValue("ok", out var okv) && okv is bool okb && !okb);
                    if (toolOk) BinaVibe.DocState.OperationLedger.Instance.Complete(ledgerKey, result);
                    else BinaVibe.DocState.OperationLedger.Instance.Fail(ledgerKey, result?["error"]?.ToString() ?? "failed");
                    job.SetResult(result);
                }
                catch (Exception ex)
                {
                    job.TFinished = System.Diagnostics.Stopwatch.GetTimestamp();
                    LogTimings(job);
                    if (job.Mutate) BinaVibe.DocState.OperationLedger.Instance.Fail(job.IdempotencyKey, ex.Message);
                    job.SetError(ex.Message);
                    RevitWebAppSync.Services.TelemetryService.Track("tool_exec", "failed",
                        new { tool = job.Tool, error_class = ex.GetType().Name });
                }
                n++;
            }
            return n;
        }

        /// <summary>Fast-fail every still-pending job with one message (idle
        /// watchdog: Revit can't service the queue — busy or modal dialog).
        /// Safe to call off the UI thread; never touches the Revit API.</summary>
        public int FailAllPending(string error)
        {
            int n = 0;
            while (Pending.TryDequeue(out var job))
            {
                if (job.IsCompleted) continue;
                job.SetError(error);
                n++;
            }
            return n;
        }

        // A-vs-B split: idle(t1-t0) = time Revit took to reach idle and start
        // the job; exec(t2-t1) = time the tool itself took (≈ transaction
        // commit + regen). Big idle => idle-starvation (A); big exec => regen
        // tax (B). Visible in DebugView / VS Output as "[BinaVibe][timing]".
        private static void LogTimings(McpJob job)
        {
            double freq = System.Diagnostics.Stopwatch.Frequency;
            double idleMs = job.TEnqueued > 0
                ? (job.TStarted - job.TEnqueued) * 1000.0 / freq
                : -1;
            double execMs = (job.TFinished - job.TStarted) * 1000.0 / freq;
            string verdict = idleMs > execMs ? "A:idle-starvation" : "B:regen-tax";
            System.Diagnostics.Debug.WriteLine(
                $"[BinaVibe][timing] tool={job.Tool} idle(t1-t0)={idleMs:F0}ms " +
                $"exec(t2-t1)={execMs:F0}ms dominant={verdict}");
        }
    }
}
