namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    public enum PaneUndoAction { None, Undo, Redo }

    /// <summary>
    /// Ctrl+Z / Ctrl+Y typed while the pane holds the OS keyboard focus never reach Revit
    /// (AI-441: after a fix the pane keeps the focus, so Undo only worked from the toolbar).
    /// The pane's keyboard hook asks this whether to forward the chord to Revit's own
    /// Undo / Redo. Text fields keep their own undo; keys with the focus on Revit's canvas
    /// are already Revit's, so they are never forwarded (that would undo twice).
    /// </summary>
    public static class PaneUndoKey
    {
        private const int VkZ = 0x5A;
        private const int VkY = 0x59;

        public static PaneUndoAction Decide(int vk, bool ctrl, bool shift, bool alt, bool typing, bool paneHasFocus)
        {
            if (!ctrl || alt || typing || !paneHasFocus) return PaneUndoAction.None;
            if (vk == VkZ) return shift ? PaneUndoAction.Redo : PaneUndoAction.Undo;
            if (vk == VkY && !shift) return PaneUndoAction.Redo;
            return PaneUndoAction.None;
        }
    }
}
