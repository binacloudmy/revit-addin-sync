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

        /// <summary>Draw a result's marks and key in the model (nothing when the pane has no overlay). The key offers
        /// <paramref name="undo"/> while <paramref name="canUndo"/> says it can run.</summary>
        void ShowMarks(Highlights.HighlightSet set, Action undo, Func<bool> canUndo);
        /// <summary>Remove the marks, the key and the pill.</summary>
        void ClearMarks();
        /// <summary>Hide the shown result's marks and key; the pill stays.</summary>
        void HideMarks();
        /// <summary>Show a hidden result again.</summary>
        void ShowMarksAgain();
        /// <summary>What the model shows of this turn's result.</summary>
        Highlights.MarksState MarksStateOf(string turnId);
        /// <summary>Reset the active view's Temporary Hide/Isolate. Returns null when it was reset, "none" when the view was not isolated, else why not.</summary>
        Task<string> ResetIsolateAsync();
        /// <summary>Open a sheet or a view in Revit from its element id (the host document's). The id must be, in the model that is open
        /// now and is the one the result came from (<paramref name="docKey"/>), a sheet (<paramref name="sheet"/>) or a view. Null when it
        /// was asked for, else why not.</summary>
        Task<string> OpenViewAsync(long id, string docKey, bool sheet);
        /// <summary>What each id is in the open model (only when it is the model the result came from); an empty map when Revit cannot say.</summary>
        Task<System.Collections.Generic.IReadOnlyDictionary<long, ItemClass>> ClassifyAsync(System.Collections.Generic.IReadOnlyCollection<long> ids, string docKey);
        /// <summary>Show a card row's elements in the model (open their view, zoom, bright and dim marks, a label); with
        /// <paramref name="select"/> they are also Revit's selection (result marks design, rule 7).</summary>
        void ShowGroup(Highlights.MarkGroup target, bool select);
        /// <summary>The key's Show all: every mark as usual again.</summary>
        void ShowAllMarks();
        /// <summary>The view that holds this turn's marked elements when none is in the open view (the card's "Go to view").</summary>
        (long Id, string Name)? MarksOtherView(string turnId);
        /// <summary>Where this turn's marks are, in the key's words; "" when they are not shown.</summary>
        string MarksWhere(string turnId);
        /// <summary>Open a view (the card's "Go to view"; no transaction). The marks follow there.</summary>
        void OpenMarksView(long viewId);
        void SelectGroup(TypeGroup g);
        void ZoomGroup(TypeGroup g);
        void OpenTypeProperties(TypeGroup g);
    }
}
