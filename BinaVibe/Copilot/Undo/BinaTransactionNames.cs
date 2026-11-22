#nullable enable
// One prefix for every BINA undo entry (spec §7.6). Revit-free.
//
// Each committed write batch is one named entry in Revit's undo list, e.g.
// "BINA: Set parameter Mark". The same prefix names every transaction the
// copilot commits, so the drafter finds ours in Revit's Undo list and the
// Undo button can check the exact name it expects.

using System;

namespace BinaVibe.Copilot.Undo
{
    public static class BinaTransactionNames
    {
        public const string Prefix = "BINA: ";
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
    }
}
