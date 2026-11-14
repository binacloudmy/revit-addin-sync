using System;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Undo;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Ports
{
    /// <summary>Production IRevitActions: UndoService for undo/redo, InModelResult for the marks
    /// and selection. Both already marshal onto the Revit API thread themselves.</summary>
    public sealed class RevitActions : IRevitActions
    {
        private readonly Func<InModelResult> _inModel;
        public RevitActions(Func<InModelResult> inModel) { _inModel = inModel; }

        public event Action<UndoChange> UndoChanged
        {
            add { UndoService.Changed += value; }
            remove { UndoService.Changed -= value; }
        }

        public bool CanUndoTurn(string turnId) => UndoService.CanUndoTurn(turnId);
        public UndoBlock WhyNotUndo(string turnId) => UndoService.WhyNotUndoKind(turnId);
        public int LiveBatches(string turnId) => UndoService.LiveBatches(turnId);
        public bool CanUndoLastBatch(string turnId) => UndoService.CanUndoLastBatch(turnId);
        public bool CanRedoTurn(string turnId) => UndoService.CanRedoTurn(turnId);

        public async Task<string> UndoTurnAsync(string turnId, bool lastBatchOnly)
            => (await UndoService.UndoTurnAsync(turnId, CancellationToken.None, lastBatchOnly))?.Summary;

        public async Task<string> RedoTurnAsync(string turnId)
            => (await UndoService.RedoTurnAsync(turnId, CancellationToken.None))?.Summary;

        public void ShowMarks(HighlightSet set, Action undo, Func<bool> canUndo) => _inModel()?.Show(set, undo, canUndo);
        public void ClearMarks() => _inModel()?.Clear();
        public void HideMarks() => _inModel()?.Hide();
        public void ShowMarksAgain() => _inModel()?.ShowAgain();
        public MarksState MarksStateOf(string turnId) => _inModel()?.StateOf(turnId) ?? MarksState.None;
        public Task<string> ResetIsolateAsync()
        {
            return BinaVibe.Copilot.Writes.RevitThread.RunAsync<string>(app =>
            {
                try
                {
                    var view = app?.ActiveUIDocument?.ActiveView;
                    if (view == null) return "no active view";
                    if (!view.IsInTemporaryViewMode(Autodesk.Revit.DB.TemporaryViewMode.TemporaryHideIsolate)) return "none";
                    // Revit wants a transaction around it ("Cannot modify the model outside of a transaction"), on its own thread.
                    // If Revit records it in the Undo list, the pane sees a commit it did not make, and the card says Undo is off
                    // because the model changed after the turn: that is true, and Revit's own Undo list is still the way back.
                    using (var tx = new Autodesk.Revit.DB.Transaction(view.Document, "Reset isolate"))
                    {
                        tx.Start();
                        try
                        {
                            view.DisableTemporaryViewMode(Autodesk.Revit.DB.TemporaryViewMode.TemporaryHideIsolate);
                            tx.Commit();
                        }
                        catch (Exception)
                        {
                            if (tx.GetStatus() == Autodesk.Revit.DB.TransactionStatus.Started) tx.RollBack();
                            throw;
                        }
                    }
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }, CancellationToken.None);
        }

        public Task<string> OpenViewAsync(long id, string docKey, bool sheet)
        {
            return BinaVibe.Copilot.Writes.RevitThread.RunAsync<string>(app =>
            {
                try
                {
                    var uidoc = app?.ActiveUIDocument;
                    if (uidoc == null) return "no document is open";
                    var el = uidoc.Document.GetElement(Autodesk.Revit.DB.ElemIds.From(id));
                    var view = el as Autodesk.Revit.DB.View;
                    var why = OpenGuard.Why(docKey, BinaVibe.Copilot.Read.SnapshotService.ActiveDocKey, sheet,
                        found: el != null, isSheet: el is Autodesk.Revit.DB.ViewSheet, isView: view != null, isTemplate: view != null && view.IsTemplate);
                    if (why != null) return why;
                    // Asks Revit to make it the active view once the current command ends; no transaction.
                    uidoc.RequestViewChange(view);
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }, CancellationToken.None);
        }

        public Task<System.Collections.Generic.IReadOnlyDictionary<long, ItemClass>> ClassifyAsync(System.Collections.Generic.IReadOnlyCollection<long> ids, string docKey)
        {
            return BinaVibe.Copilot.Writes.RevitThread.RunAsync<System.Collections.Generic.IReadOnlyDictionary<long, ItemClass>>(app =>
            {
                var map = new System.Collections.Generic.Dictionary<long, ItemClass>();
                try
                {
                    var doc = app?.ActiveUIDocument?.Document;
                    if (doc == null) return map;
                    // Another model is open than the one the result came from: none of its ids can be trusted here.
                    if (!string.IsNullOrEmpty(docKey) && !string.Equals(docKey, BinaVibe.Copilot.Read.SnapshotService.ActiveDocKey, StringComparison.Ordinal)) return map;
                    foreach (var id in ids)
                    {
                        var el = doc.GetElement(Autodesk.Revit.DB.ElemIds.From(id));
                        map[id] = el == null ? ItemClass.Missing
                            : el is Autodesk.Revit.DB.ViewSheet ? ItemClass.Sheet
                            : el is Autodesk.Revit.DB.View v ? (v.IsTemplate ? ItemClass.Template : ItemClass.View)
                            : ItemClass.Other;
                    }
                }
                catch (Exception) { /* a partial map: the ids not in it get no button */ }
                return map;
            }, CancellationToken.None);
        }

        public void ShowGroup(MarkGroup target, bool select) => _inModel()?.Focus(target, select);
        public void ShowAllMarks() => _inModel()?.ShowAll();
        public (long Id, string Name)? MarksOtherView(string turnId)
        {
            var m = _inModel();
            return m != null && m.StateOf(turnId) == MarksState.Shown ? m.OtherView : null;
        }
        public string MarksWhere(string turnId)
        {
            var m = _inModel();
            return m != null && m.StateOf(turnId) == MarksState.Shown ? m.WhereLine : "";
        }
        public void OpenMarksView(long viewId) => _inModel()?.OpenView(viewId);
        public void SelectGroup(TypeGroup g) => _inModel()?.SelectGroup(g);
        public void ZoomGroup(TypeGroup g) => _inModel()?.ZoomGroup(g);
        public void OpenTypeProperties(TypeGroup g) => _inModel()?.OpenTypeProperties(g);
    }
}
