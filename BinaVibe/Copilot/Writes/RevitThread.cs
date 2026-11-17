#nullable enable
// Runs write-engine callbacks on Revit's UI thread (spec §7.2, §7.10).
//
// The single Revit-thread dispatcher for all copilot work (reads, writes,
// in-pane tool calls). A write batch needs two callbacks (preview, then commit
// after the card) with nothing held open in between:
//   - a permanently subscribed Idling handler runs ONE queued callback per
//     Idling and calls SetRaiseWithoutDelay while work remains; an
//     ExternalEvent kick wakes Revit when idle sessions have stopped (an
//     inactive user), and runs one callback itself;
//   - a watchdog fails queued work fast with RevitBusyException only when
//     Revit has stopped raising Idling while its UI thread still pumps
//     messages (a modal dialog), or after a hard ceiling. A slow sibling
//     Idling handler (snapshot refresh, overview prewarm) or a long command
//     keeps the thread busy, and the work waits for it (final review M-1;
//     WatchdogPolicy). Queued work never started, so a failure means nothing
//     was changed;
//   - the forced-update gate refuses work, as the pump does.
// Attached from UndoService.Attach (ruling R4: W5 wires that from App.cs).

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BinaVibe.Copilot.Writes
{
    public static class RevitThread
    {
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

        private static readonly object Gate = new();
        private static readonly ConcurrentQueue<IWork> Queue = new();
        private static bool _attached;
        private static Timer? _watchdog;
        private static ExternalEvent? _kick;
        private static Dispatcher? _ui;
        private static long _armedTicks;
        private static long _lastIdleTicks;
        private static long _lastDialogTicks;
        private static long _probePostedTicks;
        private static long _probeRanTicks;
        private static long _kickRaisedTicks;   // when the last kick was raised and has not run yet; 0 when none is pending (KickPolicy)

        private static long Now => Stopwatch.GetTimestamp();

        private static TimeSpan Since(long ticks) =>
            ticks == 0 ? TimeSpan.MaxValue : TimeSpan.FromSeconds((Now - ticks) / (double)Stopwatch.Frequency);

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

            private readonly Stopwatch _queued = Stopwatch.StartNew();

            public Work(Func<UIApplication, T> body, CancellationToken ct) { _body = body; _ct = ct; }

            public bool IsDone => Done.Task.IsCompleted;

            public void Run(UIApplication app)
            {
                if (_ct.IsCancellationRequested) { Done.TrySetCanceled(_ct); return; }
                var waited = _queued.ElapsedMilliseconds;
                var ran = Stopwatch.StartNew();
                RevitWebAppSync.UI.Copilot.Model.HandleProbe.Mark("revit job " + typeof(T).Name + ": start");
                try
                {
                    // The turn's Stop is the running code's too: its loop budget polls this token (CodeCancel).
                    using (BinaVibe.Copilot.Code.CodeCancel.Enter(_ct))
                        Done.TrySetResult(_body(app));
                }
                catch (Exception ex) { Done.TrySetException(ex); }
                finally
                {
                    RevitWebAppSync.UI.Copilot.Model.HandleProbe.Mark("revit job " + typeof(T).Name + ": end");
                    // A job that waited a second for Revit's turn or ran a quarter second says so in the click log (the 4 s queue after a turn).
                    if (waited >= 1000 || ran.ElapsedMilliseconds >= 250)
                        try { BinaVibe.Copilot.Guard.StageTimer.Sink("revit thread job " + typeof(T).Name + ": queued " + waited + " ms, ran " + ran.ElapsedMilliseconds + " ms"); }
                        catch (Exception) { /* a log must never break the work */ }
                }
            }

            public void Fail(Exception ex) => Done.TrySetException(ex);
        }

        private sealed class KickHandler : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                Volatile.Write(ref _kickRaisedTicks, 0);   // this raise ran: the next job may raise again
                try { DrainOne(app); }
                catch (Exception ex) { Debug.WriteLine("[BINA][writes] kick drain failed: " + ex); }
            }
            public string GetName() => "BINA copilot writes";
        }

        public static bool IsAttached { get { lock (Gate) return _attached; } }

        /// <summary>Work of ours is waiting for Revit's turn: other Idling handlers (the snapshot refresh) step aside for it.</summary>
        public static bool HasQueuedWork => !Queue.IsEmpty;

        /// <summary>Raised on Revit's thread at every Idling call, after the queued callback (if any) ran. A plain event:
        /// a listener may be added from any thread at any time. Revit's own Idling event cannot be subscribed from inside
        /// an Idling call, and queued work often runs inside one (live round 13: "Can not subscribe to an event during
        /// execution of that event!", so the result key followed the view only when the work happened to run from the
        /// kick). The args are Revit's: a listener may call SetRaiseWithoutDelay.</summary>
        public static event Action<UIApplication, IdlingEventArgs>? Idle;

        /// <summary>Subscribe once at startup (UI thread, a valid API context).</summary>
        public static void Attach(UIControlledApplication app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            lock (Gate)
            {
                if (_attached) return;
                app.Idling += OnIdling;
                app.DialogBoxShowing += (s, e) => Volatile.Write(ref _lastDialogTicks, Now);
                _ui = Dispatcher.CurrentDispatcher;
                try { _kick = ExternalEvent.Create(new KickHandler()); }
                catch (Exception) { _kick = null; /* Idling still drains */ }
                _attached = true;
            }
        }

        /// <summary>Have Revit take an idle turn now, when it would otherwise wait for the drafter's next mouse move (the
        /// same ExternalEvent kick queued work uses). For work that advances only in Idling, like the turn's Undo.</summary>
        public static void Wake()
        {
            RaiseKick(force: true);   // the turn's Undo advances only in Idling: never skipped (a few per drafter click, not per job)
        }

        /// <summary>Raise the kick unless one is already pending (see <see cref="KickPolicy"/>: five to eight raises per prompt were a handle leak
        /// of about as many signaled Events per prompt in the soak).</summary>
        private static void RaiseKick(bool force = false)
        {
            var kick = _kick;
            if (kick == null) return;
            var now = Now;
            if (!force && !KickPolicy.ShouldRaise(Volatile.Read(ref _kickRaisedTicks), now, Stopwatch.Frequency, KickPolicy.PendingFor)) return;
            Volatile.Write(ref _kickRaisedTicks, now);
            RevitWebAppSync.UI.Copilot.Model.HandleProbe.Mark("raise kick: before");
            try { kick.Raise(); RevitWebAppSync.UI.Copilot.Model.HandleProbe.Mark("raise kick: after"); }
            catch (Exception) { Volatile.Write(ref _kickRaisedTicks, 0); /* Idling still drains */ }
        }

        /// <summary>Queues <paramref name="body"/> for Revit's thread. Completes with its result,
        /// its exception, cancellation (only before it started; a running callback cannot be
        /// aborted), or <see cref="RevitBusyException"/> when Revit cannot run it.</summary>
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
            RaiseKick();
            ArmWatchdog();
            return work.Done.Task;
        }

        private static void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (sender is not UIApplication app) return;
            try
            {
                DrainOne(app);
                // Only while work remains: Revit's own idle-time work (its Save Reminder among
                // it) is never starved once the queue is empty (AI-327).
                if (!Queue.IsEmpty) e.SetRaiseWithoutDelay();
            }
            catch (Exception ex)
            {
                // Work.Run already turns a callback's exception into its task's; this is the
                // dispatcher itself. Never let it escape into Revit's event loop.
                Debug.WriteLine("[BINA][writes] idling drain failed: " + ex);
            }
            try { Idle?.Invoke(app, e); }
            catch (Exception ex) { Debug.WriteLine("[BINA][writes] idle listener failed: " + ex.Message); }
        }

        /// <summary>Runs one queued callback (Idling or the kick); stamps that Revit gave us a turn.</summary>
        private static void DrainOne(UIApplication app)
        {
            Volatile.Write(ref _lastIdleTicks, Now);
            while (Queue.TryDequeue(out var work))
            {
                if (work.IsDone) continue;   // the watchdog already failed it
                if (RevitWebAppSync.Services.UpdateGate.IsBlocked)
                {
                    work.Fail(new RevitBusyException(RevitWebAppSync.Services.UpdateGate.RefusalMessage));
                    continue;
                }
                work.Run(app);
                break;   // one callback per turn: Revit repaints between preview and commit
            }
            if (!Queue.IsEmpty) RaiseKick();
        }

        private static void ArmWatchdog()
        {
            lock (Gate)
            {
                if (_watchdog == null) _watchdog = new Timer(OnWatchdog, null, Tick, Tick);
                else _watchdog.Change(Tick, Tick);
            }
        }

        private static void StopWatchdog()
        {
            lock (Gate) _watchdog?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        /// <summary>A System.Threading.Timer callback: an exception escaping it ends the Revit
        /// process (AI-327), so nothing may leave it. The next tick tries again.</summary>
        private static void OnWatchdog(object? state)
        {
            try
            {
                Watch();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA][writes] watchdog tick failed: " + ex);
            }
        }

        private static void Watch()
        {
            if (Queue.IsEmpty) { StopWatchdog(); return; }

            var armed = Volatile.Read(ref _armedTicks);
            var sinceArmed = Since(armed);
            var idleSinceArm = Volatile.Read(ref _lastIdleTicks) >= armed;
            var probed = Volatile.Read(ref _probePostedTicks);
            var probedSinceArm = probed >= armed && probed != 0;

            if (WatchdogPolicy.ShouldProbe(sinceArmed, idleSinceArm, probedSinceArm))
            {
                Volatile.Write(ref _probePostedTicks, Now);
                try { _ui?.BeginInvoke(DispatcherPriority.Send, new Action(() => Volatile.Write(ref _probeRanTicks, Now))); }
                catch (Exception) { /* no dispatcher: the ceiling still applies */ }
                return;
            }

            var probeRan = Volatile.Read(ref _probeRanTicks);
            var uiPumped = probedSinceArm && probeRan >= Volatile.Read(ref _probePostedTicks);
            var verdict = WatchdogPolicy.Decide(sinceArmed, idleSinceArm, uiPumped, uiPumped ? Since(probeRan) : TimeSpan.Zero,
                Since(Volatile.Read(ref _lastDialogTicks)));
            if (verdict == WatchdogVerdict.Wait) return;

            var message = WatchdogPolicy.Message(verdict);
            while (Queue.TryDequeue(out var work))
                if (!work.IsDone) work.Fail(new RevitBusyException(message));
            StopWatchdog();
            Debug.WriteLine("[BINA][writes] watchdog: " + message);
        }
    }
}
