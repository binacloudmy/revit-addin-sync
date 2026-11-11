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
        void ShowRings(Highlights.HighlightSet set, ReceiptLanguage lang);
        void ClearRings();
        void SelectGroup(TypeGroup g);
        void ZoomGroup(TypeGroup g);
        void OpenTypeProperties(TypeGroup g);
    }
}
