#nullable enable
// The result card's one way in (result marks design, rules 7 and 12; build step 2), Revit-free and WPF-free:
//   keys     with the card focused: Up and Down move between rows, Enter shows the row in the model, Shift+Enter also
//            selects it in Revit, A shows all, H hides or shows the marks. In the model a mark is used with the mouse;
//            Revit keeps its keys (nothing here is bound outside the card).
//   buttons  every body: the row is the button (show in model), a small arrow selects in Revit, the section header has
//            "Hide marks" / "Show in model" and, for elements in another view, "Go to view". The older separate buttons
//            ("Show them", "Highlight in model", "Select them in Revit", the view row's "Open view") are gone.
// Every fixed label is English (rule 11).

using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum CardKeyAction { None, Previous, Next, Show, ShowAndSelect, ShowAll, ToggleMarks }

    public static class CardKeys
    {
        /// <summary>What a key does on a focused card. <paramref name="key"/> is WPF's key name ("Up", "Return", "A");
        /// <paramref name="typing"/> is true while a text box has the focus (it keeps every key).</summary>
        public static CardKeyAction For(string? key, bool shift, bool ctrlOrAlt, bool typing)
        {
            if (typing || ctrlOrAlt || string.IsNullOrEmpty(key)) return CardKeyAction.None;
            switch (key)
            {
                case "Up": return shift ? CardKeyAction.None : CardKeyAction.Previous;
                case "Down": return shift ? CardKeyAction.None : CardKeyAction.Next;
                case "Enter":
                case "Return": return shift ? CardKeyAction.ShowAndSelect : CardKeyAction.Show;
                case "A": return shift ? CardKeyAction.None : CardKeyAction.ShowAll;
                case "H": return shift ? CardKeyAction.None : CardKeyAction.ToggleMarks;
                default: return CardKeyAction.None;
            }
        }

        /// <summary>The row the cursor moves to among <paramref name="count"/> rows; -1 is "no row yet". Down from none is the
        /// first row, Up from none the last; it stops at both ends (no wrap).</summary>
        public static int Move(int current, int count, CardKeyAction a)
        {
            if (count <= 0) return -1;
            if (current < 0 || current >= count) return a == CardKeyAction.Previous ? count - 1 : a == CardKeyAction.Next ? 0 : -1;
            if (a == CardKeyAction.Next) return current + 1 < count ? current + 1 : current;
            if (a == CardKeyAction.Previous) return current > 0 ? current - 1 : current;
            return current;
        }
    }

    public static class CardButtons
    {
        public const string HideMarks = "Hide marks";
        public const string ShowInModel = "Show in model";
        public const string GoToView = "Go to view";
        public const string ShowAll = "Show all";
        /// <summary>The small arrow on a row.</summary>
        public const string ArrowTip = "Select in Revit";
        /// <summary>What a row click does.</summary>
        public const string RowTip = "Show in the model (Enter). Shift+Enter also selects in Revit.";
        public const string ViewRowTip = "Open this view in Revit";
        public const string SheetRowTip = "Open this sheet in Revit";
        /// <summary>A row whose elements the server did not send: it has nothing to show.</summary>
        public const string NothingToShowTip = "Copilot did not get which elements these are, so it cannot show them.";

        /// <summary>The buttons gone from every card body (the row and its arrow do what they did).</summary>
        public static readonly IReadOnlyList<string> Removed = new[] { "Show them", "Highlight in model", "Select them in Revit", "Open view" };

        /// <summary>The section header's buttons, left to right: "Go to view" when the elements are in another view, then
        /// "Hide marks" while the marks show, else "Show in model". None when the turn has nothing to mark.</summary>
        public static List<string> Header(bool hasMarks, bool marksShown, bool inOtherView)
        {
            var list = new List<string>();
            if (!hasMarks) return list;
            if (inOtherView) list.Add(GoToView);
            list.Add(marksShown ? HideMarks : ShowInModel);
            return list;
        }

        /// <summary>A body's buttons that stay on its action bar after step 2 (each has no row to live on).</summary>
        public static IReadOnlyList<string> Kept(ResultKind kind)
        {
            switch (kind)
            {
                case ResultKind.Created: return new[] { "Copy list" };
                case ResultKind.Changes: return new[] { "Open Type Properties", "Open as grid", "Copy table" };
                case ResultKind.Selection: return new[] { "Reset isolate" };
                default: return new string[0];
            }
        }
    }
}
