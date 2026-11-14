#nullable enable
// Revit shows a selection made through the API from a pane (its highlight, Properties, the count, the Modify tab)
// only once the view has been activated. A click in the canvas does that; a model that was opened and then used only
// through the Copilot's pane has no activated view, and a selection made there is held by Revit ("Revit holds 38")
// and shown nowhere until the drafter clicks in the canvas (live round 15, 14 November: three probe runs; where and
// when the selection is made does not matter, and giving the view the keyboard focus is not enough).
//
// What does it: telling the view's window that a mouse click is about to activate it (WM_MOUSEACTIVATE with the hit
// test "client" and the left button, as Windows sends before a real click). No click happens. An MFC view answers by
// making itself its frame's active view, and takes the keyboard.
//
// The keyboard is the danger. In Revit letters start commands, Delete deletes the selection, Space turns it and the
// arrow keys move it. Handing the keyboard straight back is not enough: in the first live run (G-A) the log said
// "keyboard handed back" and half a second later it was elsewhere; the letters typed next did not reach the pane. So:
//   - never while the drafter may be typing (the end of a turn, a find during a turn): when the keyboard is somewhere
//     other than the view then, the selection is made and nothing is activated (it shows once the view has been
//     clicked, by the drafter or by a click on the card);
//   - after a click on the card (the hands are on the mouse): activate, hand the keyboard back, and watch it for two
//     seconds, taking it back from the view each time unless the drafter pressed a mouse button themselves.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Autodesk.Revit.UI;

namespace BinaVibe.Copilot.Writes
{
    public static class ViewActivation
    {
        private const uint WmMouseActivate = 0x0021;
        private const int HtClient = 1;
        private const int WmLButtonDown = 0x0201;

        /// <summary>How long after an activation the keyboard is watched and taken back from the view.</summary>
        public const double WatchSeconds = 2.0;
        /// <summary>How often it is looked at in that time.</summary>
        public const int WatchEveryMs = 40;

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint p);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);

        private static IntPtr _view, _had;
        private static long _at;
        private static int _taken;
        private static DispatcherTimer? _watch;

        /// <summary>Call on Revit's thread right after <c>Selection.SetElementIds</c>: has Revit show the selection, and
        /// leaves the keyboard where it was. <paramref name="mayBeTyping"/>: the selection was not asked for by a click
        /// just now (a turn's end, a find), so the view is never given the keyboard. Returns what happened, for the log.</summary>
        public static string Show(UIApplication app, bool mayBeTyping)
        {
            try
            {
                var view = ViewWindow(app);
                if (view == IntPtr.Zero) return "no view window found";
                var had = GetFocus();
                if (had == view) return "the view has the keyboard";
                if (mayBeTyping && had != IntPtr.Zero) return "not activated: the keyboard is in " + Name(had) + " and the drafter may be typing";
                var message = (IntPtr)((WmLButtonDown << 16) | HtClient);
                var answer = SendMessage(view, WmMouseActivate, app.MainWindowHandle, message).ToInt64();
                if (had == IntPtr.Zero) return "view activated (answer " + answer + "), Revit did not have the keyboard";
                // Back as a click in the pane would bring it: the pane's host is told the same thing, then the focus moves.
                var host = GetParent(had);
                if (host != IntPtr.Zero && host != app.MainWindowHandle) SendMessage(host, WmMouseActivate, app.MainWindowHandle, message);
                SetFocus(had);
                Watch(view, had);
                return "view activated (answer " + answer + "), keyboard " + (GetFocus() == had ? "handed back to " : "NOT handed back to ") + Name(had);
            }
            catch (Exception ex) { return "view activation failed: " + ex.Message; }
        }

        /// <summary>For two seconds: when the view holds the keyboard and the drafter pressed no mouse button since, it goes
        /// back to the window that had it. One line in the clicks log at the end says how often that was needed.</summary>
        private static void Watch(IntPtr view, IntPtr had)
        {
            _view = view; _had = had; _at = Stopwatch.GetTimestamp(); _taken = 0;
            GetAsyncKeyState(0x01); GetAsyncKeyState(0x02);   // forget presses from before (the click that asked for this)
            if (_watch == null)
            {
                _watch = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(WatchEveryMs) };
                _watch.Tick += (_, __) => Look();
            }
            _watch.Stop(); _watch.Start();
        }

        private static void Look()
        {
            try
            {
                var over = (Stopwatch.GetTimestamp() - _at) / (double)Stopwatch.Frequency > WatchSeconds;
                var clicked = (GetAsyncKeyState(0x01) & 1) != 0 || (GetAsyncKeyState(0x02) & 1) != 0;   // the drafter's own click: theirs to keep
                var now = GetFocus();
                if (!over && !clicked && now == _view && _had != IntPtr.Zero) { SetFocus(_had); _taken++; return; }
                if (!over && !clicked) return;
                _watch?.Stop();
                RevitWebAppSync.UI.Copilot.Model.ClickTrace.Sink("view activation, watched " + (clicked ? "until the drafter clicked" : WatchSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s")
                    + ": the view took the keyboard " + _taken + " time(s), taken back each time; it is now in " + Name(now));
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] view activation watch: " + ex.Message); }
        }

        /// <summary>A window for the log: "the view", "the window that had it", or its class name.</summary>
        private static string Name(IntPtr h)
        {
            if (h == IntPtr.Zero) return "no window";
            if (h == _view) return "the view";
            var text = new StringBuilder(80);
            try { GetClassName(h, text, text.Capacity); } catch { /* a name for the log only */ }
            return (h == _had ? "the window that had it" : "another window") + " (" + text + ")";
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
