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
    }

    public sealed class BudgetExceededException : Exception
    {
        public BudgetExceededException(BudgetReason reason, long ticks, TimeSpan elapsed, int depth)
            : base(Describe(reason, ticks, elapsed, depth))
        {
            Reason = reason;
            Ticks = ticks;
            Elapsed = elapsed;
            Depth = depth;
        }

        public BudgetReason Reason { get; }
        public long Ticks { get; }
        public TimeSpan Elapsed { get; }
        public int Depth { get; }

        private static string Describe(BudgetReason reason, long ticks, TimeSpan elapsed, int depth) => reason switch
        {
            BudgetReason.Steps => "run_revit_code stopped after " + ticks.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " loop steps.",
            BudgetReason.Time => "run_revit_code stopped after " + ((int)elapsed.TotalSeconds) + " s.",
            _ => "run_revit_code stopped at " + depth + " nested calls.",
        };
    }

    public sealed class LoopBudget
    {
        private readonly Func<TimeSpan> _clock;
        private BudgetExceededException? _exceeded;

        public LoopBudget(long maxTicks, TimeSpan maxTime, int maxDepth, Func<TimeSpan>? clock = null)
        {
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

        /// <summary>The budget compiled into the add-in (code-policy.json "budget").</summary>
        public static LoopBudget FromPolicy(CodePolicySet? policy = null)
        {
            var b = (policy ?? CodePolicySet.Default).Budget;
            return new LoopBudget(b.MaxTicks, TimeSpan.FromSeconds(b.MaxSeconds), b.MaxDepth);
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

        /// <summary>A loop back-edge (or a Print).</summary>
        public void Tick()
        {
            if (_exceeded != null) Stop(_exceeded.Reason);
            Ticks++;
            if (Ticks > MaxTicks) Stop(BudgetReason.Steps);
            if (_clock() > MaxTime) Stop(BudgetReason.Time);
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
            var ex = new BudgetExceededException(first, Ticks, _clock(), Depth);
            _exceeded ??= ex;
            throw ex;
        }
    }
}
