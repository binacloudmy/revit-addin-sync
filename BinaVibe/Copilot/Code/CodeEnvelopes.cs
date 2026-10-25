#nullable enable
// The envelopes run_revit_code returns when the code ran (failures come from
// CodeFailure.ToEnvelope). Revit-free, so their shape is tested against
// EnvelopeInvariants; the Revit-bound tool supplies the measurements.
//
//   read     phase read, done, data {mode, output, truncated, lines, steps}
//   preview  phase preview, done (or noop), touched changes, warnings
//   commit   phase commit, done + verified (or noop), touched changes, warnings
// A write that changed nothing is noop with an inline warning. Lists in changes
// are capped at 50 rows; the full set goes behind a handle.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Code
{
    public sealed class ChangeCounts
    {
        public ChangeCounts(int added, int modified, int deleted)
        {
            Added = added;
            Modified = modified;
            Deleted = deleted;
        }

        public int Added { get; }
        public int Modified { get; }
        public int Deleted { get; }
        public int Total => Added + Modified + Deleted;
    }

    public static class CodeEnvelopes
    {
        public const string NothingChanged = "The code ran but changed nothing in the model.";

        public static Envelope Read(string intent, OutputBuffer output, CodeRunResult run)
        {
            var summary = "Ran (read only): " + intent + ". " + Lines(output) + (output.Truncated ? " Output was cut at 16 KB." : "");
            return new Envelope
            {
                Ok = true,
                Status = EnvelopeStatus.Done,
                Phase = EnvelopePhase.Read,
                Summary = EnvelopeFactory.Shorten(summary, EnvelopeLimits.SummaryMaxChars),
                Data = CopilotJson.ToElement(OutputData(CodePlans.Read, output, run)),
                Next = output.Truncated ? "Narrow the query (filter, group or count) to see everything." : null,
            };
        }

        public static Envelope Write(WritePhase phase, string intent, OutputBuffer output, CodeRunResult run,
            ChangesDto touched, SideEffectsDto? sideEffects, ChangeCounts counts, ExpectedChanges? expected,
            VerifiedDto? verified, string? handle, IReadOnlyList<WarningDto> warnings)
        {
            var all = new List<WarningDto>(warnings);
            all.AddRange(ExpectedWarnings(expected, counts));

            var noop = counts.Total == 0;
            if (noop) all.Insert(0, new WarningDto { Text = NothingChanged, Severity = WarningSeverity.Warning });

            var what = "adds " + counts.Added + ", modifies " + counts.Modified + ", deletes " + counts.Deleted;
            string summary;
            if (noop) summary = "Ran " + intent + ", but it changed nothing in the model.";
            else if (phase == WritePhase.Preview) summary = "Preview: " + intent + " (" + what + "). Nothing has changed yet.";
            else summary = "Done: " + intent + " (" + what.Replace("adds", "added").Replace("modifies", "modified").Replace("deletes", "deleted") + ").";

            var data = OutputData(CodePlans.Write, output, run);
            data["changes"] = new Dictionary<string, object?> { ["added"] = counts.Added, ["modified"] = counts.Modified, ["deleted"] = counts.Deleted };

            return new Envelope
            {
                Ok = true,
                Status = noop ? EnvelopeStatus.Noop : EnvelopeStatus.Done,
                Phase = EnvelopePhase.Of(phase),
                Summary = EnvelopeFactory.Shorten(summary, EnvelopeLimits.SummaryMaxChars),
                Data = CopilotJson.ToElement(data),
                Count = noop ? (int?)null : counts.Total,
                Handle = handle,
                Changes = noop ? null : new ChangeReportDto { Touched = touched, SideEffects = sideEffects },
                Verified = phase == WritePhase.Commit && !noop ? verified : null,
                Warnings = all.Count > 0 ? all : null,
                Next = phase == WritePhase.Preview && !noop ? "The drafter is reviewing this change in Revit's confirm card." : null,
            };
        }

        /// <summary>The model's estimate against what the code really did, as warnings the card shows.</summary>
        public static IEnumerable<WarningDto> ExpectedWarnings(ExpectedChanges? expected, ChangeCounts counts)
        {
            if (expected == null) yield break;
            foreach (var (word, want, got) in new[]
                     {
                         ("additions", expected.Adds, counts.Added),
                         ("modifications", expected.Modifies, counts.Modified),
                         ("deletions", expected.Deletes, counts.Deleted),
                     })
                if (want != null && want.Value != got)
                    yield return new WarningDto
                    {
                        Text = "The code made " + got + " " + word + "; the assistant expected " + want.Value + ".",
                        Severity = WarningSeverity.Warning,
                    };
        }

        /// <summary>At most 50 rows per list; total is the uncapped count.</summary>
        public static ChangesDto Cap(ChangesDto touched, out int total)
        {
            total = touched.Added.Count + touched.Modified.Count + touched.Deleted.Count;
            return new ChangesDto
            {
                Added = touched.Added.Take(EnvelopeLimits.MaxRows).ToList(),
                Modified = touched.Modified.Take(EnvelopeLimits.MaxRows).ToList(),
                Deleted = touched.Deleted.Take(EnvelopeLimits.MaxRows).ToList(),
            };
        }

        private static Dictionary<string, object?> OutputData(string mode, OutputBuffer output, CodeRunResult run) =>
            new Dictionary<string, object?>
            {
                ["mode"] = mode,
                ["output"] = output.Text,
                ["truncated"] = output.Truncated,
                ["lines"] = output.Lines,
                ["steps"] = run.Ticks,
            };

        private static string Lines(OutputBuffer output) =>
            output.Lines == 0 ? "No output (use Print(obj) to report results)." : output.Lines + (output.Lines == 1 ? " line" : " lines") + " of output.";
    }
}
