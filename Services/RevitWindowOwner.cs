using System;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Parents a WPF dialog to Revit's main window.
    ///
    /// A modal ShowDialog() with no owner can open BEHIND the Revit window. It is
    /// still modal, so Revit ignores every click while it waits — which looks
    /// exactly like Revit has frozen, with no visible dialog to answer. Setting
    /// the owner keeps the dialog in front, minimises with Revit, and closes with
    /// it rather than being orphaned.
    /// </summary>
    public static class RevitWindowOwner
    {
        /// <summary>Must be called before ShowDialog().</summary>
        public static void SetOwner(Window window, UIApplication uiApp)
        {
            if (window == null || uiApp == null) return;
            try
            {
                new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
            }
            catch
            {
                // Older hosts may not expose the handle; an unowned dialog is worse
                // than no dialog only if it hides, so this stays best-effort.
            }
        }
    }
}
