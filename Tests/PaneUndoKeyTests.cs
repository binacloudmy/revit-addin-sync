using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-441: after a fix from the pane, the pane holds the OS keyboard focus and Revit
    /// never sees Ctrl+Z. The hook forwards Ctrl+Z / Ctrl+Y to Revit's Undo / Redo, but only
    /// when the pane (not a text field, not a dialog, not Revit's canvas) has the focus.
    /// </summary>
    public class PaneUndoKeyTests
    {
        private const int VkZ = 0x5A, VkY = 0x59, VkA = 0x41;

        private static PaneUndoAction Decide(int vk, bool ctrl = true, bool shift = false, bool alt = false,
            bool typing = false, bool paneHasFocus = true) =>
            PaneUndoKey.Decide(vk, ctrl, shift, alt, typing, paneHasFocus);

        [Fact] public void Ctrl_Z_in_the_pane_undoes_in_Revit() => Assert.Equal(PaneUndoAction.Undo, Decide(VkZ));
        [Fact] public void Ctrl_Y_in_the_pane_redoes_in_Revit() => Assert.Equal(PaneUndoAction.Redo, Decide(VkY));
        [Fact] public void Ctrl_Shift_Z_in_the_pane_redoes_in_Revit() => Assert.Equal(PaneUndoAction.Redo, Decide(VkZ, shift: true));
        [Fact] public void Ctrl_Shift_Y_is_not_ours() => Assert.Equal(PaneUndoAction.None, Decide(VkY, shift: true));

        [Fact] public void A_text_field_keeps_its_own_undo() => Assert.Equal(PaneUndoAction.None, Decide(VkZ, typing: true));
        [Fact] public void A_text_field_keeps_its_own_redo() => Assert.Equal(PaneUndoAction.None, Decide(VkY, typing: true));
        [Fact] public void Revit_canvas_focus_is_left_to_Revit() => Assert.Equal(PaneUndoAction.None, Decide(VkZ, paneHasFocus: false));
        [Fact] public void Plain_Z_is_not_ours() => Assert.Equal(PaneUndoAction.None, Decide(VkZ, ctrl: false));
        [Fact] public void Alt_chords_are_not_ours() => Assert.Equal(PaneUndoAction.None, Decide(VkZ, alt: true));
        [Fact] public void Other_ctrl_keys_are_not_ours() => Assert.Equal(PaneUndoAction.None, Decide(VkA));
    }
}
