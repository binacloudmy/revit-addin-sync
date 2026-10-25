#nullable enable
// What the check engine knows about one element, Revit-free: the Revit
// reader (RevitCheckReader) fills it, the evaluators only read it. Every
// parameter a rule needs is read up front into a ValueReading, so the
// evaluators can tell "absent" from "empty" from "could not be read".

using System;
using System.Collections.Generic;
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

        private ValueReading(bool found, string? unreadable, string? text, double? number, string? dimension)
        {
            Found = found;
            UnreadableReason = unreadable;
            Text = text;
            Number = number;
            Dimension = dimension;
        }

        /// <summary>The element has no parameter of that name.</summary>
        public static ValueReading Missing { get; } = new ValueReading(false, null, null, null, null);

        public static ValueReading Unreadable(string reason) =>
            new ValueReading(true, string.IsNullOrWhiteSpace(reason) ? "the value could not be read" : reason, null, null, null);

        public static ValueReading OfText(string? text) => new ValueReading(true, null, text, null, null);

        /// <param name="canonical">Value in the canonical unit of <paramref name="dimension"/>.</param>
        /// <param name="display">Revit's display string, when there is one.</param>
        public static ValueReading OfNumber(double canonical, string? dimension, string? display) =>
            new ValueReading(true, null, display ?? CheckUnits.Format(canonical, dimension), canonical, dimension);

        public bool IsReadable => Found && UnreadableReason == null;

        /// <summary>The value for a finding: canonical number with its unit when numeric, else the text.</summary>
        public string? Display => Number.HasValue ? CheckUnits.Format(Number.Value, Dimension) : Text;
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

        /// <summary>Set when the element's values cannot be trusted at all (its source
        /// is unloaded, or reading it threw): every outcome is not_verified.</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>Every parameter the rule needs, by display name (case-insensitive).</summary>
        public Dictionary<string, ValueReading> Params { get; } = new Dictionary<string, ValueReading>(StringComparer.OrdinalIgnoreCase);

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
