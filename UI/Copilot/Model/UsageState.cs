using System;
using System.Globalization;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Plan + usage snapshot driving the header ring, popover, near-limit
    /// notice and blocked state.</summary>
    public class UsageState
    {
        public string PlanName { get; set; } = "Free";
        public int Pct { get; set; }
        public bool AtLimit { get; set; }
        public bool IsAdmin { get; set; } = true;
        public string AdminName { get; set; } = "Sara Rahman";
        public string AdminEmail { get; set; } = "sara@bina.cloud";

        /// <summary>Uncapped wallet (admin override) — the ring hides rather than
        /// rendering a meaningless 0%.</summary>
        public bool Unlimited { get; set; }

        /// <summary>Raw <c>resets_at</c> from the credits endpoint (ISO date, the 1st
        /// of the month after the current period). Kept verbatim so it can also key
        /// the dismissal of the near-limit notice per quota period.</summary>
        public string ResetsAt { get; set; }

        /// <summary>The severity thresholds. One source of truth so the ring, the
        /// popover bar and the near-limit notice can never disagree.</summary>
        public const int WarnPct = 80;
        public const int CriticalPct = 95;

        /// <summary>Design ramp: accent &lt;80, amber 80–94, red ≥95.</summary>
        public static string MeterColorKey(int pct) =>
            pct >= CriticalPct ? "Cp.Red" : pct >= WarnPct ? "Cp.Amber" : "Cp.Accent";

        /// <summary>True once usage is worth warning about (drives the notice).</summary>
        public bool ShouldWarn => !Unlimited && !AtLimit && Pct >= WarnPct;

        /// <summary>Which band the warning sits in — the notice re-appears after a
        /// dismissal when the user crosses from one band into the next.</summary>
        public int WarnBand => Pct >= CriticalPct ? CriticalPct : WarnPct;

        /// <summary>Short plan pill for the popover ("PRO", "BASIC", …). Pricing v2
        /// tiers are Free / Basic / Plus / Pro / Pro Max; Pro Max shares the PRO pill
        /// so the badge stays narrow.</summary>
        public string TierBadge
        {
            get
            {
                var p = (PlanName ?? "").Trim();
                if (p.Length == 0) return "";
                if (p.StartsWith("Unlimited", StringComparison.OrdinalIgnoreCase)) return "INTERNAL";
                if (p.StartsWith("Pro", StringComparison.OrdinalIgnoreCase)) return "PRO";
                return p.ToUpperInvariant();
            }
        }

        /// <summary>"Resets 1 Aug" for the popover's normal state. Empty when the
        /// quota can't reset (unlimited) or the backend sent nothing parseable —
        /// callers hide the row on empty rather than printing a placeholder date.</summary>
        public string ResetsLabel
        {
            get
            {
                if (Unlimited || string.IsNullOrWhiteSpace(ResetsAt)) return "";
                DateTime d;
                if (!DateTime.TryParse(ResetsAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                    return "";
                return "Resets " + d.ToString("d MMM", CultureInfo.InvariantCulture);
            }
        }

        public static UsageState FromCredits(bool unlimited, int used, int limit, string plan = null,
            string resetsAt = null)
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
            return new UsageState
            {
                PlanName = name, Pct = pct, AtLimit = atLimit,
                Unlimited = unlimited, ResetsAt = resetsAt,
            };
        }
    }
}
