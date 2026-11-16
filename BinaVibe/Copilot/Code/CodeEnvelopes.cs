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
        /// <summary>What a stored script prints first when it stops on purpose before making anything (it then
        /// asks the drafter, or the drafter chose to skip).</summary>
        public const string NothingMadeMarker = "NOTHING MADE";

        /// <summary>A run_revit_code envelope whose output starts with <see cref="NothingMadeMarker"/>: a deliberate
        /// stop, not an issue. Its noop gets no "changed nothing" warning (live round 3, problem 4: the warning
        /// became a "1 issue" card after the drafter chose to skip).</summary>
        public static bool IsDeliberateStop(Envelope? env)
        {
            if (env?.Data is not { } data || data.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (!data.TryGetProperty("output", out var output) || output.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            return (output.GetString() ?? "").TrimStart().StartsWith(NothingMadeMarker, StringComparison.Ordinal);
        }

        public const string ChangedNothingText = "The code ran but changed nothing.";

        /// <summary>What the write engine does to a run_revit_code call it found changed nothing (it has already set
        /// status noop): the summary says so, and a warning says so too unless the script stopped on purpose
        /// (<see cref="IsDeliberateStop"/>), so the result card shows no "1 issue" for a deliberate stop.</summary>
        public static void MarkChangedNothing(Envelope env)
        {
            env.Summary = ChangedNothingText;
            if (IsDeliberateStop(env)) return;
            env.Warnings ??= new List<WarningDto>();
            env.Warnings.Add(new WarningDto { Text = ChangedNothingText, Severity = WarningSeverity.Warning });
        }

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
            IDictionary<string, object?>? ui = null, IReadOnlyList<WarningDto>? uiWarnings = null)
        {
            var counts = new ChangeCounts(added, 0, deleted);
            var warnings = ExpectedWarnings(expected == null ? null : new ExpectedChanges(expected.Adds, null, expected.Deletes), counts).ToList();
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
            if (phase == WritePhase.Preview) builder.WithNext("This is a preview: the change applies next and can be undone.");
            return builder.Build();
        }

        /// <summary>"read" | "preview" | "commit" as the builder's phase (null is read).</summary>
        public static WritePhase? WritePhaseOf(string phase) =>
            phase == EnvelopePhase.Commit ? WritePhase.Commit
            : phase == EnvelopePhase.Preview ? WritePhase.Preview
            : (WritePhase?)null;

        /// <summary>The counts a run's result is checked against for the "the assistant expected N" warning: the model's own
        /// code keeps its estimate; a stored script (<see cref="RunRevitCodeArgs.Playbook"/> set) decides what it makes, so a
        /// guess the model put in <c>expected</c> is not held against it (live 13 Nov, E-d01: the stored dimension script made
        /// the 4 strings it should; the card warned "the assistant expected 5"). Only the warning: whether the call is a write
        /// still follows <see cref="RunRevitCodeArgs.Expected"/> (CodePlans, ModeClassifier.WithExpected).</summary>
        public static ExpectedChanges? ExpectedToCheck(RunRevitCodeArgs args) => args?.Playbook != null ? null : args?.Expected;

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
                ["output"] = HeadTail(output.Text, out var cut),
                ["truncated"] = output.Truncated || cut,
                ["lines"] = output.Lines,
                ["steps"] = run.Ticks,
            };

        /// <summary>Output past 2 KB keeps its first and last lines: a long dump only adds noise
        /// to the reply, and a total printed last must survive.</summary>
        internal static string HeadTail(string text, out bool cut, int max = 2048)
        {
            cut = text.Length > max;
            if (!cut) return text;
            var head = text.Substring(0, max * 2 / 3);
            var tail = text.Substring(text.Length - max / 3);
            head = head.Substring(0, Math.Max(0, head.LastIndexOf('\n')));
            tail = tail.Substring(tail.IndexOf('\n') + 1);
            var omitted = text.Split('\n').Length - head.Split('\n').Length - tail.Split('\n').Length;
            return head + (omitted > 0 ? "\n… " + omitted + " lines omitted …\n" : "\n…\n") + tail;
        }

        private static string Lines(OutputBuffer output) =>
            output.Lines == 0 ? "No output (use Print(obj) to report results)." : output.Lines + (output.Lines == 1 ? " line" : " lines") + " of output.";
    }
}
