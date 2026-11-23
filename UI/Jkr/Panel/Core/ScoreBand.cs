namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The big compliance score at the top of the Results view: which colour band the % falls in
    /// and the one count line under it. Pure (no WPF); the numbers come from the existing score
    /// (SpecChecklist.Pct / Score / TallyOf), nothing here scores anything.
    /// </summary>
    public static class ScoreBand
    {
        public const string Fail = "fail", Partial = "partial", Pass = "pass", None = "none";

        /// <summary>fail &lt; 50, partial (amber) 50-79, pass &gt;= 80, none without a score. The names match the pane's tones.</summary>
        public static string Of(int? pct) =>
            !pct.HasValue ? None : pct.Value < 50 ? Fail : pct.Value < 80 ? Partial : Pass;

        /// <summary>"39 of 59 checks pass · 16 fail · 4 could not be checked" (partly checked ones, when any, as "n partly checked").</summary>
        public static string CountsLine(int passed, int total, int failed, int unjudged, int partial = 0)
        {
            var s = total == 1 ? passed + " of 1 check passes" : passed + " of " + total + " checks pass";
            s += " · " + failed + " fail";
            if (partial > 0) s += " · " + partial + " partly checked";
            if (unjudged > 0) s += " · " + unjudged + " could not be checked";
            return s;
        }
    }
}
