using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    /// <summary>
    /// Places a borderless window exactly over Revit's main window and owns it by
    /// that window, so its scrim covers the whole app (prototype: the scrim covers
    /// the app). Revit's main window is not reliably a WPF Window (Window.GetWindow
    /// on the docked pane gave nothing), so it is found by handle.
    /// </summary>
    internal static class RevitWindowCover
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        /// <summary>True when <paramref name="w"/> now covers Revit's window.
        /// <paramref name="anchor"/> (an element in the pane) gives the DPI.</summary>
        public static bool Cover(Window w, FrameworkElement anchor)
        {
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
                if (r.Right - r.Left < 300 || r.Bottom - r.Top < 300) return false;

                // Pixels → WPF units with the pane's DPI (the pane sits in Revit's window).
                var source = anchor != null ? PresentationSource.FromVisual(anchor) : null;
                var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var topLeft = toDip.Transform(new Point(r.Left, r.Top));
                var bottomRight = toDip.Transform(new Point(r.Right, r.Bottom));

                new WindowInteropHelper(w).Owner = hwnd;
                w.SizeToContent = SizeToContent.Manual;
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = topLeft.X;
                w.Top = topLeft.Y;
                w.Width = bottomRight.X - topLeft.X;
                w.Height = bottomRight.Y - topLeft.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
