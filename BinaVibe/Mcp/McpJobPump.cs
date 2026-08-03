// McpJobPump — drives McpJob execution from Revit's Idling event instead of
// relying on an ExternalEvent firing on its own.
//
// WHY (see docs/superpowers/specs/2026-06-30-revit-tool-execution-reliability-design.md):
// Revit runs ExternalEvent.Execute() / Idling handlers only during an Idling
// session, and in DEFAULT mode idle sessions stop when the user is inactive or a
// MODAL DIALOG is open. The Copilot is a modeless DockablePane, so a queued tool
// could sit unserviced indefinitely while the addin block-waited 600s ("stuck").
//
// Fix, layered:
//   L1  A permanently-subscribed Idling handler drains the queue and calls
//       IdlingEventArgs.SetRaiseWithoutDelay() while work remains, forcing Revit
//       to keep raising Idling continuously ("even if the user is totally
//       inactive") until the queue empties — then it stops (no CPU spin at rest).
//   L3/4 An idle WATCHDOG: if no idle fires within IdleGrace of enqueue, Revit is
//       not processing (busy / modal dialog) — fail the pending jobs fast with a
//       clear message instead of a 10-minute freeze. DialogBoxShowing tailors the
//       message ("close the open dialog"). IMPORTANT: the watchdog does NOT fail a
//       job merely because Revit is in the BACKGROUND. Revit raises Idling only
//       while it is the foreground app, so a backgrounded Revit looks identical to
//       a busy one from here — but it is healthy and resumes the moment it is
//       clicked. See OnWatchdog.
//
// ToolRegistry.Invoke still runs on the Revit UI thread (Idling handlers hold a
// valid API context); the ExternalEvent path (McpExternalEventHandler, gated
// inbound MCP server) shares the same queue + DrainOnce and still works.

using System;
using System.Diagnostics;
using System.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BinaVibe.Mcp
{
    public static class McpJobPump
    {
        // How long a job may wait for the FIRST idle before we declare Revit
        // unable to service it (busy / modal dialog) and fail fast. This is NOT
        // the tool execution budget — that lives in ToolLoopRunner (await Done).
        private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(6);

        // A DialogBoxShowing within this window of a fast-fail means a modal is
        // (almost certainly) the blocker — tailor the message.
        private static readonly TimeSpan DialogRecent = TimeSpan.FromSeconds(20);

        // How long a job may wait while Revit sits in the BACKGROUND before we give
        // up. Generous (Revit resumes the instant it is clicked, and the user may be
        // reading the answer in another window) but finite, because the pane's own
        // RunLocalToolAsync awaits with no timeout of its own.
        private static readonly TimeSpan BackgroundGrace = TimeSpan.FromMinutes(2);

        private static readonly object _gate = new();
        private static McpExternalEventHandler _handler;   // owns the queue + DrainOnce
        private static ExternalEvent _kick;                // optional wake (tunnel handler)
        private static UIControlledApplication _uiCtrl;
        private static UIApplication _uiApp;               // captured from Idling sender
        private static bool _wired;

        private static long _armedTicks;      // when the watchdog was last armed
        private static long _lastIdleTicks;   // last Idling callback
        private static long _lastDialogTicks; // last DialogBoxShowing
        private static Timer _watchdog;

        private static double Freq => Stopwatch.Frequency;
        private static long Now => Stopwatch.GetTimestamp();

        /// <summary>Wire the pump once at startup (UI thread). ``kick`` is the
        /// existing McpToolEvent — raised on enqueue as a best-effort wake; the
        /// Idling drainer is the guarantee.</summary>
        public static void Init(UIControlledApplication uiCtrl,
                                McpExternalEventHandler handler,
                                ExternalEvent kick)
        {
            lock (_gate)
            {
                if (_wired) return;
                _uiCtrl = uiCtrl;
                _handler = handler;
                _kick = kick;
                _uiCtrl.Idling += OnIdling;
                _wired = true;
            }
        }

        /// <summary>Record that a Revit modal dialog is showing — lets a fast-fail
        /// message point the user at it. Wire from App.DialogBoxShowing.</summary>
        public static void NoteDialogShown() => Volatile.Write(ref _lastDialogTicks, Now);

        /// <summary>Queue a job for execution on the Revit UI thread. Thread-safe;
        /// called from the tool-loop (threadpool) and the gated MCP server.</summary>
        public static void Enqueue(McpJob job)
        {
            if (_handler == null)   // not wired (e.g. unit context) — fail closed, never hang
            {
                job.SetError("Revit tool pump not initialised");
                return;
            }
            job.TEnqueued = Now;
            _handler.Pending.Enqueue(job);

            Volatile.Write(ref _armedTicks, Now);
            try { _kick?.Raise(); } catch { /* event disposed / not in context */ }
            ArmWatchdog();
        }

        // ── Idling drainer (L1) ──────────────────────────────────────────────
        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            Volatile.Write(ref _lastIdleTicks, Now);
            if (sender is UIApplication ua) _uiApp = ua;

            if (_handler.Pending.IsEmpty) return;   // nothing to do — stay in default idle cadence

            var app = _uiApp;
            if (app != null)
                _handler.DrainOnce(app);

            // Keep Revit raising Idling promptly until the queue empties. Must be
            // called every callback to stay in non-default mode.
            if (!_handler.Pending.IsEmpty)
                e.SetRaiseWithoutDelay();
        }

        /// <summary>One-shot drain entry for the ExternalEvent path (gated tunnel).</summary>
        public static void DrainViaExternalEvent(UIApplication app)
        {
            if (_handler != null && app != null) _handler.DrainOnce(app);
        }

        // ── Watchdog (L3/L4 fast-fail) ───────────────────────────────────────
        private static void ArmWatchdog()
        {
            lock (_gate)
            {
                if (_watchdog == null)
                    _watchdog = new Timer(OnWatchdog, null, IdleGrace, Timeout.InfiniteTimeSpan);
                else
                    _watchdog.Change(IdleGrace, Timeout.InfiniteTimeSpan);
            }
        }

        private static void OnWatchdog(object _)
        {
            // If an idle fired AFTER we armed, Revit is servicing the queue —
            // slow tools are the runner's per-job timeout's job, not ours.
            bool idleSinceArm = Volatile.Read(ref _lastIdleTicks) >= Volatile.Read(ref _armedTicks);
            if (idleSinceArm || _handler == null || _handler.Pending.IsEmpty)
                return;

            // No idle since the job was queued → Revit can't process it (busy or a
            // modal dialog). Fail every still-pending job fast with a clear cause.
            double sinceDialogMs = (Now - Volatile.Read(ref _lastDialogTicks)) * 1000.0 / Freq;
            bool dialogLikely = sinceDialogMs <= DialogRecent.TotalMilliseconds;
            // NOT-FOREGROUND IS NOT BUSY.
            //
            // Revit raises Idling only while it is the foreground application. Ask
            // the Copilot a question and then switch to another window — a browser,
            // an editor — and the backend's tool call lands 10-40s later against a
            // backgrounded Revit. No idle arrives, and this watchdog used to fail
            // the whole batch within 6s with "Revit is busy", which is simply
            // untrue: Revit is idle and healthy, just not focused. It resumes
            // raising Idling the instant it is clicked, so the job would have run.
            //
            // Measured 2026-07-31: four consecutive tool calls (analyze_model_
            // statistics, filter_elements, get_scene_overview) fast-failed ~8s
            // apart and never reached ToolRegistry.Invoke, while a Build clicked
            // inside Revit drained through the SAME queue in 0.27s.
            //
            // So only fast-fail when Revit is genuinely unable to serve us: a modal
            // dialog, or busy WHILE focused. Otherwise keep waiting and re-arm —
            // ToolLoopRunner's per-job timeout is the real ceiling.
            // Bounded, though: waiting forever would turn a wrong error into a hang.
            // The pane's own path (RunLocalToolAsync) awaits with no timeout, so the
            // ceiling has to live here. Past BackgroundGrace we give up and say so
            // in the message — naming focus, not a fictional "busy".
            if (!dialogLikely && !IsRevitForeground() && OldestPendingAge() < BackgroundGrace)
            {
                Debug.WriteLine(
                    "[BinaVibe][pump] no idle, but Revit is not the foreground window — "
                    + "waiting instead of fast-failing (it idles again once focused).");
                ArmWatchdog();
                return;
            }

            if (!dialogLikely && !IsRevitForeground())
            {
                int gaveUp = _handler.FailAllPending(
                    "Revit stayed in the background, so it never processed this. "
                    + "Click into the Revit window and ask again.");
                Debug.WriteLine(
                    $"[BinaVibe][pump] background-grace expired — failed {gaveUp} job(s)");
                return;
            }

            string msg = dialogLikely
                ? "Revit has a dialog open — close it and try again."
                : "Revit is busy and didn't respond — try again in a moment.";

            int failed = _handler.FailAllPending(msg);
            Debug.WriteLine($"[BinaVibe][pump] idle-watchdog fast-failed {failed} job(s): {msg}");
        }

        /// <summary>How long the oldest queued job has been waiting. Zero when the
        /// queue is empty. Used to bound the background wait.</summary>
        private static TimeSpan OldestPendingAge()
        {
            try
            {
                if (_handler == null || !_handler.Pending.TryPeek(out var oldest) || oldest == null)
                    return TimeSpan.Zero;
                if (oldest.TEnqueued <= 0) return TimeSpan.Zero;
                return TimeSpan.FromSeconds((Now - oldest.TEnqueued) / (double)Freq);
            }
            catch { return TimeSpan.Zero; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>
        /// Is Revit the foreground application? Compared by PROCESS id, not window
        /// handle, so Revit's modeless children (the Copilot pane's own popups, the
        /// Scheme Preview window) still count as "Revit is focused".
        ///
        /// Fails OPEN: any error returns true, which preserves the old fast-fail
        /// behaviour rather than letting a job hang on a P/Invoke quirk.
        ///
        /// Public because the tool-loop's execution budget must not be spent while
        /// Revit is backgrounded — a queued job simply cannot run then, so charging
        /// it for that time fails healthy work (see ToolLoopRunner.ExecuteOneAsync).
        /// </summary>
        public static bool IsRevitForeground()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;   // nothing focused — treat as background
                if (GetWindowThreadProcessId(hwnd, out uint pid) == 0) return true;
                return pid == (uint)Process.GetCurrentProcess().Id;
            }
            catch { return true; }
        }
    }
}
