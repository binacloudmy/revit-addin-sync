#nullable enable
// Runs write-engine callbacks on Revit's UI thread (spec §7.2, §7.10).
//
// McpJobPump only runs ToolRegistry tools by name, and a write batch needs
// two separate callbacks (preview, then commit after the card) with nothing
// held open in between. This is the same Idling pattern, for delegates:
//   - a permanently subscribed Idling handler runs ONE queued callback per
//     Idling and calls SetRaiseWithoutDelay while work remains;
//   - a watchdog fails a callback fast with RevitBusyException when no Idling
//     arrives within 6 s of queueing (busy, or a modal dialog is open), so a
//     write never hangs the turn (the model then tells the drafter; no retry);
//   - the forced-update gate refuses work, as the pump does.
// Attached from UndoService.Attach (ruling R4: W5 wires that from App.cs).

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BinaVibe.Copilot.Writes
{
    public static class RevitThread
    {
        private static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan DialogRecent = TimeSpan.FromSeconds(20);

        private static readonly object Gate = new();
        private static readonly ConcurrentQueue<IWork> Queue = new();
        private static bool _attached;
        private static Timer? _watchdog;
        private static long _armedTicks;
        private static long _lastIdleTicks;
        private static long _lastDialogTicks;

        private static long Now => Stopwatch.GetTimestamp();

        private interface IWork
        {
            bool IsDone { get; }
            void Run(UIApplication app);
            void Fail(Exception ex);
        }

        private sealed class Work<T> : IWork
        {
            private readonly Func<UIApplication, T> _body;
            private readonly CancellationToken _ct;
            public readonly TaskCompletionSource<T> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Work(Func<UIApplication, T> body, CancellationToken ct) { _body = body; _ct = ct; }

            public bool IsDone => Done.Task.IsCompleted;

            public void Run(UIApplication app)
            {
                if (_ct.IsCancellationRequested) { Done.TrySetCanceled(_ct); return; }
                try { Done.TrySetResult(_body(app)); }
                catch (Exception ex) { Done.TrySetException(ex); }
            }

            public void Fail(Exception ex) => Done.TrySetException(ex);
        }

        public static bool IsAttached { get { lock (Gate) return _attached; } }

        /// <summary>Subscribe once at startup (UI thread).</summary>
        public static void Attach(UIControlledApplication app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            lock (Gate)
            {
                if (_attached) return;
                app.Idling += OnIdling;
                app.DialogBoxShowing += (s, e) => Volatile.Write(ref _lastDialogTicks, Now);
                _attached = true;
            }
        }

        /// <summary>Queues <paramref name="body"/> for Revit's thread. Completes with its result,
        /// its exception, cancellation (only before it started; a running callback cannot be
        /// aborted), or <see cref="RevitBusyException"/> when Revit is not idling.</summary>
        public static Task<T> RunAsync<T>(Func<UIApplication, T> body, CancellationToken ct)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (!IsAttached)
                return Task.FromException<T>(new RevitBusyException(
                    "The Copilot write engine is not attached to Revit in this session, so nothing was changed. Restart Revit; if it persists, report it."));
            if (RevitWebAppSync.Services.UpdateGate.IsBlocked)
                return Task.FromException<T>(new RevitBusyException(RevitWebAppSync.Services.UpdateGate.RefusalMessage));

            var work = new Work<T>(body, ct);
            Queue.Enqueue(work);
            Volatile.Write(ref _armedTicks, Now);
            ArmWatchdog();
            return work.Done.Task;
        }

        private static void OnIdling(object? sender, IdlingEventArgs e)
        {
            Volatile.Write(ref _lastIdleTicks, Now);
            if (Queue.IsEmpty || sender is not UIApplication app) return;

            while (Queue.TryDequeue(out var work))
            {
                if (work.IsDone) continue;   // the watchdog already failed it
                if (RevitWebAppSync.Services.UpdateGate.IsBlocked)
                {
                    work.Fail(new RevitBusyException(RevitWebAppSync.Services.UpdateGate.RefusalMessage));
                    continue;
                }
                work.Run(app);
                break;   // one callback per Idling: Revit repaints between preview and commit
            }

            if (!Queue.IsEmpty) e.SetRaiseWithoutDelay();
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
            if (Volatile.Read(ref _lastIdleTicks) >= Volatile.Read(ref _armedTicks) || Queue.IsEmpty) return;
            var sinceDialog = TimeSpan.FromSeconds((Now - Volatile.Read(ref _lastDialogTicks)) / (double)Stopwatch.Frequency);
            var message = sinceDialog <= DialogRecent
                ? "Revit has a dialog open, so nothing was changed. Ask the drafter to close it, then try again."
                : "Revit is busy and did not respond, so nothing was changed. Try again in a moment.";
            while (Queue.TryDequeue(out var work))
                if (!work.IsDone) work.Fail(new RevitBusyException(message));
            Debug.WriteLine("[BINA][writes] watchdog: " + message);
        }
    }
}
