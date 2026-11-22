#nullable enable
// The run-time half of the loop budget (spec §7.8 "Budget"). Revit-free.
//
// LoopBudgetRewriter inserts calls to Tick() at every loop back-edge and to
// Enter()/Exit() around every lambda and local-function body. On Revit's UI
// thread nothing else can stop a runaway snippet, so:
//   - Tick() throws BudgetExceededException past max_ticks steps or max_seconds;
//   - Enter() also throws past max_depth nested calls, and before the stack
//     runs out (a StackOverflowException would kill Revit);
//   - once exceeded the budget stays exceeded: every later Tick throws again and
//     Live is false, which the rewriter puts in every catch filter, so the
//     model's own try/catch cannot swallow the stop.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BinaVibe.Copilot.Code
{
    public enum BudgetReason
    {
        /// <summary>More loop steps (ticks) than allowed.</summary>
        Steps,
        /// <summary>Ran longer than allowed.</summary>
        Time,
        /// <summary>Calls nested deeper than allowed (runaway recursion).</summary>
        Depth,
        /// <summary>The drafter pressed Stop: the turn's cancellation token was cancelled while the code ran.</summary>
        Cancelled,
    }

    public sealed class BudgetExceededException : Exception
    {
        public BudgetExceededException(BudgetReason reason, long ticks, TimeSpan elapsed, int depth, TimeSpan? timeLimit = null)
            : base(Describe(reason, ticks, elapsed, depth))
        {
            Reason = reason;
            Ticks = ticks;
            Elapsed = elapsed;
            Depth = depth;
            TimeLimit = timeLimit;
        }

        /// <summary>The time limit of the run that stopped (30 s, or longer for a stored script); null when unknown.</summary>
        public TimeSpan? TimeLimit { get; }

        public BudgetReason Reason { get; }
        public long Ticks { get; }
        public TimeSpan Elapsed { get; }
        public int Depth { get; }

        private static string Describe(BudgetReason reason, long ticks, TimeSpan elapsed, int depth) => reason switch
        {
            BudgetReason.Steps => "run_revit_code stopped after " + ticks.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " loop steps.",
            BudgetReason.Time => "run_revit_code stopped after " + ((int)elapsed.TotalSeconds) + " s.",
            BudgetReason.Cancelled => "run_revit_code was stopped by the drafter after " + ticks.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " loop steps.",
            _ => "run_revit_code stopped at " + depth + " nested calls.",
        };
    }

    public sealed class LoopBudget
    {
        private readonly Func<TimeSpan> _clock;
        private readonly System.Threading.CancellationToken _cancel;
        private BudgetExceededException? _exceeded;

        /// <param name="cancel">The turn's Stop: a cancelled token ends the run at the next loop step. Defaults to the token of the callback
        /// running on this thread (<see cref="CodeCancel.Current"/>), so a budget made inside RevitThread's callback honours the turn's Stop.</param>
        public LoopBudget(long maxTicks, TimeSpan maxTime, int maxDepth, Func<TimeSpan>? clock = null, System.Threading.CancellationToken? cancel = null)
        {
            _cancel = cancel ?? CodeCancel.Current;
            MaxTicks = maxTicks;
            MaxTime = maxTime;
            MaxDepth = maxDepth;
            if (clock != null)
            {
                _clock = clock;
            }
            else
            {
                var watch = Stopwatch.StartNew();
                _clock = () => watch.Elapsed;
            }
        }

        /// <summary>The budget compiled into the add-in (code-policy.json "budget"). A stored script the backend
        /// rendered (storedScript) gets max_seconds_stored; model-written code max_seconds. Steps and depth are the same.</summary>
        public static LoopBudget FromPolicy(CodePolicySet? policy = null, bool storedScript = false)
        {
            var b = (policy ?? CodePolicySet.Default).Budget;
            var seconds = storedScript && b.MaxSecondsStored > 0 ? b.MaxSecondsStored : b.MaxSeconds;
            return new LoopBudget(b.MaxTicks, TimeSpan.FromSeconds(seconds), b.MaxDepth);
        }

        public long MaxTicks { get; }
        public TimeSpan MaxTime { get; }
        public int MaxDepth { get; }

        public long Ticks { get; private set; }
        public int Depth { get; private set; }
        public TimeSpan Elapsed => _clock();

        /// <summary>False once the budget is exceeded; the rewriter's catch filters read it.</summary>
        public bool Live => _exceeded == null;

        public BudgetReason? Reason => _exceeded?.Reason;

        private TimeSpan _lastTick;

        /// <summary>The longest time between two Ticks (a loop pass, a lambda entry, a Print) and the tick it ended at. The budget can only
        /// stop code at a Tick, so a single long Revit call (a type change that regenerates the model) runs to its end past max_seconds:
        /// the "205 walls" turn hit the 120 s limit after 162 s (handover item 2b). This says which step it was.</summary>
        public TimeSpan LongestGap { get; private set; }
        public long LongestGapAtTick { get; private set; }

        /// <summary>For the stage line of a run: "longest step 41.2 s (step 17), last step to the end 2.0 s", or "" when no step took a second.
        /// Read once, after the code ran.</summary>
        public string GapNote()
        {
            var tail = _clock() - _lastTick;
            if (LongestGap < TimeSpan.FromSeconds(1) && tail < TimeSpan.FromSeconds(1)) return "";
            string S(TimeSpan t) => t.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
            return "longest step " + S(LongestGap) + " (before step " + LongestGapAtTick.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "), last step to the end " + S(tail);
        }

        /// <summary>A loop back-edge (or a Print).</summary>
        public void Tick()
        {
            if (_exceeded != null) Stop(_exceeded.Reason);
            if (_cancel.IsCancellationRequested) Stop(BudgetReason.Cancelled);
            Ticks++;
            if (Ticks > MaxTicks) Stop(BudgetReason.Steps);
            var now = _clock();
            var gap = now - _lastTick;
            if (gap > LongestGap) { LongestGap = gap; LongestGapAtTick = Ticks; }
            _lastTick = now;
            if (now > MaxTime) Stop(BudgetReason.Time);
        }

        /// <summary>A lambda or local-function entry.</summary>
        public void Enter()
        {
            Tick();
            Depth++;
            if (Depth > MaxDepth) Stop(BudgetReason.Depth);
            try
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
            }
            catch (InsufficientExecutionStackException)
            {
                Stop(BudgetReason.Depth);
            }
        }

        /// <summary>The matching exit (the rewriter puts it in a finally).</summary>
        public void Exit()
        {
            if (Depth > 0) Depth--;
        }

        private void Stop(BudgetReason reason)
        {
            // First reason wins; every later throw repeats it from where the code is now.
            var first = _exceeded?.Reason ?? reason;
            var ex = new BudgetExceededException(first, Ticks, _clock(), Depth, MaxTime);
            _exceeded ??= ex;
            throw ex;
        }
    }
}
