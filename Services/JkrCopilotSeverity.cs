using System;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The severity model from the Claude Design canvas ("JKR Audit Copilot.dc.html",
    /// <c>sev()</c> / <c>diff()</c>), ported verbatim.
    ///
    /// Why this exists as its own type: the shipped Zoom window collapsed every finding
    /// to one red "fail", which is exactly the wall the redesign was commissioned to
    /// remove — 722 undifferentiated issues with nothing saying which will get the
    /// submission rejected (Build Diff delta 03, a BLOCKER).
    ///
    /// Every tier carries a SHAPE prefix as well as a colour, so the ranking survives
    /// greyscale printing and colour-blindness. Do not drop the diamonds.
    ///
    /// Pure and Revit-free by design: it links into the cross-platform test target and
    /// is unit-tested without WPF.
    /// </summary>
    public static class JkrCopilotSeverity
    {
        /// <summary>One severity tier: the label plus every colour the row needs.</summary>
        public sealed class Tier
        {
            public string Tag { get; set; }      // "◆◆◆ KRITIKAL"
            public string Bg { get; set; }       // chip fill
            public string Fg { get; set; }       // chip text
            public string Bd { get; set; }       // chip border
            public string Style { get; set; }    // "solid" | "dashed"
            public string Bar { get; set; }      // 2px row-edge bar
        }

        // Ordered exactly as sev() branches: manual first, then crit, then High/Med/Low.
        public static Tier Of(JkrCopilotRule r)
        {
            if (r == null) return Low();

            if (string.Equals(r.Kind, "manual", StringComparison.OrdinalIgnoreCase))
                return new Tier
                {
                    Tag = "○ SEMAK MANUAL", Bg = "#F1F3F5", Fg = "#55606D",
                    Bd = "#98A2AE", Style = "dashed", Bar = "#98A2AE",
                };

            if (r.Crit)
                return new Tier
                {
                    Tag = "◆◆◆ KRITIKAL", Bg = "#FBEAE8", Fg = "#B3261E",
                    Bd = "#EFC9C5", Style = "solid", Bar = "#B3261E",
                };

            if (string.Equals(r.Sev, "High", StringComparison.OrdinalIgnoreCase))
                return new Tier
                {
                    Tag = "◆◆ HIGH", Bg = "#EDF1F6", Fg = "#1F3A5F",
                    Bd = "#C6D3E1", Style = "solid", Bar = "#1F3A5F",
                };

            if (string.Equals(r.Sev, "Med", StringComparison.OrdinalIgnoreCase))
                return new Tier
                {
                    Tag = "◆ MED", Bg = "#F5F1E8", Fg = "#8A6D2F",
                    Bd = "#E2DBC8", Style = "solid", Bar = "#8A6D2F",
                };

            return Low();
        }

        private static Tier Low() => new Tier
        {
            Tag = "◇ LOW", Bg = "#F1F3F5", Fg = "#6B7280",
            Bd = "#DBDFE4", Style = "solid", Bar = "#6B7280",
        };

        /// <summary>
        /// The row's diff line — what is wrong, not merely how much.
        /// Auto-fixable rules read "from → to"; everything else reads
        /// "requirement ≠ actual" (design: <c>diff()</c>).
        /// </summary>
        public static string Diff(JkrCopilotRule r)
        {
            if (r == null) return "";
            return !string.IsNullOrEmpty(r.From)
                ? r.From + "  →  " + r.To
                : r.Req + "  ≠  " + r.Act;
        }

        /// <summary>Sub-line: cells, rows affected, and whether an auto-fix exists.</summary>
        public static string Sub(JkrCopilotRule r)
        {
            if (r == null) return "";
            return r.Cells + " cells · " + r.Rows + " row" + (r.Rows > 1 ? "s" : "")
                 + (!string.IsNullOrEmpty(r.From) ? " · auto" : "");
        }

        /// <summary>A rule is auto-fixable exactly when the design gives it a fix source.</summary>
        public static bool Fixable(JkrCopilotRule r) => r != null && !string.IsNullOrEmpty(r.From);
    }
}
