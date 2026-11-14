#nullable enable
// Revit shows a selection made through the API from a pane (its highlight, Properties, the count, the Modify tab)
// only once the view has been activated. A click in the canvas does that; a model that was opened and then used only
// through the Copilot's pane has no activated view, and a selection made there is held by Revit ("Revit holds 38")
// and shown nowhere until the drafter clicks in the canvas (live round 15, 14 November: three probe runs; where and
// when the selection is made does not matter, and giving the view the keyboard focus is not enough).
//
// What does it: telling the view's window that a mouse click is about to activate it (WM_MOUSEACTIVATE with the hit
// test "client" and the left button, as Windows sends before a real click). No click happens. An MFC view answers by
// making itself its frame's active view, and takes the keyboard. The drafter may be typing in the pane, and in Revit
// letters start commands and arrow keys move the selected elements, so the keyboard is handed straight back to the
// window that had it, and once more at the next Idling call should Revit have taken it when the call returned.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BinaVibe.Copilot.Writes
{
    public static class ViewActivation
    {
        private const uint WmMouseActivate = 0x0021;
        private const int HtClient = 1;
        private const int WmLButtonDown = 0x0201;

        /// <summary>How long after an activation the keyboard is still taken back from the view at the next Idling call;
        /// later than this the drafter may have clicked in the canvas themselves.</summary>
        public const double HandBackSeconds = 0.5;

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint p);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);

        private static IntPtr _view, _had;
        private static long _at;
        private static bool _listening;

        /// <summary>Call on Revit's thread right after <c>Selection.SetElementIds</c>: has Revit show the selection, and
        /// leaves the keyboard where it was. Returns what happened, for the clicks log.</summary>
        public static string Show(UIApplication app)
        {
            try
            {
                var view = ViewWindow(app);
                if (view == IntPtr.Zero) return "no view window found";
                var had = GetFocus();
                if (had == view) return "the view has the keyboard";
                var answer = SendMessage(view, WmMouseActivate, app.MainWindowHandle, (IntPtr)((WmLButtonDown << 16) | HtClient)).ToInt64();
                if (had == IntPtr.Zero) return "view activated (answer " + answer + "), Revit did not have the keyboard";
                SetFocus(had);
                _view = view; _had = had; _at = Stopwatch.GetTimestamp();
                if (!_listening) { _listening = true; RevitThread.Idle += OnIdle; }
                return "view activated (answer " + answer + "), keyboard " + (GetFocus() == had ? "handed back" : "NOT handed back");
            }
            catch (Exception ex) { return "view activation failed: " + ex.Message; }
        }

        /// <summary>The next Idling call after an activation: Revit may have given the view the keyboard when the call
        /// that activated it returned. Taken back once, and only while it is the view that holds it.</summary>
        private static void OnIdle(UIApplication app, IdlingEventArgs e)
        {
            try
            {
                RevitThread.Idle -= OnIdle; _listening = false;
                var soon = (Stopwatch.GetTimestamp() - _at) / (double)Stopwatch.Frequency <= HandBackSeconds;
                var now = GetFocus();
                var taken = soon && now == _view && _had != IntPtr.Zero;
                if (taken) SetFocus(_had);
                RevitWebAppSync.UI.Copilot.Model.ClickTrace.Sink("view activation, next idle: "
                    + (now == _had ? "the keyboard is where it was" : taken ? "Revit had taken the keyboard, handed back again" : "the keyboard is elsewhere, left alone"));
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] view activation idle: " + ex.Message); }
        }

        /// <summary>The active view's own window: the window of Revit's under the middle of the view; zero when none is found
        /// (a view dragged out of Revit's window, another window on top).</summary>
        private static IntPtr ViewWindow(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument; var view = uidoc?.ActiveView;
            if (uidoc == null || view == null) return IntPtr.Zero;
            var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
            if (ui == null) return IntPtr.Zero;
            var r = ui.GetWindowRectangle();
            var h = WindowFromPoint(new ScreenPoint { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 });
            return h != IntPtr.Zero && IsChild(app.MainWindowHandle, h) ? h : IntPtr.Zero;
        }
    }
}
