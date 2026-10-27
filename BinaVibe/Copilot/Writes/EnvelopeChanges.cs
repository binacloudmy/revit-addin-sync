#nullable enable
// The engine's last pass over a write envelope's `changes` (ruling R78; final
// review I-1, M-5). Revit-free.
//
//   - touched_by_category: per-category counts of the FULL touched set
//     (added / modified / deleted), from the recorder's categories, so the
//     confirm card can say "Change 34 Walls · Delete 2 Sheets, 5 Views";
//   - a code call keeps no modify rows with an empty field (the counts carry
//     them; a ": (empty) → (empty)" row says nothing);
//   - every touched list is cut to 50 rows, and when anything was cut the
//     envelope gets a handle to the ids that still exist (unless the tool
//     already set one);
//   - the §2.1 invariants are checked again, with the smallest repair for
//     anything this pass could have broken;
//   - deletions: Revit cannot report a deleted element's category, and code
//     names no targets, so after the preview rolled back (the deleted elements
//     exist again) their categories are read — at most 500, nothing else —
//     and the deleted counts are rewritten with them (re-review I-1). The
//     commit reuses those categories for the same ids.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class EnvelopeChanges
    {
        public const string UnknownCategory = "Other";
        public const int MaxDeletedLookups = 500;

        /// <summary>Reads the category of the first <see cref="MaxDeletedLookups"/> deleted ids (by id)
        /// through <paramref name="categoryOf"/>, and only those; an id it cannot name stays unknown.</summary>
        public static Dictionary<long, string> LookUpDeleted(IEnumerable<long> deletedIds, Func<long, string?> categoryOf)
        {
            var map = new Dictionary<long, string>();
            if (deletedIds == null || categoryOf == null) return map;
            foreach (var id in deletedIds.Distinct().OrderBy(id => id).Take(MaxDeletedLookups))
            {
                string? category;
                try { category = categoryOf(id); }
                catch (Exception) { category = null; }
                if (!string.IsNullOrWhiteSpace(category)) map[id] = category!;
            }
            return map;
        }

        /// <summary>Rewrites the deleted counts of touched_by_category and side effects from one
        /// call's deleted ids (with the categories they were counted under), using the looked-up
        /// categories where there are any.</summary>
        public static void ApplyDeletedCategories(Envelope env, IReadOnlyDictionary<long, string> touchedDeleted,
                                                  IReadOnlyDictionary<long, string> sideDeleted, IReadOnlyDictionary<long, string> lookedUp)
        {
            var changes = env?.Changes;
            if (changes == null) return;
            string Name(KeyValuePair<long, string> kv) =>
                lookedUp != null && lookedUp.TryGetValue(kv.Key, out var c) ? c
                : string.IsNullOrWhiteSpace(kv.Value) ? UnknownCategory : kv.Value;

            if (touchedDeleted != null && touchedDeleted.Count > 0)
            {
                changes.TouchedByCategory ??= new SideEffectsDto();
                changes.TouchedByCategory.DeletedByCategory = touchedDeleted.GroupBy(Name)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            }
            if (sideDeleted != null && sideDeleted.Count > 0)
            {
                changes.SideEffects ??= new SideEffectsDto();
                changes.SideEffects.DeletedByCategory = sideDeleted.GroupBy(Name)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            }
        }

        public static SideEffectsDto ByCategory(ChangesDto touched, IReadOnlyDictionary<long, string> categoryOf)
        {
            var by = new SideEffectsDto();
            if (touched == null) return by;
            foreach (var r in touched.Added) Bump(by.AddedByCategory, Category(categoryOf, r.Id));
            foreach (var m in touched.Modified) Bump(by.ModifiedByCategory, Category(categoryOf, m.Id));
            foreach (var r in touched.Deleted) Bump(by.DeletedByCategory, Category(categoryOf, r.Id));
            return by;
        }

        /// <summary>Removes modify rows that name no field (code edits the recorder saw).</summary>
        public static void DropEmptyFieldRows(ChangesDto touched)
        {
            touched?.Modified.RemoveAll(m => string.IsNullOrEmpty(m.Field));
        }

        /// <summary>Cuts every touched list to <see cref="EnvelopeLimits.MaxRows"/>; when one was
        /// cut and the envelope has no handle yet, <paramref name="putHandle"/> stores
        /// <paramref name="handleRefs"/> (ids that still exist) and its handle is set.</summary>
        public static void Cap(Envelope env, IReadOnlyList<ElementRef> handleRefs, Func<IReadOnlyList<ElementRef>, string?> putHandle)
        {
            var t = env?.Changes?.Touched;
            if (t == null) return;
            var max = EnvelopeLimits.MaxRows;
            var cut = t.Added.Count > max || t.Modified.Count > max || t.Deleted.Count > max;
            if (!cut) return;
            if (t.Added.Count > max) t.Added = t.Added.Take(max).ToList();
            if (t.Modified.Count > max) t.Modified = t.Modified.Take(max).ToList();
            if (t.Deleted.Count > max) t.Deleted = t.Deleted.Take(max).ToList();
            if (string.IsNullOrEmpty(env!.Handle) && handleRefs != null && handleRefs.Count > 0)
            {
                string? handle = null;
                try { handle = putHandle?.Invoke(handleRefs); }
                catch (Exception) { handle = null; }
                if (!string.IsNullOrEmpty(handle)) env.Handle = handle;
            }
        }

        /// <summary>The invariants this envelope broke (for the log), after the smallest repair that
        /// keeps it honest: summary/next cut to length, ok:false always with an error, and an ok
        /// commit without read-back downgraded to partial.</summary>
        public static IReadOnlyList<string> Recheck(Envelope env)
        {
            var violations = EnvelopeInvariants.Violations(env);
            if (violations.Count == 0) return violations;
            Trace.TraceWarning("[BINA][writes] envelope repaired after the engine's pass: " + string.Join("; ", violations));

            env.Summary = EnvelopeFactory.Shorten(string.IsNullOrWhiteSpace(env.Summary) ? env.Error?.Message ?? "(no summary)" : env.Summary,
                EnvelopeLimits.SummaryMaxChars);
            if (env.Next != null) env.Next = EnvelopeFactory.Shorten(env.Next, EnvelopeLimits.NextMaxChars);
            if (env.Count < 0) env.Count = null;
            if (env.Error != null && env.Error.Code == ErrorCodes.NotFound && env.Error.Candidates == null) env.Error.Candidates = new List<string>();
            if (!env.Ok && env.Error == null)
                env.Error = new ErrorDto { Code = ErrorCodes.RuntimeError, Message = "The write failed without saying why." };
            if (env.Phase == EnvelopePhase.Commit && env.Ok && env.Status != EnvelopeStatus.Noop
                && (env.Verified == null || env.Verified.Method == VerifyMethods.None))
            {
                env.Ok = false;
                env.Status = EnvelopeStatus.Partial;
                env.Error ??= new ErrorDto { Code = ErrorCodes.RuntimeError, Message = "The change was committed but not verified." };
            }
            if (env.PlanHash != null && EnvelopeInvariants.Violations(env).Any(v => v.StartsWith("plan_hash", StringComparison.Ordinal))) env.PlanHash = null;
            return violations;
        }

        private static string Category(IReadOnlyDictionary<long, string> categoryOf, long id) =>
            categoryOf != null && categoryOf.TryGetValue(id, out var c) && !string.IsNullOrWhiteSpace(c) ? c : UnknownCategory;

        private static void Bump(Dictionary<string, int> counts, string category) =>
            counts[category] = counts.TryGetValue(category, out var n) ? n + 1 : 1;
    }
}
