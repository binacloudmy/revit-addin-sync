// Pane [Undo] for the latest Copilot change (AI-202).
//
// Old behaviour: [Undo] posted Revit's Undo once per transaction + once for the
// highlight tint. Revit accepts ONE posted command per API call, so any batch
// needing 2+ posts threw "a command has already been posted" — the J-02 / A-06
// failure — while Revit's own Undo still worked.
//
// Now the runner sends a mutate batch as ONE pump job (McpJob.Children). The
// drainer opens a TransactionGroup around the children (tools + receipt
// epilogue incl. tint) and assimilates it, so the batch is ONE undo entry and
// [Undo] posts Revit's Undo exactly once. ReceiptUndoState tracks whether that
// entry is still on top; the pane hides the chip and says why when it is not.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BinaVibe.Mcp;

namespace RevitWebAppSync.Services
{
    public static class ReceiptUndo
    {
        private static readonly ReceiptUndoState _state = new ReceiptUndoState();
        private static Document _doc;   // document that holds our undo entry

        /// <summary>Raised (on the Revit UI thread) when Undo availability may
        /// have changed — the pane re-checks its chips.</summary>
        public static event EventHandler Changed;

        /// <summary>One line why the pane must not offer Undo, or null.</summary>
        public static string WhyNot(string receiptId) => _state.WhyNot(receiptId);

        /// <summary>True while the latest batch's group is still on top of the
        /// undo list — anything that commits now would close the pane Undo.</summary>
        public static bool IsLive => _state.Status == UndoStatus.Live;

        /// <summary>The batch ran without a group (several undo entries).</summary>
        public static void MarkUngrouped(string receiptId)
        {
            if (string.IsNullOrEmpty(receiptId)) return;
            _state.MarkUnavailable(receiptId);
            Notify();
        }

        private static void Notify()
        {
            try { Changed?.Invoke(null, EventArgs.Empty); } catch { /* UI refresh is best-effort */ }
        }

        // ─── subscriptions (wired from TurnReceiptService.EnsureSubscribed) ───

        internal static void Subscribe(UIApplication app)
        {
            app.Application.DocumentChanged += OnDocumentChanged;
            app.Application.DocumentClosing += OnDocumentClosing;
            app.ViewActivated += OnViewActivated;
        }

        // READ-ONLY handler — only updates state.
        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = _doc;
                if (doc == null || !IsSame(doc, e.GetDocument())) return;
                var before = _state.Status;
                switch (e.Operation)
                {
                    case UndoOperation.TransactionCommitted: _state.OnCommitted(); break;
                    case UndoOperation.TransactionUndone: _state.OnUndone(); break;
                    case UndoOperation.TransactionRedone: _state.OnRedone(); break;
                    default: return;   // rolled back = nothing entered the undo list
                }
                if (_state.Status == UndoStatus.Undone) TurnReceiptService.ForgetTint();
                if (_state.Status != before) Notify();
            }
            catch { /* recorder must never break a commit */ }
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            try
            {
                if (_doc == null || !IsSame(_doc, e.Document)) return;
                _state.OnDocumentClosed();
                _doc = null;
                Notify();
            }
            catch { }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                if (_doc == null) return;
                bool other = !IsSame(_doc, e.Document);
                if (other == _state.OtherDocumentActive) return;
                _state.SetOtherDocumentActive(other);
                Notify();
            }
            catch { }
        }

        private static bool IsSame(Document a, Document b)
        {
            try { return a != null && b != null && a.IsValidObject && b.IsValidObject && a.Equals(b); }
            catch { return false; }
        }

        // ─── one batch = one TransactionGroup (driven by McpExternalEventHandler) ───

        /// <summary>Open the batch's group before its first child runs. Null
        /// when there is no editable document (children still run).</summary>
        public static GroupScope OpenGroup(UIApplication app, McpJob parent)
        {
            var scope = new GroupScope { Parent = parent };
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || doc.IsReadOnly || doc.IsModifiable) return scope;
                var tg = new TransactionGroup(doc, "BinaVibe: perubahan Copilot");
                if (tg.Start() != TransactionStatus.Started) { tg.Dispose(); return scope; }
                scope.Doc = doc;
                scope.Group = tg;
            }
            catch { /* no group = old multi-entry behaviour; Undo will be refused */ }
            return scope;
        }

        public sealed class GroupScope
        {
            internal McpJob Parent;
            internal Document Doc;
            internal TransactionGroup Group;
            private bool _closed;

            /// <summary>Merge everything the children committed into one undo
            /// entry, then arm [Undo] for the batch's receipt. Never rolls back
            /// — the drafter approved this change.</summary>
            public void Close()
            {
                if (_closed) return;
                _closed = true;
                var receipt = ReceiptOf(Parent);
                string receiptId = receipt != null && receipt.TryGetValue("receipt_id", out var rid) ? rid as string : null;
                bool oneEntry = false;
                string name = "";
                try
                {
                    if (Group != null && Group.GetStatus() == TransactionStatus.Started)
                    {
                        name = ReceiptUndoState.GroupName(TxNamesOf(receipt));
                        try { Group.SetName(name); } catch { }
                        try { oneEntry = Group.Assimilate() == TransactionStatus.Committed; }
                        catch { try { Group.Commit(); } catch { } }
                    }
                }
                finally { try { Group?.Dispose(); } catch { } }

                // A batch that changed nothing shows no receipt card — leave
                // the previous one's state alone.
                if (receiptId != null && HasChanges(receipt))
                {
                    if (oneEntry) { _doc = Doc; _state.Arm(receiptId); }
                    else _state.MarkUnavailable(receiptId);
                    Notify();
                }
                Parent?.SetResult(new Dictionary<string, object> { ["ok"] = true, ["undo_group"] = name, ["one_undo_entry"] = oneEntry });
            }
        }

        private static Dictionary<string, object> ReceiptOf(McpJob parent)
        {
            var kids = parent?.Children;
            if (kids == null) return null;
            var last = kids.LastOrDefault(k => k.Tool == "__turn_receipt");
            return last?.Result as Dictionary<string, object>;
        }

        private static bool HasChanges(Dictionary<string, object> receipt)
        {
            int Count(string k) => receipt.TryGetValue(k, out var v) && v is int i ? i : 0;
            return Count("added") + Count("modified") + Count("deleted") > 0;
        }

        private static IReadOnlyList<string> TxNamesOf(Dictionary<string, object> receipt)
        {
            if (receipt != null && receipt.TryGetValue("transactions", out var t) && t is IEnumerable<string> names)
                return names.ToList();
            return Array.Empty<string>();
        }

        // ─── the [Undo] chip ───

        /// <summary>Post Revit's Undo ONCE for the batch's group, only while
        /// that group is still the top of the undo list.</summary>
        public static Dictionary<string, object> Undo(UIApplication app, JsonElement args)
        {
            string receiptId = "";
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("receipt_id", out var r) && r.ValueKind == JsonValueKind.String)
                receiptId = r.GetString() ?? "";

            var active = app.ActiveUIDocument?.Document;
            if (_doc != null && !_doc.IsValidObject) { _state.OnDocumentClosed(); _doc = null; Notify(); }
            else if (_doc != null) _state.SetOtherDocumentActive(!IsSame(_doc, active));

            var why = _state.WhyNot(receiptId);
            if (why != null) { Notify(); return Refuse(why); }

            var undoId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
            if (undoId == null || !app.CanPostCommand(undoId))
                return Refuse("Revit tidak membenarkan Undo sekarang — tutup sebarang dialog atau arahan yang sedang berjalan, kemudian cuba lagi.");
            try { app.PostCommand(undoId); }
            catch (Exception ex) { return Refuse("Undo tidak dapat diposkan ke Revit (" + ex.Message + ")."); }

            // The tint and badges go with the undo.
            TurnReceiptService.ForgetTint();
            try { if (active != null) TurnReceiptService.ClearBadges(active); } catch { }
            _state.OnPosted();
            Notify();
            return new Dictionary<string, object> { ["ok"] = true, ["undo_steps_posted"] = 1 };
        }

        private static Dictionary<string, object> Refuse(string why) =>
            new Dictionary<string, object> { ["ok"] = false, ["error"] = why };
    }
}
