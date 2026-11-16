using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>AI-330: changing the type of pipes, ducts or cable trays makes Revit re-apply the new
    /// type's routing preferences, so the connected fittings are swapped or regenerated. This builds
    /// the card warning that says so. Revit-free: the engine passes in names, this decides and words.</summary>
    public static class FittingChangeNote
    {
        /// <summary>Fitting kinds named in a sentence before the rest fold into "n other".</summary>
        public const int MaxKinds = 4;

        /// <summary>Null when no pipe/duct/tray changed type or no fitting changed.
        /// <paramref name="curvesRetyped"/>: category name of each curve whose type changed.
        /// The fitting lists hold one label (family name) per fitting added, modified or deleted.</summary>
        public static string? Build(IReadOnlyCollection<string> curvesRetyped, IReadOnlyCollection<string> fittingsAdded,
                                    IReadOnlyCollection<string> fittingsModified, IReadOnlyCollection<string> fittingsDeleted)
        {
            if (curvesRetyped == null || curvesRetyped.Count == 0) return null;
            var parts = new List<string>();
            Add(parts, "replaced or added", fittingsAdded);
            Add(parts, "removed", fittingsDeleted);
            Add(parts, "modified", fittingsModified);
            if (parts.Count == 0) return null;
            return "Changing the type of " + Counted(curvesRetyped) + " made Revit re-apply the new type's routing preferences, "
                   + "so connected fittings change: " + string.Join("; ", parts) + ". Check the junctions after this change.";
        }

        private static void Add(List<string> parts, string verb, IReadOnlyCollection<string>? labels)
        {
            if (labels == null || labels.Count == 0) return;
            parts.Add(Counted(labels) + " " + verb);
        }

        /// <summary>"3 Elbow - Generic, 2 Tee - Generic" (largest first, at most MaxKinds, then "n other").</summary>
        internal static string Counted(IEnumerable<string> labels)
        {
            var groups = labels.Select(l => string.IsNullOrWhiteSpace(l) ? "fitting" : l.Trim())
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var shown = groups.Take(MaxKinds).Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + g.Key).ToList();
            var rest = groups.Skip(MaxKinds).Sum(g => g.Count());
            if (rest > 0) shown.Add(rest.ToString(CultureInfo.InvariantCulture) + " other");
            return string.Join(", ", shown);
        }
    }
}
