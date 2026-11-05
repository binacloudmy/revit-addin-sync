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
            // What the card needs to tell a schedule's companions from a surprise, as structure (no text parsing, no contract field).
            if (addedKinds != null && addedKinds.Values.Sum() == added && added > 0) data["added_kinds"] = addedKinds.ToDictionary(k => k.Key, k => k.Value);   // net48 has no Dictionary(IReadOnlyDictionary) constructor
            if (expected?.Adds != null) data["expected_adds"] = expected.Adds.Value;
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

        /// <summary>"Schedules 1, Viewports 1, Other 1" for a dictionary of kinds, largest first then by name.</summary>
        public static string KindsText(IReadOnlyDictionary<string, int> kinds) =>
            string.Join(", ", kinds.Where(k => k.Value > 0).OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + " " + k.Value));

        /// <summary>What comes with a schedule or a view, with a sane cap on each: up to 3 schedules, up to 5 viewports, up to 5 graphics
        /// styles and elements with no category together. Anything else, or more than that, is not "the companions of a schedule".</summary>
        public static bool AreScheduleCompanions(IReadOnlyDictionary<string, int> kinds)
        {
            int N(string k) => kinds.TryGetValue(k, out var n) ? n : 0;
            var known = new[] { "Schedules", "Viewports", "Graphics Styles", "Other" };
            if (kinds.Any(k => k.Value > 0 && !known.Contains(k.Key))) return false;
            return N("Schedules") <= 3 && N("Viewports") <= 5 && N("Graphics Styles") + N("Other") <= 5;
        }

        /// <summary>The sentence of the "expected N" warning; with the added kinds (adding up to <paramref name="made"/>) it names them.</summary>
        public static string ExpectedAdditionsText(int made, int expected, IReadOnlyDictionary<string, int>? addedKinds) =>
            "The code made " + made + " additions"
            + (addedKinds != null && made > 0 && addedKinds.Values.Sum() == made ? " (" + KindsText(addedKinds) + ")" : "")
            + "; the assistant expected " + expected + ".";

        /// <summary>The card's information row when the code added MORE than the model expected and every added kind is a companion of a
        /// schedule or a view (within the caps of <see cref="AreScheduleCompanions"/>); null otherwise: that stays an amber warning.</summary>
        public static string? CompanionNote(int made, int expected, IReadOnlyDictionary<string, int>? addedKinds, bool ms)
        {
            if (addedKinds == null || made <= expected || addedKinds.Values.Sum() != made || !AreScheduleCompanions(addedKinds)) return null;
            return ms
                ? "Kod menambah " + made + " elemen (" + KindsText(addedKinds) + "); pembantu menjangka " + expected + ". Yang lain datang bersama jadual atau pandangan itu."
                : "The code adds " + made + " elements (" + KindsText(addedKinds) + "); the assistant expected " + expected + ". The others come with the schedule or view itself.";
        }

        /// <summary>The model's estimate against what the code really did, as warnings the card shows. With
        /// <paramref name="addedKinds"/> (the categories of the added elements) an additions mismatch says WHAT was added.</summary>
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
                    yield return new WarningDto
                    {
                        Text = word == "additions"
                            ? ExpectedAdditionsText(got, want.Value, addedKinds)
                            : "The code made " + got + " " + word + "; the assistant expected " + want.Value + ".",
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
