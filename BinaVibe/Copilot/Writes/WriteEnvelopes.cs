#nullable enable
// Envelopes the write runner itself produces (spec §10, §14). Revit-free.
//
// The tools build their own preview/commit envelopes; these are the ones no
// tool can: refusals before anything ran, a declined card, a batch stopped
// because a sibling call failed, drift, and estimate-only previews. Every one
// satisfies the §2.1 invariants (ok:false always carries error; not_found
// always carries candidates). Wording is model-facing English.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class WriteEnvelopes
    {
        /// <summary>A failure at a phase; status revit_busy for that code, failed otherwise.</summary>
        public static Envelope Failure(string code, string message, string phase, IEnumerable<string>? candidates = null)
        {
            var e = EnvelopeFactory.Failed(code, message, candidates?.ToArray());
            e.Phase = phase;
            if (code == ErrorCodes.RevitBusy) e.Status = EnvelopeStatus.RevitBusy;
            else if (code == ErrorCodes.Timeout) e.Status = EnvelopeStatus.Timeout;
            return e;
        }

        public static Envelope DocChanged(DocIdentity? expected, DocIdentity? active, string phase = EnvelopePhase.Read)
        {
            string message;
            if (active == null)
                message = "No Revit document is active, so nothing was changed. Ask the drafter to open " +
                          (expected?.ToString() ?? "the document") + " and send the request again.";
            else if (expected == null)
                message = "This turn started with no document open, and " + active + " is active now. Nothing was changed; start a new turn.";
            else
                message = "The active document is " + active + ", not " + expected + " where this turn started. " +
                          "Nothing was changed; ask the drafter to switch back, then send the request again.";
            return Failure(ErrorCodes.DocChanged, message, phase);
        }

        public static Envelope ModelChangedTwice() => Failure(ErrorCodes.DocChanged,
            "The model changed again between the preview and the commit, so nothing was changed. Read the model again, then send the change again.",
            EnvelopePhase.Commit);

        public static Envelope Drift(string? reason) => Failure(ErrorCodes.DiffDrift,
            "The commit did not match the preview, even after one fresh preview: " +
            (string.IsNullOrWhiteSpace(reason) ? "the result differed" : reason!.TrimEnd('.')) +
            ". It was rolled back; nothing was changed.",
            EnvelopePhase.Commit);

        /// <summary>A call that was not applied because another call of the same batch failed.</summary>
        public static Envelope NotRunBecauseSibling(int failedIndex, ToolCallDto failed, Envelope failure, string phase) =>
            Failure(ErrorCodes.RolledBack,
                "Not applied: call " + (failedIndex + 1).ToString(CultureInfo.InvariantCulture) + " (" + failed.Tool +
                ") in the same batch failed (" + (failure.Error?.Code ?? "failed") +
                "), so nothing in the batch was changed. Fix that call and send the batch again.",
                phase);

        /// <summary>next for a call that changes nothing: there is no card to review.</summary>
        public const string NoChangeNext = "Nothing would change, so there is nothing to apply. Tell the drafter what is already in place.";

        private static readonly System.Text.RegularExpressions.Regex SetSummary =
            new System.Text.RegularExpressions.Regex(@"^Set (?<what>.+?) on (?<on>\d+ [^;.]+)[;.]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// A typed write (set_parameters) that the recorder saw change nothing: the engine turned it into noop, but its
        /// summary, data and next were written as a change (live round 9, F-t04: a second call to types the first call
        /// had just set read "Set … on 10 types; read back 10 of 10", data.changed 10). Says it as nothing changed:
        /// data.changed 0, every target counted in data.unchanged, a summary that every value was already set.
        /// The read-back stays: it shows the values hold.
        /// </summary>
        public static void MarkUnchanged(Envelope env)
        {
            if (env == null) return;
            var m = SetSummary.Match(env.Summary ?? "");
            env.Summary = m.Success
                ? "Nothing to change: all " + m.Groups["on"].Value + " already had " + m.Groups["what"].Value + "."
                : "Nothing to change: every target already had the value.";
            env.Next = NoChangeNext;
            if (env.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
                && System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText()) is System.Text.Json.Nodes.JsonObject o)
            {
                int Int(string key) => o[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
                if (o.ContainsKey("changed"))
                {
                    o["unchanged"] = Int("unchanged") + Int("changed");
                    o["changed"] = 0;
                }
                env.Data = System.Text.Json.JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
            }
        }

        public static Envelope Rejected(Envelope preview, string planHash, string? reason)
        {
            var why = string.IsNullOrWhiteSpace(reason) ? "" : " Reason given: " + reason!.Trim();
            return new Envelope
            {
                Ok = true,
                Status = EnvelopeStatus.Rejected,
                Phase = EnvelopePhase.Preview,
                Summary = EnvelopeFactory.Shorten(
                    "The change was declined; nothing was changed." + why,
                    EnvelopeLimits.SummaryMaxChars),
                Count = preview.Count,
                // No handle: the preview's handle points at ids that were rolled back (final review M-6).
                PlanHash = planHash,
                Next = "Ask the drafter what to change instead; do not resend the same change.",
            };
        }

        /// <summary>The runner itself failed unexpectedly. <paramref name="mayHaveApplied"/> is
        /// true once a commit was attempted.</summary>
        public static Envelope Unexpected(Exception ex, bool mayHaveApplied) => mayHaveApplied
            ? MayHaveApplied("The write failed in the add-in during the commit (" + ex.GetType().Name + ": " + ex.Message +
                "). Changes may or may not have been applied; inspect the model before retrying.")
            : Failure(ErrorCodes.RuntimeError,
                "The write failed in the add-in before anything was committed (" + ex.GetType().Name + ": " + ex.Message +
                "). Nothing was changed.",
                EnvelopePhase.Preview);

        /// <summary>A write that may or may not have changed the model (final review M-7): phase
        /// commit, status partial, ok:false, so the client's late-post rule (phase commit, ok or
        /// partial) treats it as landed.</summary>
        public static Envelope MayHaveApplied(string message)
        {
            var e = Failure(ErrorCodes.MayHaveApplied, message, EnvelopePhase.Commit);
            e.Status = EnvelopeStatus.Partial;
            return e;
        }

        /// <summary>The card body for a call Revit cannot preview: only the plan's estimate.</summary>
        public static Envelope Estimate(WritePlan plan, string label)
        {
            var added = plan.ExpectedAddedByCategory ?? new Dictionary<string, int>();
            var deleted = plan.ExpectedDeletedByCategory ?? new Dictionary<string, int>();
            var parts = new List<string>();
            if (deleted.Values.Sum() > 0) parts.Add("delete " + Counts(deleted));
            if (added.Values.Sum() > 0) parts.Add("add " + Counts(added));
            if ((plan.TargetIds?.Length ?? 0) > 0) parts.Add("change " + plan.TargetIds!.Length.ToString(CultureInfo.InvariantCulture) + " existing element(s)");
            var what = parts.Count == 0 ? "no measured effect" : string.Join(", ", parts);
            var data = new Dictionary<string, object?>
            {
                ["estimate_only"] = true,
                ["label"] = label,
                ["expected_added_by_category"] = added,
                ["expected_deleted_by_category"] = deleted,
                ["targets"] = plan.TargetIds?.Length ?? 0,
            };
            return new Envelope
            {
                Ok = true,
                Status = EnvelopeStatus.Done,
                Phase = EnvelopePhase.Preview,
                Summary = EnvelopeFactory.Shorten(
                    "Estimate only: Revit cannot preview this change, so nothing was measured. Expected: " + what + ".",
                    EnvelopeLimits.SummaryMaxChars),
                Data = CopilotJson.ToElement(data),
                Next = "The drafter is reviewing an estimate-only card in Revit.",
            };
        }

        private static string Counts(Dictionary<string, int> byCategory) =>
            string.Join(", ", byCategory.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value.ToString(CultureInfo.InvariantCulture) + " " + kv.Key));
    }
}
