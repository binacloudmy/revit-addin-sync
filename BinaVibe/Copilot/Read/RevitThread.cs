#nullable enable
// Runs copilot read work on Revit's UI thread, the same way McpJobPump runs
// legacy tools (see McpJobPump.cs for the why):
//   - an Idling handler (SnapshotService.Attach subscribes it) drains the queue
//     and calls SetRaiseWithoutDelay while work remains;
//   - an ExternalEvent raised on enqueue is the best-effort wake;
//   - a watchdog fails the queue fast with RevitBusyException when no idle
//     arrives within IdleGrace (a modal dialog or a long command) — the read
//     returns revit_busy instead of hanging (spec §7.10);
//   - while a forced update is pending nothing runs (UpdateGate).
//
// McpJob only carries a ToolRegistry name, so the copilot tools cannot ride
// McpJobPump without editing it; this is the same pump in a few lines, kept
// in W2's files (see W2 report: a Func-carrying McpJob would let both share one).

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BinaVibe.Copilot.Read
{
    /// <summary>Revit did not reach idle in time (busy, or a modal dialog is open).</summary>
    public sealed class RevitBusyException : Exception
    {
        public RevitBusyException(string message) : base(message) { }
    }

    /// <summary>A forced add-in update is pending: no AI path may act on the model.</summary>
    public sealed class UpdateBlockedException : Exception
    {
        public UpdateBlockedException(string message) : base(message) { }
    }

    public static class RevitThread
    {
        private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan DialogRecent = TimeSpan.FromSeconds(20);

        private sealed class WorkItem
        {
            public Action<UIApplication> Run = _ => { };
            public Action<Exception> Fail = _ => { };
            public volatile bool Started;
            public volatile bool Abandoned;
        }

        private sealed class KickHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app) => Drain(app);
            public string GetName() => "BinaVibe.Copilot.Read.RevitThread";
        }

        private static readonly ConcurrentQueue<WorkItem> Pending = new();
        private static readonly object Gate = new();
        private static ExternalEvent? _kick;
        private static Timer? _watchdog;
        private static volatile bool _attached;
        private static long _armedTicks;
        private static long _lastIdleTicks;
        private static long _lastDialogTicks;

        private static long Now => Stopwatch.GetTimestamp();

        /// <summary>True once SnapshotService.Attach wired the pump.</summary>
        public static bool IsAttached => _attached;

        /// <summary>Wire once, in OnStartup (a valid API context for ExternalEvent.Create).</summary>
        internal static void Attach(UIControlledApplication app)
        {
            lock (Gate)
            {
                if (_attached) return;
                try { _kick = ExternalEvent.Create(new KickHandler()); }
                catch { _kick = null; /* the Idling drainer is the guarantee */ }
                app.DialogBoxShowing += OnDialogBoxShowing;
                _attached = true;
            }
        }

        // Read-only: only notes the time so a busy failure can name the dialog.
        private static void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e) =>
            Volatile.Write(ref _lastDialogTicks, Now);

        /// <summary>Called from SnapshotService's Idling handler.</summary>
        internal static void OnIdling(UIApplication app, IdlingEventArgs e)
        {
            Volatile.Write(ref _lastIdleTicks, Now);
            if (Pending.IsEmpty) return;
            Drain(app);
            if (!Pending.IsEmpty) e.SetRaiseWithoutDelay();
        }

        /// <summary>Runs <paramref name="work"/> on Revit's UI thread. Faults with
        /// <see cref="RevitBusyException"/> when Revit does not reach idle in time,
        /// <see cref="UpdateBlockedException"/> while a forced update is pending, and is
        /// cancelled when <paramref name="ct"/> fires before the work started (Revit
        /// cannot abort work that already started).</summary>
        public static Task<T> RunAsync<T>(Func<UIApplication, T> work, CancellationToken ct)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (!_attached)
                return Task.FromException<T>(new InvalidOperationException(
                    "The copilot read pump is not wired (SnapshotService.Attach was not called at startup)."));

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new WorkItem();
            item.Run = app =>
            {
                try { tcs.TrySetResult(work(app)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            item.Fail = ex => tcs.TrySetException(ex);
            if (ct.CanBeCanceled)
                ct.Register(() =>
                {
                    if (item.Started) return;
                    item.Abandoned = true;
                    tcs.TrySetCanceled(ct);
                });

            Pending.Enqueue(item);
            Volatile.Write(ref _armedTicks, Now);
            ArmWatchdog();
            try { _kick?.Raise(); } catch { /* disposed or no context: Idling still drains */ }
            return tcs.Task;
        }

        private static void Drain(UIApplication app)
        {
            if (RevitWebAppSync.Services.UpdateGate.IsBlocked)
            {
                FailAll(new UpdateBlockedException(RevitWebAppSync.Services.UpdateGate.RefusalMessage));
                return;
            }
            while (Pending.TryDequeue(out var item))
            {
                if (item.Abandoned) continue;
                item.Started = true;
                item.Run(app);
            }
        }

        private static void FailAll(Exception ex)
        {
            while (Pending.TryDequeue(out var item))
            {
                if (item.Abandoned) continue;
                item.Fail(ex);
            }
        }

        private static void ArmWatchdog()
        {
            lock (Gate)
            {
                if (_watchdog == null) _watchdog = new Timer(OnWatchdog, null, IdleGrace, Timeout.InfiniteTimeSpan);
                else _watchdog.Change(IdleGrace, Timeout.InfiniteTimeSpan);
            }
        }

        private static void OnWatchdog(object? _)
        {
            bool idleSinceArm = Volatile.Read(ref _lastIdleTicks) >= Volatile.Read(ref _armedTicks);
            if (idleSinceArm || Pending.IsEmpty) return;
            double sinceDialogMs = (Now - Volatile.Read(ref _lastDialogTicks)) * 1000.0 / Stopwatch.Frequency;
            var message = sinceDialogMs <= DialogRecent.TotalMilliseconds
                ? "Revit has a dialog open. Close it and try again."
                : "Revit is busy and did not respond. Try again in a moment.";
            FailAll(new RevitBusyException(message));
        }
    }
}
