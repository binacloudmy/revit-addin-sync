using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The one place that answers "does a person decide this?" for a JKR spec check or a row of it
    /// (AI-504). Anything that offers an automatic action on a row (Fix via Copilot, AI-492; Fix All)
    /// asks here and must offer nothing, and skip the row, when the answer is yes. WPF- and Revit-free.
    ///
    /// Two levels, both from the existing catalog class (spec sheet "Kelas cell"):
    /// <list type="bullet">
    /// <item><b>Check</b>: <see cref="IsPersonDecidesCheck"/>. The check's class is Human in
    /// <see cref="SpecCatalog"/>: it never gets a verdict.</item>
    /// <item><b>Row</b>: <see cref="IsPersonDecides(SpecResult)"/>. A row (a finding that gives an issue)
    /// that is not an AI verdict: a Human or List check's finding, a context finding (a slab without a
    /// room, a value that only reads as the minimum when rounded, a prefixed grid), or something the
    /// scan could not judge. Such a row is listed, counts as passed in the % (AI-474), and is never
    /// auto-failed. This is the same test the pane uses to mark a row Human
    /// (<see cref="SpecModelIssues.IsAiVerdict"/>), so the two cannot drift.</item>
    /// </list>
    /// </summary>
    public static class SpecPersonDecides
    {
        /// <summary>AI-verifiable checks that also have a part a person decides (spec sheet class
        /// "AI + Human part", AI-504). Their class stays Ai: the documented part is judged by the add-in,
        /// and only the undocumented judgement is listed. Not counted in the Human class total.</summary>
        public static readonly IReadOnlyList<string> AiWithHumanPart = new[]
        {
            "A1.1", "A1.2", "A1.3", "A1.4", "A1.5", "A4.1", "A6.2", "C4.1.2",
        };

        /// <summary>True when the check's catalog class is Human. Accepts "A1.2", "a1.2" and the pane's
        /// "SPEC-A1.2". An unknown id is false.</summary>
        public static bool IsPersonDecidesCheck(string checkId)
        {
            var id = Resolve(checkId);
            var check = id == null ? null : SpecCatalog.Find(id);
            return check != null && check.Class == SpecCellClass.Human;
        }

        /// <summary>True when the check is AI-verifiable but has a person-decides part (see
        /// <see cref="AiWithHumanPart"/>): its rows may be either an AI verdict or a person's row, so
        /// ask <see cref="IsPersonDecides(SpecResult)"/> per row.</summary>
        public static bool HasPersonPart(string checkId)
        {
            var id = Resolve(checkId);
            return id != null && AiWithHumanPart.Contains(id, StringComparer.Ordinal);
        }

        private static string Resolve(string checkId)
        {
            var t = (checkId ?? "").Trim();
            if (t.StartsWith(SpecModelIssues.CheckIdPrefix, StringComparison.OrdinalIgnoreCase)) t = t.Substring(SpecModelIssues.CheckIdPrefix.Length);
            return SpecReport.ResolveCheckId(t);
        }

        /// <summary>True when the row is for a person: it gives an issue and is not an AI verdict.</summary>
        public static bool IsPersonDecides(SpecResult r) =>
            r != null && SpecModelIssues.GivesIssue(r) && !SpecModelIssues.IsAiVerdict(r);
    }
}
