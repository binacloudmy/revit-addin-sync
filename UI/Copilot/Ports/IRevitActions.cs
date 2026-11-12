using System;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Ports
{
    /// <summary>
    /// Everything the pane asks of Revit: undo/redo of a turn, and the in-model
    /// rings and selection. The view model holds this port instead of calling
    /// UndoService / InModelResult directly, so it can be driven with a fake.
    /// Implementations are responsible for reaching the Revit API context.
    /// </summary>
    public interface IRevitActions
    {
        /// <summary>The undo stack changed (our button or Revit's own Undo/Redo). Raised on any thread.</summary>
        event Action<BinaVibe.Copilot.Undo.UndoChange> UndoChanged;

        bool CanUndoTurn(string turnId);
        /// <summary>Why the turn's Undo is off, as W3's kind (AI-298); None when it is available.</summary>
        BinaVibe.Copilot.Undo.UndoBlock WhyNotUndo(string turnId);
        int LiveBatches(string turnId);
        bool CanUndoLastBatch(string turnId);
        bool CanRedoTurn(string turnId);

        /// <summary>Undo the turn (or its last batch); returns the envelope summary, or null.</summary>
        Task<string> UndoTurnAsync(string turnId, bool lastBatchOnly);
        Task<string> RedoTurnAsync(string turnId);

        /// <summary>Draw a result's rings in the model. False when the pane has no overlay.</summary>
        void ShowRings(Highlights.HighlightSet set, ReceiptLanguage lang, Action undo);
        void ClearRings();
        /// <summary>Reset the active view's Temporary Hide/Isolate. Returns null when it was reset, "none" when the view was not isolated, else why not.</summary>
        Task<string> ResetIsolateAsync();
        /// <summary>Open a sheet or a view in Revit from its element id (the host document's). The id must be, in the model that is open
        /// now and is the one the result came from (<paramref name="docKey"/>), a sheet (<paramref name="sheet"/>) or a view. Null when it
        /// was asked for, else why not.</summary>
        Task<string> OpenViewAsync(long id, string docKey, bool sheet);
        /// <summary>What each id is in the open model (only when it is the model the result came from); an empty map when Revit cannot say.</summary>
        Task<System.Collections.Generic.IReadOnlyDictionary<long, ItemClass>> ClassifyAsync(System.Collections.Generic.IReadOnlyCollection<long> ids, string docKey);
        void SelectGroup(TypeGroup g);
        void ZoomGroup(TypeGroup g);
        void OpenTypeProperties(TypeGroup g);
    }
}
