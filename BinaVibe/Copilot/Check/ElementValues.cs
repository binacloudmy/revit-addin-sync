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

    /// <summary>One element's (or one type's) parameters, as ParameterLookup needs them.</summary>
    public interface IParameterSource
    {
        /// <summary>The parameter with exactly this name (case included), or null. Cheap.</summary>
        ValueReading? Exact(string name);

        /// <summary>Every parameter name: a full scan, done at most once per type.</summary>
        IEnumerable<string> Names();

        /// <summary>How many parameters it has (cheap): an instance whose count differs from
        /// the instance its type's name list was learned from is scanned itself.</summary>
        int Count { get; }
    }

    /// <summary>Picks the parameter a rule names, per check run: an exact-case name on the
    /// instance, then on the type; failing both, the one parameter whose name matches
    /// ignoring case (noted via MatchedName); several spellings → ambiguous; none → null.
    /// The work stays bounded on big models: exact lookups first; type readings are cached
    /// per (type, name); the full name scans run once per type, for the type and for its
    /// instances (an instance with a different parameter count is scanned itself). Only
    /// elements with no type are scanned one by one, and only when both exact lookups miss.</summary>
    public sealed class ParameterLookup
    {
        private static readonly HashSet<string> Pseudo =
            new HashSet<string>(new[] { "name", "type name", "family name", "category", "level" }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Element properties the reader answers when no parameter has the name (any case).</summary>
        public static bool IsPseudoName(string name) => Pseudo.Contains(name);

        private readonly Dictionary<string, (List<string> Names, int Count)> _names = new Dictionary<string, (List<string>, int)>(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), ValueReading?> _typeReads = new Dictionary<(string, string), ValueReading?>();

        /// <summary>Full name scans so far (for tests and diagnostics).</summary>
        public int Scans { get; private set; }

        /// <param name="typeKey">Identifies the type across the run (source + type id); null when the element has no type.</param>
        public ValueReading? Read(string wanted, IParameterSource instance, IParameterSource? type, string? typeKey)
        {
            var exact = instance.Exact(wanted) ?? (type == null ? null : TypeRead(type, typeKey, wanted));
            if (exact != null) return exact;

            bool Same(string n) => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase);
            var onInstance = Names("i|", instance, typeKey).Where(Same).ToList();
            var onType = type == null ? new List<string>() : Names("t|", type, typeKey).Where(Same).ToList();
            var all = onInstance.Concat(onType).Distinct(StringComparer.Ordinal).ToList();
            if (all.Count == 0) return null;
            if (all.Count > 1) return ValueReading.Ambiguous(wanted, all);
            var reading = (onInstance.Count > 0 ? instance.Exact(all[0]) : null)
                ?? (onType.Count > 0 ? TypeRead(type!, typeKey, all[0]) : null);
            return reading?.WithMatchedName(all[0]);
        }

        private ValueReading? TypeRead(IParameterSource type, string? typeKey, string name)
        {
            if (typeKey == null) return type.Exact(name);
            if (!_typeReads.TryGetValue((typeKey, name), out var reading)) _typeReads[(typeKey, name)] = reading = type.Exact(name);
            return reading;
        }

        private List<string> Names(string side, IParameterSource source, string? typeKey)
        {
            if (typeKey != null && _names.TryGetValue(side + typeKey, out var cached) && cached.Count == source.Count) return cached.Names;
            Scans++;
            var names = source.Names().Distinct(StringComparer.Ordinal).ToList();
            if (typeKey != null && !_names.ContainsKey(side + typeKey)) _names[side + typeKey] = (names, source.Count);
            return names;
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
