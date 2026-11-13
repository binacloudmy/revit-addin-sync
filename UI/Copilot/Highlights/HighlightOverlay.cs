using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>
    /// Controls the highlight overlay window: positions it over the active Revit view
    /// and renders either the legacy marker list or a result's key (or its hidden pill).
    /// </summary>
    public class HighlightOverlay
    {
        private HighlightOverlayWindow _win;
        private Action _onClear;

        public void Show(UIApplication app, IList<HighlightMarker> markers, Action onClear)
        {
            try
            {
                var ui = ActiveUIView(app);
                if (ui == null || markers == null || markers.Count == 0) { Hide(); return; }
                if (!Place(ui, out var w, out var h)) { Hide(); return; }
                _onClear = onClear;
                Win().Render(markers, w, h);
                if (!_win.IsVisible) _win.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Highlight overlay failed: {ex.Message}");
                Hide();
            }
        }

        /// <summary>Draw a result's key over the active view, in the first corner with no mark under it. Returns false
        /// when there is no view to draw on.</summary>
        public bool ShowResult(UIApplication app, OverlayModel model)
        {
            try
            {
                var ui = ActiveUIView(app);
                if (ui == null || !Place(ui, out var w, out var h)) { Hide(); return false; }
                Win().RenderResult(model);
                if (!_win.IsVisible) _win.Show();
                _win.PlaceKey(w, h);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Result overlay failed: {ex.Message}");
                Hide();
                return false;
            }
        }


        public void ClearResult()
        {
            try { _win?.ClearResult(); _win?.Hide(); } catch { /* ignore */ }
        }

        public void Hide()
        {
            try { _win?.Hide(); } catch { /* ignore */ }
        }

        private HighlightOverlayWindow Win()
        {
            if (_win == null)
            {
                _win = new HighlightOverlayWindow();
                _win.ClearRequested += () => _onClear?.Invoke();
            }
            return _win;
        }

        private static UIView ActiveUIView(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            var views = uidoc?.GetOpenUIViews();
            if (views == null || views.Count == 0) return null;
            var active = uidoc.ActiveView?.Id;
            return views.FirstOrDefault(v => active != null && v.ViewId == active) ?? views[0];
        }

        private bool Place(UIView ui, out double w, out double h)
        {
            var rect = ui.GetWindowRectangle(); // screen pixels
            double scale = DeviceScale();
            w = (rect.Right - rect.Left) / scale;
            h = (rect.Bottom - rect.Top) / scale;
            if (w <= 0 || h <= 0) return false;
            var win = Win();
            win.Left = rect.Left / scale;
            win.Top = rect.Top / scale;
            win.Width = w;
            win.Height = h;
            return true;
        }

        /// <summary>physical pixels → DIP divisor (dpiX / 96). 1.0 at 100 % scaling.</summary>
        public static double DeviceScale()
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
