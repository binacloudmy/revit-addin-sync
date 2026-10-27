#nullable enable
// Value fingerprints of modified existing elements (ruling R75). Revit-free.
//
// Ids alone cannot prove the commit did what the preview showed: code can
// write values that depend on hash codes, clocks or the ids of elements it
// just created. So each modified existing element is fingerprinted after the
// preview's inner commits (before the rollback) and again after the commit,
// before the shape decision: every parameter's STORAGE value (AsString,
// AsInteger, AsDouble, AsElementId), its type id and its location/bbox. A
// reference to an element the batch itself added is written as "new:<kind>",
// because those ids always differ between preview and commit. Doubles are
// rounded far below anything a drafter can see (9 decimals; coordinates to
// 1e-6 ft) so recompute noise is not drift. Read-only parameters (Area,
// Volume, Length, Perimeter, formula-driven values) are left out: they follow
// from the settable values, location and bbox, which are fingerprinted, and
// their bit-level recompute differences must not read as drift. A parameter
// whose definition the batch itself created is keyed "<name>#new", since its
// definition id differs between preview and commit. At most MaxElements are
// compared, the first ones by id; the commit says how many were compared, and
// names any element whose values could not be read on one side.
// Residual (accepted): values of NEW elements are not compared; additions are
// matched by shape (category, type, level, bbox) only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>One parameter as read from Revit: its name, definition id, storage value
    /// ("s:…", "i:…", "d:…", "e:…") and whether it is read-only.</summary>
    public sealed class ParamSample
    {
        public ParamSample(string name, long definitionId, string value, bool isReadOnly)
        {
            Name = name ?? "";
            DefinitionId = definitionId;
            Value = value ?? "";
            IsReadOnly = isReadOnly;
        }

        public string Name { get; }
        public long DefinitionId { get; }
        public string Value { get; }
        public bool IsReadOnly { get; }
    }

    public static class ValueFingerprint
    {
        public const int MaxElements = 500;

        /// <summary>The fingerprint of one element from its parameters: read-only ones are left
        /// out, and a definition the batch added (<paramref name="added"/>) is keyed as new.</summary>
        public static string FromParameters(IEnumerable<ParamSample> parameters, string typeId, string location, ISet<long> added) =>
            Compute((parameters ?? Enumerable.Empty<ParamSample>())
                    .Where(p => p != null && !p.IsReadOnly)
                    .Select(p => new KeyValuePair<string, string>(ParameterKey(p.Name, p.DefinitionId, added), p.Value)),
                typeId, location);

        /// <summary>"name#definitionId", or "name#new" for a definition created inside the batch.</summary>
        public static string ParameterKey(string name, long definitionId, ISet<long>? added) =>
            (name ?? "") + "#" + (added != null && added.Contains(definitionId) ? "new" : definitionId.ToString(CultureInfo.InvariantCulture));

        /// <summary>A warning naming the elements whose values could not be read on one side
        /// (and so were not compared); null when there are none.</summary>
        public static string? UnreadWarning(BatchShape preview, BatchShape commit)
        {
            var ids = (preview?.Unreadable ?? new HashSet<long>()).Union(commit?.Unreadable ?? new HashSet<long>())
                .OrderBy(id => id).ToList();
            if (ids.Count == 0) return null;
            var shown = string.Join(", ", ids.Take(5).Select(i => i.ToString(CultureInfo.InvariantCulture)));
            return "The values of " + ids.Count.ToString(CultureInfo.InvariantCulture) + " changed element(s) could not be read and were not compared (ids " +
                   shown + (ids.Count > 5 ? " and " + (ids.Count - 5).ToString(CultureInfo.InvariantCulture) + " more" : "") + ").";
        }

        /// <summary>Hash of the parameter storage values (order-insensitive), type id and location.</summary>
        public static string Compute(IEnumerable<KeyValuePair<string, string>> parameters, string typeId, string location)
        {
            var sb = new StringBuilder();
            foreach (var kv in (parameters ?? Enumerable.Empty<KeyValuePair<string, string>>())
                         .OrderBy(kv => kv.Key, StringComparer.Ordinal).ThenBy(kv => kv.Value, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            sb.Append("type=").Append(typeId ?? "").Append('\n').Append("loc=").Append(location ?? "");
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return BitConverter.ToString(digest, 0, 16).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>A stored double, rounded below recompute noise.</summary>
        public static string Number(double value) =>
            Math.Round(value, 9, MidpointRounding.AwayFromZero).ToString("R", CultureInfo.InvariantCulture);

        /// <summary>A coordinate in feet, rounded to 1e-6 ft (about 0.0003 mm).</summary>
        public static string Coordinate(double feet) =>
            Math.Round(feet, 6, MidpointRounding.AwayFromZero).ToString("R", CultureInfo.InvariantCulture);

        /// <summary>The ids whose values are compared: the first <paramref name="cap"/> by id order.</summary>
        public static IReadOnlyList<long> Select(IEnumerable<long> ids, int cap = MaxElements) =>
            (ids ?? Enumerable.Empty<long>()).Distinct().OrderBy(id => id).Take(Math.Max(0, cap)).ToList();

        /// <summary>The commit envelope's warning when the cap cut the comparison; null otherwise.</summary>
        public static string? CapWarning(BatchShape shape) =>
            shape == null || shape.ValuesTotal <= shape.Values.Count
                ? null
                : "Values were compared for " + shape.Values.Count.ToString(CultureInfo.InvariantCulture) + " of " +
                  shape.ValuesTotal.ToString(CultureInfo.InvariantCulture) + " changed elements.";
    }
}
