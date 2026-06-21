using System.Collections.Generic;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>
    /// Compliance hierarchy — maps a category label to its severity tier.
    /// Tier determines which user actions are allowed on an issue:
    ///   High   (Must Fix):          Auto-fix only.             No Ignore, no Approve, no MarkManual.
    ///   Medium (Fix During Project): Auto-fix + Ignore + MarkManual.   No Approve.
    ///   Low    (Fix Later):         Auto-fix + Ignore + Approve + MarkManual.
    /// "MarkManual" is voluntary deferral; the auto-fix-failure path still routes
    /// failed fixes (including High) to the Manual tab unconditionally so a hard
    /// block doesn't strand the user with no action.
    /// Labels are English only (tier subtitle already states intent).
    /// </summary>
    public static class JkrTierMap
    {
        public static readonly Dictionary<string, IssuePriority> CategoryToTier = new Dictionary<string, IssuePriority>
        {
            ["Project Naming"]         = IssuePriority.High,
            ["Project Information"]    = IssuePriority.High,
            ["Project Base Point"]     = IssuePriority.High,
            ["Grids"]                  = IssuePriority.High,
            ["Levels"]                 = IssuePriority.High,
            ["Component Parameter"]    = IssuePriority.Medium,
            ["Component Naming"]       = IssuePriority.Medium,
            ["LOD 400/500 parameter"]  = IssuePriority.Low,
        };

        public static IssuePriority Resolve(string category)
        {
            if (!string.IsNullOrEmpty(category) && CategoryToTier.TryGetValue(category, out var tier))
                return tier;
            return IssuePriority.Medium;
        }

        public static string Label(IssuePriority tier)
        {
            switch (tier)
            {
                case IssuePriority.High:   return "High";
                case IssuePriority.Low:    return "Low";
                default:                   return "Medium";
            }
        }

        public static string Subtitle(IssuePriority tier)
        {
            switch (tier)
            {
                case IssuePriority.High:   return "Must Fix";
                case IssuePriority.Low:    return "Fix Later";
                default:                   return "Fix During Project";
            }
        }

        public static bool CanAutoFix(IssuePriority tier) => true;  // all tiers
        public static bool CanIgnore(IssuePriority tier)  => tier != IssuePriority.High;
        public static bool CanApprove(IssuePriority tier) => tier == IssuePriority.Low;
        public static bool CanMarkManual(IssuePriority tier) => tier != IssuePriority.High;
    }
}
