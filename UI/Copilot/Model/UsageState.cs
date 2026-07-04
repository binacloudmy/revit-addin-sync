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
            if (unlimited) return new UsageState { PlanName = "Pro", Pct = 0, AtLimit = false };
            var pct = limit <= 0 ? 0 :
                Math.Max(0, Math.Min(100, (int)Math.Round(100.0 * used / limit)));
            return new UsageState { PlanName = "Free", Pct = pct, AtLimit = pct >= 100 };
        }
    }
}
