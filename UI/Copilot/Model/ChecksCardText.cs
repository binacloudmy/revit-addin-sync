#nullable enable
// The words and small decisions of the "Check N things" card and its "Next" row (design boards Main,
// SmartTags, SheetGen, SheetsExcel): the title with its singular and plural, which answer is the main one,
// and what an answered row says. Pure, so a test pins it; the drawing is in ChecksCard.
using System.Collections.Generic;
using System.Globalization;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ChecksCardText
    {
        /// <summary>"Check 1 thing" / "Check 3 things" (BM: "Semak 1 perkara" / "Semak 3 perkara"; BM has no plural form).</summary>
        public static string Title(int open, bool ms)
        {
            var n = open.ToString(CultureInfo.InvariantCulture);
            if (ms) return "Semak " + n + " perkara";
            return open == 1 ? "Check 1 thing" : "Check " + n + " things";
        }

        /// <summary>The label of the inline row under the card: "Next" (BM: "Seterusnya").</summary>
        public static string NextLabel(bool ms) => ms ? "Seterusnya" : "Next";

        /// <summary>The label of the link that re-opens an answered check.</summary>
        public static string ChangeLabel(bool ms) => ms ? "Ubah" : "Change";

        /// <summary>Which answer is the main one (drawn as the lime outline): the first answer that does something.
        /// "Leave out" (dismiss) is never the main one. -1 when none is.</summary>
        public static int MainIndex(IReadOnlyList<CheckAction> actions)
        {
            if (actions == null) return -1;
            for (var i = 0; i < actions.Count; i++)
                if (actions[i] != null && actions[i].Kind != ResultActionKinds.Dismiss) return i;
            return -1;
        }

        /// <summary>What the answer did: a prompt is sent as the next turn, anything else is a write that Undo takes back.</summary>
        public static string StatusText(string kind, bool ms) =>
            kind == ResultActionKinds.Prompt
                ? (ms ? "Dihantar." : "Sent.")
                : (ms ? "Ditulis. Undo membuangnya." : "Written. Undo takes it back.");

        /// <summary>The text of an answered row: the answer that was picked, then what it did ("Add these 2: Written. ...").</summary>
        public static string AnsweredText(string label, string kind, bool ms)
        {
            var status = StatusText(kind, ms);
            return string.IsNullOrWhiteSpace(label) ? status : label.Trim() + ": " + status;
        }

        /// <summary>Why an answer did not run, shown under its buttons (which come back so it can be tried again).</summary>
        public static string FailureText(string why, bool ms) =>
            (ms ? "Tidak dapat: " : "Could not: ") + Tail(why, 80);

        /// <summary>The end of a long reason, with a leading ellipsis (the end of a message is where the cause is).</summary>
        public static string Tail(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : "…" + s.Substring(s.Length - n + 1);
    }
}
