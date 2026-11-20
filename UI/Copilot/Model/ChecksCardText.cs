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

        /// <summary>A check of severity "info" is a note, not a doubt (UX review Q2): it is drawn calm, under "Notes", never amber under "Check N things".
        /// Error and warning (and a severity the pane does not know) keep the amber card.</summary>
        public static bool IsNote(string? severity) => string.Equals((severity ?? "").Trim(), "info", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>r13 (0.0.148), live fdda8df4 (hide tags) and fb713fc2 (section box) on 0.0.147: the server's read-back after a write
        /// ("Checked: section box on, name ..., box bottom -300 mm ...") is proof the write landed, not a note; inside the Notes box it read as
        /// one more remark. An info check whose text starts with "Checked:" and that has no answers is drawn as its own compact row (a small
        /// green tick and the line) above Notes. A warning or error that starts with "Checked:" stays amber; a check with answers keeps them.</summary>
        public static bool IsVerified(string? severity, string? text, int actionCount) =>
            IsNote(severity) && actionCount == 0 && ((text ?? "").TrimStart().StartsWith(VerifiedPrefix, System.StringComparison.OrdinalIgnoreCase)
                                                             || (text ?? "").TrimStart().StartsWith(VerifiedPrefixMs, System.StringComparison.OrdinalIgnoreCase));

        /// <summary>What the server's read-back line starts with: a stable token on the wire, in every chat language (the backend writes it,
        /// <see cref="IsVerified"/> reads it).</summary>
        public const string VerifiedPrefix = "Checked:";

        /// <summary>The prefix a Malay card shows in its place (r21, owner 21 Nov: the card follows the chat's language).</summary>
        public const string VerifiedPrefixMs = "Disemak:";

        /// <summary>The read-back line as the card draws it: on a Malay card "Checked:" reads "Disemak:"; the rest (the facts Revit read back,
        /// with their Revit names) is drawn as sent. An English card, or a line without the prefix, is the text trimmed, as before.</summary>
        public static string VerifiedDisplay(string? text, bool ms)
        {
            var t = (text ?? "").Trim();
            return ms && t.StartsWith(VerifiedPrefix, System.StringComparison.OrdinalIgnoreCase) ? VerifiedPrefixMs + t.Substring(VerifiedPrefix.Length) : t;
        }

        /// <summary>The calm card's title: "Notes" (BM: "Nota").</summary>
        public static string NotesTitle(bool ms) => ms ? "Nota" : "Notes";

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
            (ms ? "Tidak dapat: " : "Could not: ") + Tail(why, CardText.HeadlineMax);   // r12: whole, wrapped, up to the card's safety cap

        /// <summary>The end of a long reason, with a leading ellipsis (the end of a message is where the cause is).</summary>
        public static string Tail(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : "…" + s.Substring(s.Length - n + 1);
    }
}
