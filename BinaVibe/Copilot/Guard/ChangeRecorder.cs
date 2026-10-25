#nullable enable
// Stub created by W0; body owned by W1
// Records DocumentChanged for one document while a CallbackScope is set.
//
//   using (CallbackScope.Enter(doc, CallbackKind.Preview))
//   using (var rec = ChangeRecorder.Start(doc))
//   {
//       group.Start(); ... run the batch ...
//       var net = rec.Stop();            // BEFORE group.RollBack(): the rollback would net it to nothing
//       group.RollBack();
//       var (touched, side) = ChangeSetMath.Split(net, touchedIds, ourMarkerIds);
//   }
//
// Only events on this document, raised while one of our callbacks runs on it,
// are recorded (spec §7.3.4). Each event goes, in the order Revit raised it, to
// a ChangeJournal: commits and redos are forward changes, an undo reverts the
// last transaction, a group rollback reverts the group, and a rolled-back
// transaction is ignored (it never reached the model, so it must not erase an
// earlier committed change). Stop() unsubscribes and returns the journal's net
// change.
// Categories are read while the event runs: an element with no category is
// Revit's own bookkeeping (ChangeSetMath.BookkeepingCategory), except families
// and element types, which drafters see as "Families" / "Types". A deleted
// element has nothing left to read and lands in ChangeSetMath.UnknownCategory.
// Open, stop and dispose on Revit's UI thread.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace BinaVibe.Copilot.Guard
{
    public sealed class ChangeRecording : IDisposable
    {
        private readonly Document _doc;
        private readonly Autodesk.Revit.ApplicationServices.Application _app;
        private readonly ChangeJournal _journal = new ChangeJournal();
        private ChangeSet? _result;
        private bool _subscribed;

        internal ChangeRecording(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _app = doc.Application;
            _app.DocumentChanged += OnDocumentChanged;
            _subscribed = true;
        }

        public ChangeSet Stop()
        {
            Unsubscribe();
            return _result ??= _journal.Result();
        }

        public void Dispose() => Unsubscribe();

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            _app.DocumentChanged -= OnDocumentChanged;
        }

        private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || !doc.Equals(_doc) || CallbackScope.Current(_doc) == null) return;

                ChangeOperation op;
                switch (e.Operation)
                {
                    case UndoOperation.TransactionCommitted: op = ChangeOperation.Committed; break;
                    case UndoOperation.TransactionRedone: op = ChangeOperation.Redone; break;
                    case UndoOperation.TransactionUndone: op = ChangeOperation.Undone; break;
                    case UndoOperation.TransactionGroupRolledBack: op = ChangeOperation.GroupRolledBack; break;
                    default: return;   // TransactionRolledBack (and anything newer): nothing reached the model
                }

                var added = Ids(e.GetAddedElementIds(), readCategory: true);
                var modified = Ids(e.GetModifiedElementIds(), readCategory: true);
                var deleted = Ids(e.GetDeletedElementIds(), readCategory: false);   // nothing left to read
                _journal.Apply(op, added, modified, deleted);
            }
            catch (Exception ex)
            {
                // Never throw out of a Revit event handler.
                Debug.WriteLine($"[BinaVibe][changes] could not record a change: {ex.Message}");
            }
        }

        private List<long> Ids(ICollection<ElementId> ids, bool readCategory)
        {
            var values = new List<long>(ids.Count);
            foreach (var id in ids)
            {
                var value = (long)id.Value;
                values.Add(value);
                if (!readCategory) continue;
                var category = CategoryOf(_doc.GetElement(id));
                if (category != null) _journal.NoteCategory(value, category);
            }
            return values;
        }

        private static string? CategoryOf(Element? element)
        {
            if (element == null) return null;
            var category = element.Category;
            if (category != null && !string.IsNullOrWhiteSpace(category.Name)) return category.Name;
            if (element is Family) return "Families";
            if (element is ElementType) return "Types";
            return ChangeSetMath.BookkeepingCategory;
        }
    }

    public static class ChangeRecorder
    {
        public static ChangeRecording Start(Autodesk.Revit.DB.Document doc) => new ChangeRecording(doc);
    }
}
