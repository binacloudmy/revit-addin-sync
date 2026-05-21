using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>
    /// Controls the highlight overlay window: positions it over the active Revit view and
    /// renders markers. Best-effort projection (the prototype's %-based placement); exact
    /// XYZ→screen projection is a later refinement.
    /// </summary>
    public class HighlightOverlay
    {
        private HighlightOverlayWindow _win;
        private Action _onClear;

        public void Show(UIApplication app, IList<HighlightMarker> markers, Action onClear)
        {
            try
            {
                var uidoc = app?.ActiveUIDocument;
                var views = uidoc?.GetOpenUIViews();
                if (views == null || views.Count == 0 || markers == null || markers.Count == 0) { Hide(); return; }

                var rect = views[0].GetWindowRectangle(); // screen pixels
                double scale = DeviceScale();
                double left = rect.Left / scale;
                double top = rect.Top / scale;
                double w = (rect.Right - rect.Left) / scale;
                double h = (rect.Bottom - rect.Top) / scale;
                if (w <= 0 || h <= 0) { Hide(); return; }

                _onClear = onClear;
                if (_win == null)
                {
                    _win = new HighlightOverlayWindow();
                    _win.ClearRequested += () => _onClear?.Invoke();
                }

                _win.Left = left;
                _win.Top = top;
                _win.Width = w;
                _win.Height = h;
                _win.Render(markers, w, h);
                if (!_win.IsVisible) _win.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Highlight overlay failed: {ex.Message}");
                Hide();
            }
        }

        public void Hide()
        {
            try { _win?.Hide(); } catch { /* ignore */ }
        }

        // physical pixels → DIP divisor (dpiX / 96). 1.0 at 100% scaling.
        private static double DeviceScale()
        {
            try
            {
                var src = PresentationSource.FromVisual(Application.Current?.MainWindow);
                if (src?.CompositionTarget != null)
                {
                    Matrix m = src.CompositionTarget.TransformToDevice;
                    if (m.M11 > 0) return m.M11;
                }
            }
            catch { /* fall through */ }
            return 1.0;
        }
    }
}
