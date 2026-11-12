#nullable enable
// The "Steps" list of a turn that ends in a plain answer: what ran, as the running card showed it (each finished step with its count),
// and the attempts that failed folded into ONE muted line ("2 earlier attempts failed, then it worked"). Pure.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class StepsSummary
    {
        /// <summary>The rows to list: the steps that worked (and a step still running), in order. Failed steps are not rows.</summary>
        public static IReadOnlyList<RunRow> WorkedRows(IEnumerable<ProgressStep>? steps) =>
            RunningCard.Rows((steps ?? Enumerable.Empty<ProgressStep>()).Where(s => s.State != StepState.Error)).ToList();

        /// <summary>How many attempts failed: failed tool steps, plus failed reads the evidence knows of that no step shows.</summary>
        public static int Failed(IEnumerable<ProgressStep>? steps, int failedEvidence = 0)
        {
            var fromSteps = ProgressTrail.UserVisible(steps).Count(s => s.State == StepState.Error && s.Phase == "tool");
            return System.Math.Max(fromSteps, failedEvidence);
        }

        /// <summary>"2 earlier attempts failed, then it worked" when something worked after them, "2 attempts failed" when nothing did; ""
        /// when none failed.</summary>
        public static string FailedLine(int failed, bool anyWorked, ReceiptLanguage lang)
        {
            if (failed <= 0) return "";
            var n = failed.ToString(CultureInfo.InvariantCulture);
            if (lang == ReceiptLanguage.Ms)
                return anyWorked ? n + " percubaan terdahulu gagal, kemudian berjaya" : n + " percubaan gagal";
            return anyWorked ? n + (failed == 1 ? " earlier attempt failed, then it worked" : " earlier attempts failed, then it worked")
                             : n + (failed == 1 ? " attempt failed" : " attempts failed");
        }

        public static string MoreDetailLabel(bool open, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? (open ? "Kurang butiran ▴" : "Lagi butiran ▾") : (open ? "Less detail ▴" : "More detail ▾");
    }
}
