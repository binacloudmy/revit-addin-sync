#nullable enable
// Reads the top-level windows of Revit's UI thread from the watchdog's timer thread (r17, tester feedback 20 Nov: "an invisible popup dialog
// interrupts the copilot"). Win32 only, never on Revit's thread, never a call that sends a message to a window (InternalGetWindowText and
// GetClassName read without one; GetWindowText would block on a busy thread). Every call is guarded: anything it cannot read answers null
// ("unknown"), and the watchdog then keeps its old rule. ModalPolicy (Revit-free) picks the blocking window; BringToFront shows it.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BinaVibe.Copilot.Writes
{
    internal static class ModalWindows
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int InternalGetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        private const uint GwOwner = 4;
        private const uint FlashAll = 3, FlashTimerNoForeground = 12;

        /// <summary>The calling thread's id (call on Revit's UI thread at attach); 0 when it cannot be read.</summary>
        public static uint CurrentThread()
        {
            try { return GetCurrentThreadId(); }
            catch (Exception) { return 0; }
        }

        /// <summary>The top-level windows of <paramref name="threadId"/>; null when they cannot be read.</summary>
        public static List<ThreadWindow>? Of(uint threadId)
        {
            if (threadId == 0) return null;
            try
            {
                var found = new List<ThreadWindow>();
                EnumThreadWindows(threadId, (h, _) =>
                {
                    var title = new StringBuilder(256);
                    var cls = new StringBuilder(128);
                    InternalGetWindowText(h, title, title.Capacity);
                    GetClassName(h, cls, cls.Capacity);
                    found.Add(new ThreadWindow
                    {
                        Handle = h.ToInt64(),
                        Title = title.ToString(),
                        ClassName = cls.ToString(),
                        Visible = IsWindowVisible(h),
                        Enabled = IsWindowEnabled(h),
                        Owner = GetWindow(h, GwOwner).ToInt64(),
                    });
                    return found.Count < 200;
                }, IntPtr.Zero);
                return found;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool IsEnabled(long handle)
        {
            try { return handle == 0 || IsWindowEnabled(new IntPtr(handle)); }
            catch (Exception) { return true; }
        }

        /// <summary>Brings the blocking window forward and flashes it, so a dialog hidden behind Revit or the pane is seen. Never throws.</summary>
        public static void BringToFront(long handle)
        {
            try
            {
                var h = new IntPtr(handle);
                SetForegroundWindow(h);
                var info = new FlashInfo { Size = (uint)Marshal.SizeOf(typeof(FlashInfo)), Window = h, Flags = FlashAll | FlashTimerNoForeground, Count = 5, Timeout = 0 };
                FlashWindowEx(ref info);
            }
            catch (Exception)
            {
                // A courtesy: the revit_busy text still names it.
            }
        }
    }
}
