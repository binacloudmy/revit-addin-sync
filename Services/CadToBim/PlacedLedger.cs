// PlacedLedger — every door, window and room the builder placed, with its drawing position,
// so one that Revit deletes when the transaction commits (a failure resolution, e.g. "can't
// cut instance out of its host") is reported as skipped rather than silently dropped from the
// counts. Revit-free: ids are longs, the presence check is passed in. RecordingFailures
// feeds Revit's own words in through NoteFailure.

using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class PlacedLedger
    {
        /// <summary>Revit's "Can't cut instance out of its host" failure (IDS_CANT_CUT_INST_OUT),
        /// read from a Revit 2027 journal. Other versions fall back to Revit's own text.</summary>
        public const string CannotCutGuid = "34a653ae-4577-4bc4-ae55-e2df7f5cfa9c";

        private sealed class Entry
        {
            public BuildCategory Category;
            public double X;
            public double Y;
        }

        private readonly Dictionary<long, Entry> _placed = new Dictionary<long, Entry>();
        private readonly Dictionary<long, (string Text, bool CannotCut)> _notes = new Dictionary<long, (string, bool)>();

        /// <summary>Elements placed and not yet settled.</summary>
        public int Count => _placed.Count;

        /// <summary>A placed element, at its drawing position (mm), the same point a skip of it
        /// would be marked at.</summary>
        public void Add(long id, BuildCategory category, double x, double y) =>
            _placed[id] = new Entry { Category = category, X = x, Y = y };

        /// <summary>Revit's failure text for an element. The first note per id wins: it is the
        /// failure that started the trouble.</summary>
        public void NoteFailure(long elementId, string revitText, bool cannotCut)
        {
            if (_notes.ContainsKey(elementId)) return;
            _notes[elementId] = (revitText, cannotCut);
        }

        /// <summary>Revit's raw text noted for an element, or null.</summary>
        public string FailureText(long elementId) =>
            _notes.TryGetValue(elementId, out (string Text, bool CannotCut) note) ? note.Text : null;

        /// <summary>
        /// Every placed element that is no longer present leaves its id list and becomes a
        /// RemovedAfterPlacing skip at its drawing position, with Revit's reason when one was
        /// noted. Settled entries are dropped, so a second call adds nothing. onRemoved (optional)
        /// hears each one, for the log. Returns how many were removed.
        /// </summary>
        public int Settle(BuildReport report, Func<long, bool> present, Action<long, BuildCategory, string> onRemoved = null)
        {
            if (report == null || present == null) return 0;

            var gone = new List<long>();
            foreach (KeyValuePair<long, Entry> pair in _placed)
                if (!present(pair.Key)) gone.Add(pair.Key);

            foreach (long id in gone)
            {
                Entry entry = _placed[id];
                _placed.Remove(id);

                switch (entry.Category)
                {
                    case BuildCategory.Door: report.DoorIds.Remove(id); break;
                    case BuildCategory.Window: report.WindowIds.Remove(id); break;
                    case BuildCategory.Room: report.RoomIds.Remove(id); break;
                }

                string detail = null;
                string text = null;
                if (_notes.TryGetValue(id, out (string Text, bool CannotCut) note))
                {
                    text = note.Text;
                    detail = Detail(note.Text, note.CannotCut);
                }

                report.Skip(entry.Category, SkipReason.RemovedAfterPlacing, entry.X, entry.Y, detail);
                onRemoved?.Invoke(id, entry.Category, text);
            }

            return gone.Count;
        }

        /// <summary>The words for the Review row: a known cut failure in plain words, any other
        /// failure in Revit's own (trimmed, no closing full stop), null when there are none.</summary>
        public static string Detail(string revitText, bool cannotCut)
        {
            if (cannotCut) return "could not cut it out of its wall";
            if (string.IsNullOrWhiteSpace(revitText)) return null;
            string text = revitText.Trim().TrimEnd('.').TrimEnd();
            return text.Length == 0 ? null : text;
        }
    }
}
