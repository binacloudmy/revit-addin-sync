#nullable enable
// Stub created by W0; body owned by W1
// Counts commits, undos and redos raised OUTSIDE CallbackScope (spec §7.4):
// if the count moved between preview and commit, the drafter changed the
// model in between and the preview no longer holds.
//
//   ExternalRevision.Attach(app)          once, at startup (W5 wires it in App.cs)
//   var r = ExternalRevision.Get(doc)     after the preview's RollBack
//   ExternalRevision.Get(doc) == r ?      at the start of the commit callback
//
// Our own preview, read, look and commit callbacks run inside a CallbackScope,
// so their events never count; a rollback changed nothing and never counts.
// A document's count is dropped on DocumentClosed when the close succeeded (a
// DocumentClosing can still be cancelled). Handlers never throw into Revit.
// The counting rules are the Revit-free RevisionCounter.

using System;
using System.Diagnostics;
using Autodesk.Revit.DB.Events;

namespace BinaVibe.Copilot.Guard
{
    public static class ExternalRevision
    {
        private static readonly RevisionCounter Counter = new RevisionCounter();
        private static readonly object Gate = new object();
        private static Autodesk.Revit.ApplicationServices.Application? _attached;

        /// <summary>Subscribes once; later calls with the same application are ignored.</summary>
        public static void Attach(Autodesk.Revit.ApplicationServices.Application app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            lock (Gate)
            {
                if (_attached != null) return;
                _attached = app;
            }
            app.DocumentChanged += OnDocumentChanged;
            app.DocumentClosing += OnDocumentClosing;
            app.DocumentClosed += OnDocumentClosed;
        }

        /// <summary>Commits/undos/redos on this document outside our scopes.</summary>
        public static long Get(Autodesk.Revit.DB.Document doc) => doc == null ? 0 : Counter.Get(doc);

        private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;
                Counter.Observe(doc, ChangesModel(e.Operation), CallbackScope.Current(doc) != null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BinaVibe][revision] could not count a change: {ex.Message}");
            }
        }

        private static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
        {
            try
            {
                if (e.Document != null) Counter.Closing(e.DocumentId, e.Document);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BinaVibe][revision] could not note a closing document: {ex.Message}");
            }
        }

        private static void OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
        {
            try
            {
                Counter.Closed(e.DocumentId, e.Status == RevitAPIEventStatus.Succeeded);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BinaVibe][revision] could not drop a closed document: {ex.Message}");
            }
        }

        internal static bool ChangesModel(UndoOperation op) =>
            op == UndoOperation.TransactionCommitted || op == UndoOperation.TransactionUndone || op == UndoOperation.TransactionRedone;
    }
}
