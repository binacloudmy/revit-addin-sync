#nullable enable
// The envelopes run_revit_code returns when the code ran (failures come from
// CodeFailure.ToEnvelope). Revit-free, so their shape is tested against
// EnvelopeInvariants; the Revit-bound tool supplies the measurements.
//
//   read     phase read, done, data {mode, output, truncated, lines, steps, ui}
//   preview  phase preview, done, touched added/deleted, expected warnings
//   commit   phase commit, done + verified, touched added/deleted
// Writes run inside W3's transaction, so W3's DocumentChanged record adds the
// edits, side effects, warnings and noop after it commits. The commit summary
// therefore never says "Done": it must stay true when W3 marks the call noop.
// Lists are capped at 50 rows; the full set goes behind a handle.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;

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
        /// <param name="ui">What the host did with the code's view, zoom and selection requests (data.ui).</param>
        public static Envelope Read(string intent, OutputBuffer output, CodeRunResult run,
            IDictionary<string, object?>? ui = null, IReadOnlyList<WarningDto>? warnings = null)
        {
            var summary = "Ran (read only): " + intent + ". " + Lines(output) + (output.Truncated ? " Output was cut at 16 KB." : "");
            var data = OutputData(CodePlans.Read, output, run);
            if (ui != null) data["ui"] = ui;
            var builder = EnvelopeBuilder.Read(EnvelopeFactory.Shorten(summary, EnvelopeLimits.SummaryMaxChars))
                .WithData(data)
                .WithWarnings(warnings ?? Array.Empty<WarningDto>());
            if (output.Truncated) builder.WithNext("Narrow the query (filter, group or count) to see everything.");
            return builder.Build();
        }

        /// <summary>A write run inside the batch runner's own transaction (W3's engine). Added and deleted
        /// ids are measured by the tool inside that transaction; edits to existing elements, side effects,
        /// warnings and noop come from the runner's DocumentChanged record after it commits, so this
        /// envelope is always "done" and reports only what the tool can know.</summary>
        public static Envelope HostedWrite(WritePhase phase, string intent, OutputBuffer output, CodeRunResult run,
            ChangesDto touched, int added, int deleted, ExpectedChanges? expected, VerifiedDto? verified, string? handle,
            IDictionary<string, object?>? ui = null, IReadOnlyList<WarningDto>? uiWarnings = null,
            IReadOnlyDictionary<string, int>? addedKinds = null)
        {
            var counts = new ChangeCounts(added, 0, deleted);
            var warnings = ExpectedWarnings(expected == null ? null : new ExpectedChanges(expected.Adds, null, expected.Deletes), counts, addedKinds).ToList();
            if (uiWarnings != null) warnings.AddRange(uiWarnings);
            // Ran, not Done: whether edits to existing elements happened is known only to W3 after it
            // commits, and it marks the call noop when nothing changed (fix round 3, m1).
            var summary = phase == WritePhase.Preview
                ? "Preview: " + intent + " (adds " + added + ", deletes " + deleted + "; edits to existing elements are listed under changes). Nothing has changed yet."
                : added == 0 && deleted == 0
                    ? "Ran: " + intent + ". No elements were created or removed; edits to existing elements, if any, are listed under changes."
                    : "Ran: " + intent + " (added " + added + ", deleted " + deleted + "; edits to existing elements are listed under changes).";

            var data = OutputData(CodePlans.Write, output, run);
            data["changes"] = new Dictionary<string, object?> { ["added"] = added, ["deleted"] = deleted };
            if (ui != null) data["ui"] = ui;

            var builder = EnvelopeBuilder.Write(phase, EnvelopeFactory.Shorten(summary, EnvelopeLimits.SummaryMaxChars))
                .WithData(data)
                .WithChanges(touched)
                .WithWarnings(warnings);
            if (handle != null) builder.WithHandle(handle);
            if (phase == WritePhase.Commit && verified != null) builder.WithVerified(verified);
            if (phase == WritePhase.Preview) builder.WithNext("The drafter is reviewing this change in Revit's confirm card.");
            return builder.Build();
        }

        /// <summary>"read" | "preview" | "commit" as the builder's phase (null is read).</summary>
        public static WritePhase? WritePhaseOf(string phase) =>
            phase == EnvelopePhase.Commit ? WritePhase.Commit
            : phase == EnvelopePhase.Preview ? WritePhase.Preview
            : (WritePhase?)null;

        /// <summary>Category names of what a write added that come with a schedule or a view: the schedule, its viewport
        /// on a sheet, its graphics styles, and elements with no category (Revit's own internal ones).</summary>
        public static readonly IReadOnlyCollection<string> CompanionKinds = new[] { "Schedules", "Viewports", "Graphics Styles", "Other" };

        /// <summary>"Schedules 1, Viewports 1, Other 1" for a dictionary of kinds, largest first then by name.</summary>
        public static string KindsText(IReadOnlyDictionary<string, int> kinds) =>
            string.Join(", ", kinds.Where(k => k.Value > 0).OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + " " + k.Value));

        /// <summary>The model's estimate against what the code really did, as warnings the card shows. With
        /// <paramref name="addedKinds"/> (the categories of the added elements) an additions mismatch says WHAT was added:
        /// "The code made 3 additions (Schedules 1, Viewports 1, Other 1); the assistant expected 1."</summary>
        public static IEnumerable<WarningDto> ExpectedWarnings(ExpectedChanges? expected, ChangeCounts counts, IReadOnlyDictionary<string, int>? addedKinds = null)
        {
            if (expected == null) yield break;
            foreach (var (word, want, got) in new[]
                     {
                         ("additions", expected.Adds, counts.Added),
                         ("modifications", expected.Modifies, counts.Modified),
                         ("deletions", expected.Deletes, counts.Deleted),
                     })
                if (want != null && want.Value != got)
                {
                    var what = word == "additions" && addedKinds != null && addedKinds.Values.Sum() == got && got > 0
                        ? " (" + KindsText(addedKinds) + ")"
                        : "";
                    yield return new WarningDto
                    {
                        Text = "The code made " + got + " " + word + what + "; the assistant expected " + want.Value + ".",
                        Severity = WarningSeverity.Warning,
                    };
                }
        }

        private static readonly System.Text.RegularExpressions.Regex ExpectedNote = new System.Text.RegularExpressions.Regex(
            @"^The code made (\d+) additions \((.+)\); the assistant expected (\d+)\.$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>True when this warning only says the code added MORE than the model expected and every thing it added is
        /// a companion of a schedule or a view (see <see cref="CompanionKinds"/>): information, not a problem.
        /// <paramref name="text">"The code made N additions (kinds); the assistant expected M."</paramref></summary>
        public static bool IsCompanionNote(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var m = ExpectedNote.Match(text!);
            if (!m.Success) return false;
            if (int.Parse(m.Groups[1].Value) <= int.Parse(m.Groups[3].Value)) return false;
            var kinds = m.Groups[2].Value.Split(',').Select(k => System.Text.RegularExpressions.Regex.Replace(k.Trim(), @"\s+\d+$", ""));
            return kinds.All(k => CompanionKinds.Contains(k));
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
