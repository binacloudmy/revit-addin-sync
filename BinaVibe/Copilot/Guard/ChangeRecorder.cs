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
// are recorded (spec §7.3.4). Commits and redos fill the forward set; undos and
// rollbacks fill the reverted set, translated back to what they reverted (an
// undone addition deletes the element, so its id is a reverted Added). Stop()
// unsubscribes and returns ChangeSetMath.Subtract(forward, reverted).
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
        private readonly ChangeSet _forward = new ChangeSet();
        private readonly ChangeSet _reverted = new ChangeSet();
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
            return _result ??= ChangeSetMath.Subtract(_forward, _reverted);
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

                if (e.Operation == UndoOperation.TransactionCommitted || e.Operation == UndoOperation.TransactionRedone)
                {
                    Record(_forward, _forward.Added, e.GetAddedElementIds(), readCategory: true);
                    Record(_forward, _forward.Modified, e.GetModifiedElementIds(), readCategory: true);
                    Record(_forward, _forward.Deleted, e.GetDeletedElementIds(), readCategory: false);
                }
                else
                {
                    Record(_reverted, _reverted.Added, e.GetDeletedElementIds(), readCategory: false);
                    Record(_reverted, _reverted.Deleted, e.GetAddedElementIds(), readCategory: true);
                    Record(_reverted, _reverted.Modified, e.GetModifiedElementIds(), readCategory: true);
                }
            }
            catch (Exception ex)
            {
                // Never throw out of a Revit event handler.
                Debug.WriteLine($"[BinaVibe][changes] could not record a change: {ex.Message}");
            }
        }

        private void Record(ChangeSet set, List<long> list, ICollection<ElementId> ids, bool readCategory)
        {
            foreach (var id in ids)
            {
                var value = (long)id.Value;
                list.Add(value);
                if (!readCategory || set.CategoryOf.ContainsKey(value)) continue;
                var category = CategoryOf(_doc.GetElement(id));
                if (category != null) set.CategoryOf[value] = category;
            }
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
