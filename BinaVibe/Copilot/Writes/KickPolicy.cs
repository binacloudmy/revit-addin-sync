#nullable enable
// When RevitThread asks Revit for an idle turn (the ExternalEvent "kick"). Revit-free.
//
// Every queued job raised the kick, and the drain raised it again while work remained: five to eight Raise() calls per prompt. In the overnight
// soak (17 Nov) Revit's handle count climbed about five to seven per prompt, all of them signaled manual-reset Events, none closed, flat while
// idle. One raise is enough: the pending one drains the queue (one job per turn, then it raises again while work is left). So a raise is skipped
// while one is pending; a raise that never ran (Revit busy in a dialog, or denied) is forgotten after a short time, so the kick can never wedge.
using System;

namespace BinaVibe.Copilot.Writes
{
    public static class KickPolicy
    {
        /// <summary>How long a raise counts as pending before it is raised again.</summary>
        public static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(2);

        /// <summary>True when the kick should be raised: none is pending (<paramref name="raisedAt"/> is 0), or the pending one is older than
        /// <paramref name="pendingFor"/> (ticks of the same clock as <paramref name="now"/>, <paramref name="ticksPerSecond"/> per second).</summary>
        public static bool ShouldRaise(long raisedAt, long now, long ticksPerSecond, TimeSpan pendingFor)
        {
            if (raisedAt == 0) return true;
            var age = (now - raisedAt) / (double)Math.Max(1, ticksPerSecond);
            return age < 0 || age >= pendingFor.TotalSeconds;
        }
    }
}
