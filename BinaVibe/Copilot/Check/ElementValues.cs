#nullable enable
// What the check engine knows about one element, Revit-free: the Revit
// reader (RevitCheckReader) fills it, the evaluators only read it. Every
// parameter a rule needs is read up front into a ValueReading, so the
// evaluators can tell "absent" from "empty" from "could not be read".

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    /// <summary>One parameter as read from an element (instance first, then its type).</summary>
    public sealed class ValueReading
    {
        /// <summary>The parameter exists on the element or its type.</summary>
        public bool Found { get; }

        /// <summary>Set when the parameter exists but its value could not be read.</summary>
        public string? UnreadableReason { get; }

        /// <summary>The value as Revit displays it; null when the parameter has no value.</summary>
        public string? Text { get; }

        /// <summary>Numeric value in the canonical unit of <see cref="Dimension"/>
        /// (length mm, area m², volume m³, angle degrees), or the plain number when
        /// Dimension is null. Null for text and element-id parameters.</summary>
        public double? Number { get; }

        /// <summary>One of the CheckUnits dimensions; null for a dimensionless or text value.</summary>
        public string? Dimension { get; }

        /// <summary>The value is a name (an ElementId parameter such as Family or Type):
        /// compared as text only, never read as a number or quantity.</summary>
        public bool TextOnly { get; }

        /// <summary>The parameter's real name when it was found only by ignoring case
        /// ("Fire_Rating" for a rule's "fire_rating"); null for an exact match.</summary>
        public string? MatchedName { get; }

        /// <summary>Several parameters match the name when case is ignored: the reading is
        /// unreadable (ambiguous_parameter), never one of them picked silently.</summary>
        public IReadOnlyList<string>? AmbiguousNames { get; }

        private ValueReading(bool found, string? unreadable, string? text, double? number, string? dimension,
            bool textOnly = false, string? matchedName = null, IReadOnlyList<string>? ambiguousNames = null)
        {
            Found = found;
            UnreadableReason = unreadable;
            Text = text;
            Number = number;
            Dimension = dimension;
            TextOnly = textOnly;
            MatchedName = matchedName;
            AmbiguousNames = ambiguousNames;
        }

        public static ValueReading Ambiguous(string wanted, IReadOnlyList<string> candidates) =>
            new ValueReading(true, $"'{wanted}' matches {candidates.Count} parameters when case is ignored ({string.Join(", ", candidates)}): name the one to use",
                null, null, null, ambiguousNames: candidates);

        public ValueReading WithMatchedName(string name) =>
            new ValueReading(Found, UnreadableReason, Text, Number, Dimension, TextOnly, name, AmbiguousNames);

        /// <summary>The element has no parameter of that name.</summary>
        public static ValueReading Missing { get; } = new ValueReading(false, null, null, null, null);

        public static ValueReading Unreadable(string reason) =>
            new ValueReading(true, string.IsNullOrWhiteSpace(reason) ? "the value could not be read" : reason, null, null, null);

        public static ValueReading OfText(string? text) => new ValueReading(true, null, text, null, null);

        /// <summary>A name: compared as text, never parsed as a quantity.</summary>
        public static ValueReading OfName(string? name) => new ValueReading(true, null, name, null, null, textOnly: true);

        /// <summary>An ElementId parameter (Family, Type, Level...): the referenced element's
        /// display text (AsValueString, else its name), never the id. A negative id is no
        /// element (empty); an element whose name cannot be read is unreadable.</summary>
        public static ValueReading OfElementId(long id, string? valueString, string? elementName)
        {
            if (id < 0) return OfName(null);
            var name = !string.IsNullOrWhiteSpace(valueString) ? valueString : elementName;
            return string.IsNullOrWhiteSpace(name)
                ? Unreadable($"element {id} has no name to compare")
                : OfName(name);
        }

        /// <param name="canonical">Value in the canonical unit of <paramref name="dimension"/>.</param>
        /// <param name="display">Revit's display string, when there is one.</param>
        public static ValueReading OfNumber(double canonical, string? dimension, string? display) =>
            new ValueReading(true, null, display ?? CheckUnits.Format(canonical, dimension), canonical, dimension);

        public bool IsReadable => Found && UnreadableReason == null;

        /// <summary>The value for a finding: canonical number with its unit when numeric, else the text.</summary>
        public string? Display => Number.HasValue ? CheckUnits.Format(Number.Value, Dimension) : Text;
    }

    /// <summary>Picks the parameter a rule names: an exact-case name on the instance, then on
    /// the type; failing both, the one parameter whose name matches ignoring case (noted via
    /// MatchedName); several such → ambiguous. Null when nothing matches. The Revit reader
    /// passes the case-insensitive candidates it found; tests pass fakes.</summary>
    public static class ParameterLookup
    {
        /// <param name="instance">Instance parameters by exact name (ordinal keys).</param>
        /// <param name="type">Type parameters by exact name (ordinal keys).</param>
        public static ValueReading? Read(string wanted, IReadOnlyDictionary<string, ValueReading> instance, IReadOnlyDictionary<string, ValueReading> type)
        {
            if (instance.TryGetValue(wanted, out var exact) || type.TryGetValue(wanted, out exact)) return exact;
            var candidates = instance.Keys.Concat(type.Keys)
                .Where(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count > 1) return ValueReading.Ambiguous(wanted, candidates);
            var name = candidates[0];
            return (instance.TryGetValue(name, out var r) ? r : type[name]).WithMatchedName(name);
        }
    }

    /// <summary>One element as the check engine sees it.</summary>
    public sealed class ElementValues
    {
        public ElementRef Ref { get; set; } = new ElementRef(0, null);

        /// <summary>"host" or "link: &lt;name&gt;".</summary>
        public string Source { get; set; } = "host";

        public string? Category { get; set; }

        /// <summary>Element.Name: the type name for most instances, the element's own name
        /// for levels, grids, views and sheets.</summary>
        public string? Name { get; set; }

        public string? TypeName { get; set; }
        public string? FamilyName { get; set; }
        public string? LevelName { get; set; }

        /// <summary>The element's category has no level association (grids, levels, Revit and
        /// CAD links, views and sheets, reference planes, scope boxes): a level scope does
        /// not apply to it (ruling R39).</summary>
        public bool HasNoLevel { get; set; }

        /// <summary>Set when the element's values cannot be trusted at all (its source
        /// is unloaded, or reading it threw): every outcome is not_verified.</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>Every parameter the rule needs, by the name the rule uses (exact; the reader
        /// resolves case differences through ParameterLookup).</summary>
        public Dictionary<string, ValueReading> Params { get; } = new Dictionary<string, ValueReading>(StringComparer.Ordinal);

        public ValueReading Get(string? name) =>
            name != null && Params.TryGetValue(name, out var reading) ? reading : ValueReading.Missing;

        /// <summary>Adds or replaces one reading; returns this element.</summary>
        public ElementValues With(string name, ValueReading reading)
        {
            Params[name] = reading;
            return this;
        }
    }
}
