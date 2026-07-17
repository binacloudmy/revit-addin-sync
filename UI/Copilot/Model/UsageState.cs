using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Plan + usage snapshot driving the footer meter, popover and blocked state.</summary>
    public class UsageState
    {
        public string PlanName { get; set; } = "Free";
        public int Pct { get; set; }
        public bool AtLimit { get; set; }
        public bool IsAdmin { get; set; } = true;
        public string AdminName { get; set; } = "Sara Rahman";
        public string AdminEmail { get; set; } = "sara@bina.cloud";

        /// <summary>Design ramp: accent &lt;80, amber 80–94, red ≥95.</summary>
        public static string MeterColorKey(int pct) =>
            pct >= 95 ? "Cp.Red" : pct >= 80 ? "Cp.Amber" : "Cp.Accent";

        public static UsageState FromCredits(bool unlimited, int used, int limit)
        {
            if (unlimited) return new UsageState { PlanName = "Power", Pct = 0, AtLimit = false };
            // AtLimit is the REAL count — you're only blocked when credits are
            // actually exhausted, not when the rounded percent reaches 100. A
            // 999,000/1,000,000 balance (~1k left) must NOT trip the upgrade wall.
            bool atLimit = limit > 0 && used >= limit;
            // Floor (not round) so 99.9% shows as 99%, and never display 100%
            // while any credits remain — keeps the meter honest with AtLimit.
            var pct = limit <= 0 ? 0 :
                Math.Max(0, Math.Min(100, (int)Math.Floor(100.0 * used / limit)));
            if (pct >= 100 && !atLimit) pct = 99;
            return new UsageState { PlanName = "Free", Pct = pct, AtLimit = atLimit };
        }
    }
}
