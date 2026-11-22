#nullable enable
// Instant feedback and no double clicks on the result buttons. A click whose work goes to Revit (or the clipboard) opens a
// ClickTrace op; until an op ends, the button shows a small spinner (after 200 ms, so a quick one never flickers) and a second
// click on it is ignored. A click that opens no op is a plain UI action: only a 250 ms debounce. A button never spins longer
// than 20 s.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class ClickGuard
    {
        private static readonly HashSet<Button> Busy = new HashSet<Button>();
        private static readonly object Gate = new object();
        private static int _wired;

        public static void Run(Button button, Action onClick)
        {
            Wire();
            lock (Gate) { if (Busy.Contains(button)) return; Busy.Add(button); }
            var before = ClickTrace.Opened;
            try { onClick(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] click: " + ex.Message); }
            if (ClickTrace.Opened == before)
            {
                // Nothing went to Revit: a UI action. Ignore repeats for a moment only.
                Later(button, 250, () => Release(button));
                return;
            }
            Later(button, 200, () => { lock (Gate) { if (!Busy.Contains(button)) return; } Spin(button, true); });
            Later(button, 20000, () => Release(button));   // never spin for ever
        }

        private static void Wire()
        {
            if (System.Threading.Interlocked.Exchange(ref _wired, 1) == 1) return;
            // Any op ending frees the buttons that were waiting on one (one click at a time is the normal case).
            ClickTrace.Ended += _ =>
            {
                Button[] all;
                lock (Gate) all = new List<Button>(Busy).ToArray();
                foreach (var b in all) b.Dispatcher.BeginInvoke(new Action(() => Release(b)));
            };
        }

        private static void Release(Button button)
        {
            lock (Gate) Busy.Remove(button);
            Spin(button, false);
        }

        private static void Later(Button button, int ms, Action action)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, button.Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, __) => { timer.Stop(); try { action(); } catch (Exception) { /* the button is gone */ } };
            timer.Start();
        }

        private static void Spin(Button button, bool on)
        {
            try
            {
                button.ApplyTemplate();
                if (button.Template?.FindName("spin", button) is not FrameworkElement spin) return;
                spin.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (spin.RenderTransform is System.Windows.Media.RotateTransform rot)
                {
                    if (on) rot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(800))) { RepeatBehavior = RepeatBehavior.Forever });
                    else rot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                }
                button.Cursor = on ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Hand;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] spin: " + ex.Message); }
        }
    }
}
