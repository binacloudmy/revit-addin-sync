#nullable enable
// Revit shows a selection made through the API from a pane (its highlight, Properties, the count, the Modify tab)
// only when its view is the active one at the moment Revit next goes idle. Live rounds 15 and 16, 14 November:
//   - a model used only through the pane since it was opened: the selection is held ("Revit holds 38") and shown
//     nowhere, wherever and whenever it is made, with a refresh, with the focus set on the view's window (PROBE2);
//   - telling the view's window that a mouse click is activating it (WM_MOUSEACTIVATE, hit test "client", left
//     button; no click happens) makes an MFC view its frame's active view, and it takes the keyboard: the selection
//     shows (PROBE3, the first G-A);
//   - the same with the keyboard given straight back to the pane in the same call: nothing shows (the second G-C);
//   - a click in the canvas before a turn that writes does not help either (G-A2).
//
// The keyboard is the danger. In Revit letters start commands, Delete deletes the selection, Space turns it and the
// arrow keys move it, and the drafter types in the pane. So the view keeps the keyboard only for Revit's next few
// Idling calls (at most a second), every key pressed in that time is dropped before any window sees it, and then
// the keyboard goes back to the window that had it and is watched for two more seconds.

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace BinaVibe.Copilot.Writes
{
    public static class ViewActivation
    {
        private const uint WmMouseActivate = 0x0021;
        private const int HtClient = 1;
        private const int WmLButtonDown = 0x0201;
        private const int WhKeyboard = 2;

        /// <summary>The view never keeps the keyboard longer than this, whether Revit went idle or not.</summary>
        public const double HoldAtMostSeconds = 1.0;
        /// <summary>How many Idling calls the view keeps it for. The first may be the call this one runs in; two more are
        /// whole idle turns of Revit's, in which it shows the selection.</summary>
        public const int HoldIdleCalls = 3;
        /// <summary>How long after the hand-back the keyboard is watched and taken back from the view.</summary>
        public const double WatchSeconds = 2.0;
        public const int WatchEveryMs = 40;

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint { public int X; public int Y; }

        private delegate IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(ScreenPoint p);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int hook, KeyHook proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        // For a live test of the key drop only (BINA_VIEW_HOLD_TEST_SECONDS=3): the view keeps the keyboard that long,
        // idle or not, so a tester's key is sure to fall inside the hold. Unset in every real session.
        private static readonly double HoldTest = double.TryParse(Environment.GetEnvironmentVariable("BINA_VIEW_HOLD_TEST_SECONDS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0 && t <= 10 ? t : 0;

        /// <summary>The same test hold, switchable while Revit runs (#3 of the handover list: the key drop was never seen to act, and a live proof
        /// needs a hold a tester can type inside without restarting Revit): a file <c>%LOCALAPPDATA%\Bina\RevitSync\view-hold-test.txt</c> holding
        /// a number of seconds (0 to 10). It is read at every activation; delete the file and the real hold is back. A FORGOTTEN file must not make
        /// every selection of a real session drop keys: the file counts only while it is younger than <see cref="HoldTestFileMinutes"/> minutes
        /// (write it again to renew it).</summary>
        internal static double HoldTestNow()
        {
            if (HoldTest > 0) return HoldTest;
            try
            {
                var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "view-hold-test.txt");
                if (!System.IO.File.Exists(path)) return 0;
                if (!TestFileIsFresh(System.IO.File.GetLastWriteTimeUtc(path), DateTime.UtcNow)) return 0;
                return ParseHoldTest(System.IO.File.ReadAllText(path));
            }
            catch (Exception) { return 0; }   // a test aid must never break a selection
        }

        /// <summary>How long a test file counts after it was written.</summary>
        public const int HoldTestFileMinutes = 30;

        /// <summary>True while the test file is no older than <see cref="HoldTestFileMinutes"/> minutes (a write time in the future counts as fresh).</summary>
        internal static bool TestFileIsFresh(DateTime writtenUtc, DateTime nowUtc) => nowUtc - writtenUtc <= TimeSpan.FromMinutes(HoldTestFileMinutes);

        /// <summary>Seconds from the test file's text: 0 for anything that is not a number above 0 and at most 10.</summary>
        internal static double ParseHoldTest(string? text) =>
            double.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 && s <= 10 ? s : 0;

        private static double _holdTest;   // the test hold of the activation in progress (0: the real hold)

        private static IntPtr _view, _had, _hook;
        private static KeyHook? _proc;      // kept alive while the hook is set
        private static long _at;
        private static int _idles, _dropped, _taken;
        private static bool _holding;
        private static DispatcherTimer? _timer;

        /// <summary>Call on Revit's thread right after <c>Selection.SetElementIds</c>: has Revit show the selection and
        /// brings the keyboard back to where it was. Returns what was started, for the clicks log; a second line
        /// follows in the log when the keyboard is back.</summary>
        public static string Show(UIApplication app)
        {
            try
            {
                if (_holding) return "the view is being activated already";
                var view = ViewWindow(app);
                if (view == IntPtr.Zero) return "no view window found";
                var had = GetFocus();
                if (had == view) return "the view has the keyboard";
                _view = view; _had = had; _holdTest = HoldTestNow();
                if (had != IntPtr.Zero) DropKeys(true);   // before the view can get one
                var answer = SendMessage(view, WmMouseActivate, app.MainWindowHandle, (IntPtr)((WmLButtonDown << 16) | HtClient)).ToInt64();
                if (had == IntPtr.Zero) return "view activated (answer " + answer + "), Revit did not have the keyboard";
                _holding = true; _idles = 0; _taken = 0; _at = Stopwatch.GetTimestamp();
                RevitThread.Idle += OnIdle;
                Tick(true);
                RevitThread.Wake();   // an Idling call soon, also with no mouse moving
                return "view activated (answer " + answer + "), the view keeps the keyboard until Revit is idle, keys dropped meanwhile";
            }
            catch (Exception ex) { Back("failed: " + DialogRedaction.RedactPaths(ex.Message)); return "view activation failed: " + DialogRedaction.RedactPaths(ex.Message); }
        }

        private static double Elapsed => (Stopwatch.GetTimestamp() - _at) / (double)Stopwatch.Frequency;

        private static void OnIdle(UIApplication app, IdlingEventArgs e)
        {
            if (!_holding) { RevitThread.Idle -= OnIdle; return; }
            if (_holdTest > 0) return;   // the timer ends the test's hold
            if (++_idles < HoldIdleCalls) { try { e.SetRaiseWithoutDelay(); } catch { /* the timer ends it */ } return; }
            Back("after " + _idles + " Idling calls");
        }

        /// <summary>The keyboard goes back to the window that had it; keys reach windows again; the watch starts.</summary>
        private static void Back(string why)
        {
            try
            {
                RevitThread.Idle -= OnIdle;
                var was = _holding; _holding = false;
                if (was && _had != IntPtr.Zero) SetFocus(_had);
                DropKeys(false);
                if (!was) return;
                RevitWebAppSync.UI.Copilot.Model.ClickTrace.Sink("view activation: keyboard back in " + Name(GetFocus()) + " " + why + ", "
                    + (Elapsed * 1000).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms, " + _dropped + " key press(es) dropped"
                    + (_holdTest > 0 ? " (test hold " + _holdTest.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s)" : ""));
                _at = Stopwatch.GetTimestamp();
                GetAsyncKeyState(0x01); GetAsyncKeyState(0x02);   // forget presses from before
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] view activation back: " + ex.Message); }
        }

        /// <summary>One timer for both phases: it ends the hold when Revit did not go idle in time, then for two seconds
        /// takes the keyboard back from the view whenever the view holds it, unless the drafter pressed a mouse button.</summary>
        private static void Tick(bool start)
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(WatchEveryMs) };
                _timer.Tick += (_, __) => Look();
            }
            _timer.Stop();
            if (start) _timer.Start();
        }

        private static void Look()
        {
            try
            {
                if (_holding)
                {
                    var most = _holdTest > 0 ? _holdTest : HoldAtMostSeconds;
                    if (Elapsed > most) Back(_holdTest > 0 ? "after the test's " + most.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s"
                                                          : "after " + most.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s without Revit going idle");
                    return;
                }
                var over = Elapsed > WatchSeconds;
                var clicked = (GetAsyncKeyState(0x01) & 1) != 0 || (GetAsyncKeyState(0x02) & 1) != 0;   // the drafter's own click: theirs to keep
                var now = GetFocus();
                if (!over && !clicked && now == _view && _had != IntPtr.Zero) { SetFocus(_had); _taken++; return; }
                if (!over && !clicked) return;
                Tick(false);
                RevitWebAppSync.UI.Copilot.Model.ClickTrace.Sink("view activation, watched " + (clicked ? "until the drafter clicked" : WatchSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s")
                    + ": the view took the keyboard again " + _taken + " time(s); it is now in " + Name(now));
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] view activation watch: " + ex.Message); }
        }

        /// <summary>While on, every key message of this thread is dropped before a window gets it.</summary>
        private static void DropKeys(bool on)
        {
            if (on && _hook == IntPtr.Zero)
            {
                _dropped = 0;
                _proc = (code, w, l) => { if (code < 0) return CallNextHookEx(_hook, code, w, l); if (code == 0 && (((long)l >> 31) & 1) == 0) _dropped++; return (IntPtr)1; };   // key presses only, not their releases
                _hook = SetWindowsHookEx(WhKeyboard, _proc, IntPtr.Zero, GetCurrentThreadId());
            }
            else if (!on && _hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero; _proc = null;
            }
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
