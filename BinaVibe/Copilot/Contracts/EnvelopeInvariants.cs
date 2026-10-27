#nullable enable
// Envelope contract checks (plan §2.1; owned by W0, contracts). Revit-free.
//
// Violations() returns every way an envelope breaks
// contracts/copilot/envelope.schema.json that the C# types cannot rule out by
// themselves: allowed values, length caps, and the four invariants
//   1. ok == false                    => error is present
//   2. error.code == not_found        => error.candidates is present
//   3. phase == commit && ok          => verified.method != none, or status == noop
//   4. count == 0 on a read           => searched is present
// An empty list means the envelope is valid. EnvelopeBuilder.Build() (W1)
// and the contract tests both call it.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BinaVibe.Copilot.Contracts
{
    public static class EnvelopeInvariants
    {
        private static readonly Regex PlanHashPattern = new Regex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        /// <summary>Length in Unicode code points, as JSON Schema counts maxLength.</summary>
        public static int CodePoints(string s)
        {
            var n = 0;
            foreach (var c in s)
                if (!char.IsLowSurrogate(c)) n++;
            return n;
        }

        public static IReadOnlyList<string> Violations(Envelope e)
        {
            var v = new List<string>();

            if (!EnvelopeStatus.All.Contains(e.Status)) v.Add($"status '{e.Status}' is not one of {string.Join("|", EnvelopeStatus.All)}");
            if (!EnvelopePhase.All.Contains(e.Phase)) v.Add($"phase '{e.Phase}' is not one of {string.Join("|", EnvelopePhase.All)}");

            var summaryLength = e.Summary == null ? 0 : CodePoints(e.Summary);
            if (summaryLength < 1 || summaryLength > EnvelopeLimits.SummaryMaxChars)
                v.Add($"summary must be 1-{EnvelopeLimits.SummaryMaxChars} characters (has {summaryLength})");
            if (e.Next != null && CodePoints(e.Next) > EnvelopeLimits.NextMaxChars)
                v.Add($"next must be at most {EnvelopeLimits.NextMaxChars} characters");

            if (e.Data is JsonElement data && data.ValueKind != JsonValueKind.Object && data.ValueKind != JsonValueKind.Array)
                v.Add($"data must be a JSON object or array (is {data.ValueKind})");
            if (e.Count < 0) v.Add("count must be >= 0");
            if (e.Handle != null && e.Handle.Length == 0) v.Add("handle must not be empty");
            if (e.PlanHash != null && !PlanHashPattern.IsMatch(e.PlanHash)) v.Add("plan_hash must be sha256:<64 lowercase hex>");

            if (e.Searched != null && (e.Searched.Sources == null || e.Searched.Categories == null || e.Searched.NotSearched == null))
                v.Add("searched needs sources, categories and not_searched");

            if (e.Changes != null)
            {
                var t = e.Changes.Touched;
                if (t == null || t.Added == null || t.Modified == null || t.Deleted == null)
                    v.Add("changes.touched needs added, modified and deleted");
                var s = e.Changes.SideEffects;
                if (s != null)
                {
                    if (s.AddedByCategory == null || s.ModifiedByCategory == null || s.DeletedByCategory == null)
                        v.Add("changes.side_effects needs added_by_category, modified_by_category and deleted_by_category");
                    else if (s.AddedByCategory.Values.Concat(s.ModifiedByCategory.Values).Concat(s.DeletedByCategory.Values).Any(n => n < 0))
                        v.Add("changes.side_effects counts must be >= 0");
                }
            }

            if (e.Verified != null)
            {
                if (!VerifyMethods.All.Contains(e.Verified.Method)) v.Add($"verified.method '{e.Verified.Method}' is not one of {string.Join("|", VerifyMethods.All)}");
                if (e.Verified.Checked < 0) v.Add("verified.checked must be >= 0");
                if (e.Verified.Mismatches == null) v.Add("verified.mismatches is required");
            }

            if (e.Highlight != null && (e.Highlight.Selected < 0 || e.Highlight.VisibleInView < 0))
                v.Add("highlight counts must be >= 0");

            if (e.Warnings != null)
                foreach (var w in e.Warnings)
                {
                    if (w == null) { v.Add("warnings must not contain null"); continue; }
                    if (!WarningSeverity.All.Contains(w.Severity)) v.Add($"warning severity '{w.Severity}' is not one of {string.Join("|", WarningSeverity.All)}");
                    if (w.Text == null || w.Ids == null) v.Add("warning needs text and ids");
                }

            if (e.Dialogs != null && e.Dialogs.Any(d => d == null || d.Id == null || d.Text == null))
                v.Add("dialog needs id and text");

            if (e.Image != null)
            {
                if (e.Image.MediaType != EnvelopeLimits.ImageMediaType) v.Add("image.media_type must be image/png");
                if (string.IsNullOrEmpty(e.Image.Base64)) v.Add("image.base64 is required");
                if (e.Image.Width < 1 || e.Image.Width > EnvelopeLimits.ImageMaxPx || e.Image.Height < 1 || e.Image.Height > EnvelopeLimits.ImageMaxPx)
                    v.Add($"image must be 1-{EnvelopeLimits.ImageMaxPx} px on each side");
                if (e.Image.Caption == null) v.Add("image.caption is required");
            }

            if (e.Error != null)
            {
                if (!ErrorCodes.All.Contains(e.Error.Code)) v.Add($"error.code '{e.Error.Code}' is not one of {string.Join("|", ErrorCodes.All)}");
                if (e.Error.Message == null) v.Add("error.message is required");
            }

            // Invariant 1.
            if (!e.Ok && e.Error == null) v.Add("ok is false but error is missing");
            // Invariant 2.
            if (e.Error?.Code == ErrorCodes.NotFound && e.Error.Candidates == null) v.Add("not_found error must carry candidates (empty only when nothing similar exists)");
            // Invariant 3.
            if (e.Phase == EnvelopePhase.Commit && e.Ok && e.Status != EnvelopeStatus.Noop
                && (e.Verified == null || e.Verified.Method == VerifyMethods.None))
                v.Add("a successful commit must be verified (verified.method != none) unless status is noop");
            // Invariant 4.
            if (e.Phase == EnvelopePhase.Read && e.Count == 0 && e.Searched == null) v.Add("an empty read must say what was searched");

            return v;
        }
    }
}
