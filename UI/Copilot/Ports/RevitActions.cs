using System;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Undo;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Ports
{
    /// <summary>Production IRevitActions: UndoService for undo/redo, InModelResult for rings
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

        public void ShowRings(HighlightSet set, ReceiptLanguage lang, Action undo) => _inModel()?.Show(set, lang, undo);
        public void ClearRings() => _inModel()?.Clear();
        public Task<string> ResetIsolateAsync()
        {
            return BinaVibe.Copilot.Writes.RevitThread.RunAsync<string>(app =>
            {
                try
                {
                    var view = app?.ActiveUIDocument?.ActiveView;
                    if (view == null) return "no active view";
                    if (!view.IsInTemporaryViewMode(Autodesk.Revit.DB.TemporaryViewMode.TemporaryHideIsolate)) return "none";
                    // A view setting, not a model change: no transaction, so no entry in Revit's undo list.
                    view.DisableTemporaryViewMode(Autodesk.Revit.DB.TemporaryViewMode.TemporaryHideIsolate);
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            }, CancellationToken.None);
        }

        public void SelectGroup(TypeGroup g) => _inModel()?.SelectGroup(g);
        public void ZoomGroup(TypeGroup g) => _inModel()?.ZoomGroup(g);
        public void OpenTypeProperties(TypeGroup g) => _inModel()?.OpenTypeProperties(g);
    }
}
