#nullable enable
// One prefix for every BINA undo entry (spec §7.6). Revit-free.
//
// Each committed write batch is one named entry in Revit's undo list,
// "BINA: Set Height Offset From Level (34)". The same prefix names every
// transaction the copilot commits, so the receipt recorder and the Undo button
// recognise ours. Legacy tools still name theirs "BinaVibe: …"; IsOurs accepts
// both until those paths are gone.

using System;

namespace BinaVibe.Copilot.Undo
{
    public static class BinaTransactionNames
    {
        public const string Prefix = "BINA: ";
        public const string LegacyPrefix = "BinaVibe";
        /// <summary>Revit caps transaction names; keep ours well inside it.</summary>
        public const int MaxLength = 120;

        /// <summary>"BINA: " + the label, trimmed to <see cref="MaxLength"/>.</summary>
        public static string For(string label)
        {
            var text = string.IsNullOrWhiteSpace(label) ? "change" : label.Trim();
            if (text.StartsWith(Prefix, StringComparison.Ordinal)) text = text.Substring(Prefix.Length);
            var name = Prefix + text;
            return name.Length <= MaxLength ? name : name.Substring(0, MaxLength - 1) + "…";
        }

        public static bool IsOurs(string? name) =>
            !string.IsNullOrEmpty(name)
            && (name!.StartsWith(Prefix.TrimEnd(), StringComparison.Ordinal)
                || name.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
