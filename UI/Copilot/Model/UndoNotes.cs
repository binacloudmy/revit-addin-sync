#nullable enable
// What the model must be told after the drafter undid (or redid) its writes (live test 0.0.93:
// after an Undo the model's next turn called its own earlier write "did not persist").
// Revit-free. The pane feeds it UndoService's changes; the router asks for the note when the
// next turn starts and appends it to that turn's text, so no protocol field is needed.
using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Undo;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class UndoNotes
    {
        private const int MaxNames = 3;
        private const int MaxNameLength = 80;

        private readonly object _gate = new object();
        /// <summary>Per turn: the names of its entries that are undone right now, in undo order.</summary>
        private readonly Dictionary<string, List<string>> _undone = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        /// <summary>Per turn: how many undone entries the model has already been told about.</summary>
        private readonly Dictionary<string, int> _told = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public void Record(UndoChange? change)
        {
            if (change == null || change.Kind == UndoChangeKind.Other || string.IsNullOrEmpty(change.TurnId)) return;
            lock (_gate)
            {
                if (!_undone.TryGetValue(change.TurnId, out var list))
                {
                    _undone[change.TurnId] = list = new List<string>();
                    _order.Add(change.TurnId);
                }
                if (change.Kind == UndoChangeKind.Undone) list.Add(change.Name ?? "");
                else
                {
                    var at = list.LastIndexOf(change.Name ?? "");
                    if (at >= 0) list.RemoveAt(at);
                    else if (list.Count > 0) list.RemoveAt(list.Count - 1);
                }
            }
        }

        /// <summary>A new chat (or another one continued): the model's session starts over, nothing is owed.</summary>
        public void Reset()
        {
            lock (_gate) { _undone.Clear(); _told.Clear(); _order.Clear(); }
        }

        /// <summary>The note for the turn about to start, or null when the model's picture is still right.
        /// Taking it marks what it says as told.</summary>
        public string? Take()
        {
            lock (_gate)
            {
                var lines = new List<string>();
                foreach (var turn in _order)
                {
                    var now = _undone[turn];
                    _told.TryGetValue(turn, out var told);
                    if (now.Count == told) continue;
                    var names = string.Join("; ", now.Take(MaxNames).Select(Name));
                    if (now.Count > told)
                    {
                        var n = now.Count - told;
                        lines.Add("the user undid " + n + " of your earlier change" + (n == 1 ? "" : "s")
                                  + (names.Length > 0 ? " (" + names + ")" : "") + " in Revit");
                    }
                    else
                    {
                        var n = told - now.Count;
                        lines.Add("the user redid " + n + " change" + (n == 1 ? "" : "s") + " you made earlier, so "
                                  + (n == 1 ? "it is" : "they are") + " back in the model");
                    }
                    _told[turn] = now.Count;
                }
                if (lines.Count == 0) return null;
                return "[BINA add-in note: " + string.Join("; ", lines) + ". "
                       + "Undone changes are not in the model now; they were taken back on purpose, so do not report them as failed or as "
                       + "\"did not persist\", and do not apply them again unless the user asks. Read the model again before relying on those values.]";
            }
        }

        private static string Name(string raw)
        {
            var n = (raw ?? "").Trim();
            return n.Length <= MaxNameLength ? n : n.Substring(0, MaxNameLength - 1) + "…";
        }
    }
}
