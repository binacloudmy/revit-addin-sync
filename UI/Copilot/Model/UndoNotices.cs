#nullable enable
// The thread's "You undid: …" lines (r12, 0.0.145). When the drafter uses Revit's own Undo or Redo (Ctrl+Z, the Undo
// list) on a BINA entry, the chat says so in a small line where the thread stood at that moment. A burst (Revit's Undo
// of several entries at once, or Ctrl+Z pressed again within a few seconds) is one line.
// The lines live only in the pane: never in Thread, the saved history or the model's transcript (UndoNotes tells the
// model on its next turn). The pane's own Undo/Redo button is not noted: its card already says so.
// Revit-free and WPF-free.
using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Undo;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class UndoNotice
    {
        public UndoChangeKind Kind;
        /// <summary>How many thread messages stood above it: the line draws after message <c>After - 1</c>.</summary>
        public int After;
        public List<string> Names = new List<string>();
        public DateTime At;

        public string Text => UndoNotices.TextOf(Kind, Names);
    }

    public sealed class UndoNotices
    {
        /// <summary>Changes this close together (and with no new message between) are one line.</summary>
        public static readonly TimeSpan Burst = TimeSpan.FromSeconds(3);
        public const int MaxNamesShown = 3;
        public const int MaxNameLength = 80;
        public const int MaxNotices = 40;

        private readonly List<UndoNotice> _items = new List<UndoNotice>();

        public IReadOnlyList<UndoNotice> Items => _items;

        /// <summary>A change of the undo stack. True when it made or extended a line (the thread should be drawn again).</summary>
        public bool Record(UndoChange? change, int threadCount, DateTime now)
        {
            if (change == null || change.ByPane || string.IsNullOrEmpty(change.TurnId)) return false;
            if (change.Kind != UndoChangeKind.Undone && change.Kind != UndoChangeKind.Redone) return false;
            if (threadCount <= 0) return false;   // nothing on screen to anchor it to (a new, empty chat)
            var name = Strip(change.Name);
            var last = _items.Count > 0 ? _items[_items.Count - 1] : null;
            if (last != null && last.Kind == change.Kind && last.After == threadCount && now - last.At <= Burst)
            {
                if (!last.Names.Contains(name, StringComparer.Ordinal)) last.Names.Add(name);
                last.At = now;
                return true;
            }
            _items.Add(new UndoNotice { Kind = change.Kind, After = threadCount, Names = { name }, At = now });
            if (_items.Count > MaxNotices) _items.RemoveAt(0);
            return true;
        }

        /// <summary>The lines that draw after the message at <paramref name="index"/> (0-based).</summary>
        public IEnumerable<UndoNotice> After(int index) => _items.Where(n => n.After == index + 1);

        /// <summary>A new chat, or another one continued: the lines belonged to the thread that was on screen.</summary>
        public void Clear() => _items.Clear();

        /// <summary>The entry's name as the drafter reads it: no "BINA: " prefix, not overlong.</summary>
        public static string Strip(string? raw)
        {
            var n = (raw ?? "").Trim();
            if (n.StartsWith(BinaTransactionNames.Prefix, StringComparison.OrdinalIgnoreCase)) n = n.Substring(BinaTransactionNames.Prefix.Length).Trim();
            else if (n.StartsWith("BINA:", StringComparison.OrdinalIgnoreCase)) n = n.Substring(5).Trim();
            if (n.Length == 0) n = "a Copilot change";
            return n.Length <= MaxNameLength ? n : n.Substring(0, MaxNameLength - 1).TrimEnd() + "…";
        }

        /// <summary>"You undid: Set Mark", "You redid: Set Mark and Tag doors", "You undid: A, B, C and 2 more".</summary>
        public static string TextOf(UndoChangeKind kind, IReadOnlyList<string> names)
        {
            var head = kind == UndoChangeKind.Redone ? "You redid: " : "You undid: ";
            var list = (names ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (list.Count == 0) return head + "a Copilot change";
            if (list.Count == 1) return head + list[0];
            if (list.Count <= MaxNamesShown) return head + string.Join(", ", list.Take(list.Count - 1)) + " and " + list[list.Count - 1];
            return head + string.Join(", ", list.Take(MaxNamesShown)) + " and " + (list.Count - MaxNamesShown) + " more";
        }
    }
}
