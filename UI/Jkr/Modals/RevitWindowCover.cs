using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    /// <summary>
    /// Places a borderless pop-up over the screen Revit is on (its work area, so
    /// the taskbar stays visible) and owns it by Revit's window: the scrim covers
    /// the app and the card sits in the centre of the screen.
    ///
    /// The final size is set in physical pixels once the window has a handle
    /// (SetWindowPos), so the result does not depend on converting pixels to WPF
    /// units with the right DPI — a wrong conversion left the confirm card off
    /// centre with no scrim. Revit's window is the root of the pane's own window
    /// (Process.MainWindowHandle is only a fallback).
    /// </summary>
    internal static class RevitWindowCover
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private const uint GaRoot = 2;
        private const uint MonitorDefaultToNearest = 2;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        /// <summary>
        /// As <paramref name="w"/> closes, activate the window that owns it (Revit, or the
        /// Borang preview for the export dialog) while <paramref name="w"/> is still the
        /// active window. Left to Windows, closing it could bring another app to the front
        /// (live R3: after the preview had owned the modal export dialog).
        /// </summary>
        public static void ReturnToOwnerOnClose(Window w)
        {
            if (w == null) return;
            w.Closing += (_, e) =>
            {
                if (e.Cancel) return;
                try
                {
                    var owner = new WindowInteropHelper(w).Owner;
                    if (owner != IntPtr.Zero && IsWindow(owner)) SetForegroundWindow(owner);
                }
                catch { /* best effort */ }
            };
        }

        /// <summary>Revit's top-level window: the root of the window holding the pane.</summary>
        public static IntPtr RevitWindow(FrameworkElement anchor)
        {
            try
            {
                if (anchor != null && PresentationSource.FromVisual(anchor) is HwndSource src && src.Handle != IntPtr.Zero)
                {
                    var root = GetAncestor(src.Handle, GaRoot);
                    if (root != IntPtr.Zero) return root;
                }
            }
            catch { }
            try { return Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// Cover the work area of the screen Revit is on. <paramref name="fitHeight"/>
        /// gets the window's height (WPF units) whenever it changes, to cap the card.
        /// False when there is no Revit window to cover.
        /// </summary>
        public static bool Cover(Window w, FrameworkElement anchor, Action<double> fitHeight = null)
        {
            try
            {
                var revit = RevitWindow(anchor);
                if (revit == IntPtr.Zero) return false;
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(MonitorFromWindow(revit, MonitorDefaultToNearest), ref info)) return false;
                var r = info.rcWork;
                if (r.Right - r.Left < 300 || r.Bottom - r.Top < 300) return false;

                new WindowInteropHelper(w).Owner = revit;
                w.SizeToContent = SizeToContent.Manual;
                w.WindowStartupLocation = WindowStartupLocation.Manual;

                // First guess in WPF units (avoids a flash at the wrong size)…
                var source = anchor != null ? PresentationSource.FromVisual(anchor) : null;
                var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var tl = toDip.Transform(new Point(r.Left, r.Top));
                var br = toDip.Transform(new Point(r.Right, r.Bottom));
                w.Left = tl.X; w.Top = tl.Y; w.Width = br.X - tl.X; w.Height = br.Y - tl.Y;

                // …then exactly, in pixels, once the window exists.
                w.SourceInitialized += (_, __) =>
                {
                    var hwnd = new WindowInteropHelper(w).Handle;
                    SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SwpNoZOrder | SwpNoActivate);
                };
                if (fitHeight != null)
                {
                    w.SizeChanged += (_, e) => fitHeight(e.NewSize.Height);
                    fitHeight(w.Height);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Own <paramref name="w"/> by Revit's window and centre it there
        /// (CenterOwner works with an owner handle); else the centre of the screen.</summary>
        public static void CenterOnRevit(Window w, FrameworkElement anchor = null)
        {
            try
            {
                var revit = RevitWindow(anchor);
                if (revit != IntPtr.Zero)
                {
                    new WindowInteropHelper(w).Owner = revit;
                    w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    return;
                }
            }
            catch { }
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
