#nullable enable
// r18 (live Round L): one wording for an undo, wherever the pane says it. Before, the same Undo read three ways: the card's banner
// "Undone. The model is back to how it was.", the thread line after Revit's own Undo "You undid: Revit code", and the thread line after
// the card's Undo button "Undid 1 BINA change(s) in Revit: BINA: Revit code." Now every line starts "Undone" ("Redone" for a redo), the
// way the banner and the card's status line ("Undone: this turn's changes are no longer in the model.") already did, and names the
// entries without the "BINA: " prefix: "Undone: Revit code", "Redone: Tag doors", "Undone: A, B, C and 2 more". Revit-free.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Undo
{
    public static class UndoWords
    {
        public const string Undone = "Undone: ";
        public const string Redone = "Redone: ";
        public const int MaxNamesShown = 3;
        public const int MaxNameLength = 80;
        public const string Unnamed = "a Copilot change";

        /// <summary>The entry's name as the drafter reads it: no "BINA: " prefix, not overlong.</summary>
        public static string Name(string? raw)
        {
            var n = (raw ?? "").Trim();
            if (n.StartsWith(BinaTransactionNames.Prefix, StringComparison.OrdinalIgnoreCase)) n = n.Substring(BinaTransactionNames.Prefix.Length).Trim();
            else if (n.StartsWith("BINA:", StringComparison.OrdinalIgnoreCase)) n = n.Substring(5).Trim();
            if (n.Length == 0) n = Unnamed;
            return n.Length <= MaxNameLength ? n : n.Substring(0, MaxNameLength - 1).TrimEnd() + "…";
        }

        public const string UndoneMs = "Dibuat asal: ";
        public const string RedoneMs = "Dibuat semula: ";
        public const string UnnamedMs = "satu perubahan Copilot";

        /// <summary>"Undone: Set Mark", "Redone: Set Mark and Tag doors", "Undone: A, B, C and 2 more" (names as given; see <see cref="Name"/>).</summary>
        public static string Line(bool redo, IEnumerable<string>? names) => Line(redo, names, false);

        /// <summary>r22 (live M2 b7a76924): the same line in a Malay chat, "Dibuat asal: Set parameter Comments", "Dibuat asal: A, B dan 2 lagi";
        /// the entry names stay exactly as Revit shows them (without the "BINA: " prefix).</summary>
        public static string Line(bool redo, IEnumerable<string>? names, bool ms)
        {
            var head = ms ? (redo ? RedoneMs : UndoneMs) : (redo ? Redone : Undone);
            var and = ms ? " dan " : " and ";
            var list = (names ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => ms ? MoreMs(n) : n).ToList();
            if (list.Count == 0) return head + (ms ? UnnamedMs : Unnamed);
            if (list.Count == 1) return head + list[0];
            if (list.Count <= MaxNamesShown) return head + string.Join(", ", list.Take(list.Count - 1)) + and + list[list.Count - 1];
            return head + string.Join(", ", list.Take(MaxNamesShown)) + and + (list.Count - MaxNamesShown) + (ms ? " lagi" : " more");
        }

        /// <summary>A line made in English (the undo job's summary, which the model reads too) as the pane shows it in a Malay chat: its
        /// "Undone: " / "Redone: " head and a closing "and N more" in Malay; the names between are left exactly as written.</summary>
        public static string ForPane(string? line, bool ms)
        {
            var text = line ?? "";
            if (!ms) return text;
            if (text.StartsWith(Undone, StringComparison.Ordinal)) text = UndoneMs + text.Substring(Undone.Length);
            else if (text.StartsWith(Redone, StringComparison.Ordinal)) text = RedoneMs + text.Substring(Redone.Length);
            else return text;
            var m = System.Text.RegularExpressions.Regex.Match(text, @" and (\d+) more$");
            return MoreMs(m.Success ? text.Substring(0, m.Index) + " dan " + m.Groups[1].Value + " lagi" : text);
        }

        private static readonly System.Text.RegularExpressions.Regex BatchMore = new System.Text.RegularExpressions.Regex(@"\(\+(\d+) more\)");

        /// <summary>r22 (PC P6 fba25214): the add-in's own batch mark in an entry name ("Set parameter Width (+1 more)", WriteLabels) reads
        /// "(+1 lagi)" on a Malay line; the rest of the name stays exactly as Revit lists it.</summary>
        public static string MoreMs(string name) => BatchMore.Replace(name ?? "", "(+$1 lagi)");

        /// <summary>The line for entries as Revit names them ("BINA: Revit code"): each stripped, repeats said once.</summary>
        public static string LineOfEntries(bool redo, IEnumerable<string>? entries) =>
            Line(redo, (entries ?? Array.Empty<string>()).Select(Name).Distinct(StringComparer.Ordinal));
    }
}
