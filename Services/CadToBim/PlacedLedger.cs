// PlacedLedger — every door, window and room the builder placed, with its drawing position,
// so one that Revit deletes when the transaction commits (a failure resolution, e.g. "can't
// cut instance out of its host") is reported as skipped rather than silently dropped from the
// counts. Revit-free: ids are longs, the presence check is passed in. RecordingFailures
// feeds Revit's own words in through NoteFailure.

using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>The Revit failures the Review words plainly; any other keeps Revit's text.</summary>
    public enum FailureKind
    {
        Other,
        CannotCut,
        CannotMakeType,
    }

    public sealed class PlacedLedger
    {
        /// <summary>Revit's "Can't cut instance out of its host" failure (IDS_CANT_CUT_INST_OUT),
        /// read from a Revit 2027 journal. Other versions fall back to Revit's own text.</summary>
        public const string CannotCutGuid = "34a653ae-4577-4bc4-ae55-e2df7f5cfa9c";

        /// <summary>Revit's "Can't make type" failure (String_Family_CantDrawFamily): the family's
        /// geometry cannot be built in this host, e.g. a door sweep that collapses in a thin wall.
        /// Read from a Revit 2027 journal.</summary>
        public const string CannotMakeTypeGuid = "7c6abae0-cf9f-4d01-a19b-16961525f217";

        private sealed class Entry
        {
            public BuildCategory Category;
            public double X;
            public double Y;
        }

        private readonly Dictionary<long, Entry> _placed = new Dictionary<long, Entry>();
        private readonly Dictionary<long, (string Text, FailureKind Kind)> _notes = new Dictionary<long, (string, FailureKind)>();

        /// <summary>Elements placed and not yet settled.</summary>
        public int Count => _placed.Count;

        /// <summary>A placed element, at its drawing position (mm), the same point a skip of it
        /// would be marked at.</summary>
        public void Add(long id, BuildCategory category, double x, double y) =>
            _placed[id] = new Entry { Category = category, X = x, Y = y };

        /// <summary>Revit's failure text for an element. The first note per id wins: it is the
        /// failure that started the trouble.</summary>
        public void NoteFailure(long elementId, string revitText, FailureKind kind)
        {
            if (_notes.ContainsKey(elementId)) return;
            _notes[elementId] = (revitText, kind);
        }

        /// <summary>Revit's raw text noted for an element, or null.</summary>
        public string FailureText(long elementId) =>
            _notes.TryGetValue(elementId, out (string Text, FailureKind Kind) note) ? note.Text : null;

        /// <summary>
        /// Drops a placed element and its note without a skip, for one the builder is about to
        /// place again (a door re-placed without its masonry frame). Returns whether it was
        /// in the ledger. A later Settle neither reports it nor counts it.
        /// </summary>
        public bool Forget(long elementId)
        {
            _notes.Remove(elementId);
            return _placed.Remove(elementId);
        }

        /// <summary>The placed elements of a category that are no longer present and whose
        /// noted failure is of the given kind. Nothing is settled or dropped.</summary>
        public List<long> GoneWith(FailureKind kind, BuildCategory category, Func<long, bool> present)
        {
            var result = new List<long>();
            if (present == null) return result;
            foreach (KeyValuePair<long, Entry> pair in _placed)
            {
                if (pair.Value.Category != category) continue;
                if (!_notes.TryGetValue(pair.Key, out (string Text, FailureKind Kind) note) || note.Kind != kind) continue;
                if (!present(pair.Key)) result.Add(pair.Key);
            }
            return result;
        }

        /// <summary>The kind of a Revit failure, from its definition id.</summary>
        public static FailureKind KindOf(Guid failureId)
        {
            if (failureId == new Guid(CannotCutGuid)) return FailureKind.CannotCut;
            if (failureId == new Guid(CannotMakeTypeGuid)) return FailureKind.CannotMakeType;
            return FailureKind.Other;
        }

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
                if (_notes.TryGetValue(id, out (string Text, FailureKind Kind) note))
                {
                    text = note.Text;
                    detail = Detail(note.Text, note.Kind);
                }

                report.Skip(entry.Category, SkipReason.RemovedAfterPlacing, entry.X, entry.Y, detail);
                onRemoved?.Invoke(id, entry.Category, text);
            }

            return gone.Count;
        }

        /// <summary>The words for the Review row: a known failure in plain words, any other
        /// failure in Revit's own (trimmed, no closing full stop), null when there are none.</summary>
        public static string Detail(string revitText, FailureKind kind)
        {
            if (kind == FailureKind.CannotCut) return "could not cut it out of its wall";
            if (kind == FailureKind.CannotMakeType) return "its family cannot be built in this wall (try another door type in TYPES)";
            if (string.IsNullOrWhiteSpace(revitText)) return null;
            string text = revitText.Trim().TrimEnd('.').TrimEnd();
            return text.Length == 0 ? null : text;
        }
    }
}
