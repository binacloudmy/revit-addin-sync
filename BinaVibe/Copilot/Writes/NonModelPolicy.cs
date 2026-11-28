#nullable enable
// Which changed elements the force path may treat as harmless annotation (r18 audit). Revit-free.
//
// An explicit allow-list, decided from the element's class and its base classes (Revit's own type names, verified in the
// RevitAPI 2023 and 2027 XML): tags (IndependentTag; SpatialElementTag: room, space and area tags), dimensions (Dimension,
// spot dimensions included), text notes (TextNote) and generic annotation symbols (AnnotationSymbol), plus an element type the
// write ADDED (a new FamilySymbol or DuctType). Nothing else is exempt: datums (Level, Grid, ReferencePlane, scope boxes), views,
// sheets, schedules, analytical members, detail lines, filled regions and any other view-specific content still drift, roll
// back and get no force card. An existing element type is never exempt (editing it changes every instance).

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Writes
{
    public static class NonModelPolicy
    {
        private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
        {
            "IndependentTag", "SpatialElementTag", "Dimension", "TextNote", "AnnotationSymbol",
        };

        /// <summary>True for an allow-listed annotation, or for an element type when <paramref name="added"/>.
        /// <paramref name="classChain"/>: the element's class name, then its base classes.</summary>
        public static bool IsExempt(IEnumerable<string>? classChain, bool added)
        {
            var chain = (classChain ?? Enumerable.Empty<string>()).ToList();
            if (chain.Any(Exempt.Contains)) return true;
            return added && chain.Contains("ElementType");
        }
    }
}
