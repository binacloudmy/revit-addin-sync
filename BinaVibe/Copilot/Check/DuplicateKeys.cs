#nullable enable
// duplicate_instances, Revit-free: elements of the same source, category,
// type and level whose bounding boxes agree to the rule's tolerance
// (quantised bbox key) are copies. In each group the lowest id passes as the
// one to keep and every other member fails, so the rule's fail handle holds
// exactly the copies a fix could remove (the hint asks the drafter to check
// which one is tagged or hosted first).
//
// The existing find_duplicate_walls (Inspectors.cs) is walls-only, curve
// based and returns no per-element verdicts, so it is not called here.
// Quantising can split two boxes that straddle a tolerance-cell boundary;
// true copies have identical boxes, so they always share a key.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    public sealed class BoxItem
    {
        public ElementRef Ref { get; }
        public string Source { get; }
        public string Category { get; }
        public string TypeKey { get; }
        public string LevelKey { get; }

        /// <summary>Bounding box corners in mm (x, y, z); null when the element has no box.</summary>
        public double[]? MinMm { get; }
        public double[]? MaxMm { get; }

        public BoxItem(ElementRef @ref, string source, string category, string typeKey, string levelKey, double[]? minMm, double[]? maxMm)
        {
            Ref = @ref;
            Source = source;
            Category = category;
            TypeKey = typeKey;
            LevelKey = levelKey;
            MinMm = minMm;
            MaxMm = maxMm;
        }
    }

    public static class DuplicateKeys
    {
        public const double DefaultToleranceMm = 1.0;
        private const double SmallestToleranceMm = 0.01;

        /// <summary>One outcome per item, in input order.</summary>
        public static IReadOnlyList<Evaluated> Evaluate(IEnumerable<BoxItem> items, double? toleranceMm)
        {
            var tol = Math.Max(SmallestToleranceMm, toleranceMm ?? DefaultToleranceMm);
            var list = items.ToList();
            var groups = new Dictionary<string, List<BoxItem>>(StringComparer.Ordinal);
            foreach (var item in list)
            {
                if (item.MinMm == null || item.MaxMm == null) continue;
                var key = Key(item, tol);
                if (!groups.TryGetValue(key, out var members)) groups[key] = members = new List<BoxItem>();
                members.Add(item);
            }

            var verdict = new Dictionary<BoxItem, Outcome>();
            foreach (var members in groups.Values)
            {
                if (members.Count < 2) continue;
                var ordered = members.OrderBy(m => m.Ref.Id).ToList();
                var keep = ordered[0];
                var copies = ordered.Count - 1;
                var copyIds = string.Join(", ", ordered.Skip(1).Take(5).Select(m => m.Ref.Id.ToString(CultureInfo.InvariantCulture)))
                    + (copies > 5 ? $" (+{copies - 5} more)" : "");
                verdict[keep] = Outcome.Pass($"{copies + 1} copies", $"kept; duplicates: {copyIds}");
                foreach (var copy in ordered.Skip(1))
                    verdict[copy] = Outcome.Fail(
                        $"duplicate of {keep.Ref.Id}: same type, level and bounding box (±{tol.ToString("0.##", CultureInfo.InvariantCulture)} mm)",
                        $"{copies + 1} copies");
            }

            return list.Select(item => new Evaluated(item.Ref,
                item.MinMm == null || item.MaxMm == null
                    ? Outcome.NotVerified("the element has no bounding box to compare", null, OutcomeCodes.NotApplicable)
                    : verdict.TryGetValue(item, out var o) ? o : Outcome.Pass())).ToList();
        }

        private static string Key(BoxItem item, double tol)
        {
            // + 0.0 turns -0 into 0, so -0.3 and 0.2 share a cell.
            static string Q(double v, double t) => (Math.Round(v / t) + 0.0).ToString("R", CultureInfo.InvariantCulture);
            var min = item.MinMm!;
            var max = item.MaxMm!;
            return string.Join("|", item.Source, item.Category, item.TypeKey, item.LevelKey,
                Q(min[0], tol), Q(min[1], tol), Q(min[2], tol), Q(max[0], tol), Q(max[1], tol), Q(max[2], tol));
        }
    }
}
