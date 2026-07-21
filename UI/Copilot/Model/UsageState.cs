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

        public static UsageState FromCredits(bool unlimited, int used, int limit, string plan = null)
        {
            // AtLimit is the REAL count — you're only blocked when credits are
            // actually exhausted, not when the rounded percent reaches 100. A
            // 999,000/1,000,000 balance (~1k left) must NOT trip the upgrade wall.
            bool atLimit = !unlimited && limit > 0 && used >= limit;
            // Floor (not round) so 99.9% shows as 99%, and never display 100%
            // while any credits remain — keeps the meter honest with AtLimit.
            int pct = unlimited || limit <= 0 ? 0 :
                Math.Max(0, Math.Min(100, (int)Math.Floor(100.0 * used / limit)));
            if (pct >= 100 && !atLimit) pct = 99;
            // Prefer the backend-reported plan/tier (pricing v2: Free / Basic /
            // Plus / Pro / Pro Max) so a fresh upgrade shows "Pro" on the next
            // refresh. Fall back to inferring when the field is absent (older
            // backends). Pricing v2 caps EVERY tier, so an uncapped wallet isn't a
            // tier at all — it's an internal/admin override (POST /credits/set-unlimited).
            // Label it as such rather than aliasing it to "Pro Max", which would imply
            // the account is paying $199/mo.
            string name = !string.IsNullOrWhiteSpace(plan) ? plan
                : (unlimited ? "Unlimited (internal)" : "Free");
            return new UsageState { PlanName = name, Pct = pct, AtLimit = atLimit };
        }
    }
}
