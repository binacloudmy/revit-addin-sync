using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Model;

namespace UiHarness
{
    /// <summary>
    /// Renders the Copilot panel to PNG files off-screen — no Revit, no visible
    /// window — so UI changes can be reviewed as images. Invoked by
    /// `UiHarness --shot &lt;dir&gt;`. A fresh panel is built per theme so the header
    /// icon matches, and the user's real theme preference is restored afterward.
    /// </summary>
    internal static class HarnessShots
    {
        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            bool userDark = CopilotPrefs.Load().Dark;   // restore this at the end

            Shot(dir, "copilot-light.png", dark: false, openRate: false);
            Shot(dir, "copilot-dark.png", dark: true, openRate: false);
            Shot(dir, "copilot-rate-sheet.png", dark: true, openRate: true);

            // Undo the persistence side-effect of SetDark so we don't silently
            // flip the user's Copilot theme just by taking screenshots.
            CopilotTheme.SetDark(userDark);
        }

        private static void Shot(string dir, string file, bool dark, bool openRate)
        {
            // Set the theme BEFORE constructing the panel so its constructor picks
            // the matching header icon (moon in light / sun in dark).
            CopilotTheme.SetDark(dark);

            var panel = new CopilotPanel();
            var frame = new Frame { Content = panel };
            var win = new Window
            {
                Width = 430, Height = 860, Content = frame,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = -4000, Top = -4000, ResizeMode = ResizeMode.NoResize,
            };
            win.Show();
            Settle(200);

            if (openRate)
            {
                panel.ViewModel.RequestRate();   // public trigger for the Rate sheet
                Settle(450);                      // let the slide-up reach rest
            }

            Save(frame, Path.Combine(dir, file));
            win.Close();
        }

        private static void Save(FrameworkElement el, string path)
        {
            el.UpdateLayout();
            int w = (int)Math.Ceiling(el.ActualWidth);
            int h = (int)Math.Ceiling(el.ActualHeight);
            if (w <= 0 || h <= 0) { w = 430; h = 860; }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        // Pump the dispatcher for `ms` so layout + animations advance before the
        // next capture (a plain Sleep would freeze the render thread).
        private static void Settle(int ms)
        {
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
