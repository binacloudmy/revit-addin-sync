using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Controls;
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

            Shot(dir, "copilot-light.png", dark: false);
            Shot(dir, "copilot-dark.png", dark: true);
            Shot(dir, "copilot-dark-answer.png", dark: true, seedChat: true);   // AI reply legible on dark
            Shot(dir, "copilot-rate-sheet.png", dark: true, action: "rate");

            // Subscription/usage states.
            Shot(dir, "copilot-usage-80.png", dark: false, usage: 88);   // amber meter + 80 note
            Shot(dir, "copilot-usage-95.png", dark: false, usage: 96);   // red meter + 95 banner
            Shot(dir, "copilot-blocked.png", dark: false, usage: 100);   // blocked composer
            Shot(dir, "copilot-plans.png", dark: false, usage: 100, action: "upgrade"); // plan carousel (Basic centered)
            Shot(dir, "copilot-plans-pro.png", dark: false, usage: 100, action: "upgrade", planIndex: 2); // Pro centered
            Shot(dir, "copilot-plans-dark.png", dark: true, usage: 100, action: "upgrade"); // cards must lift off the dark sheet
            Shot(dir, "copilot-history.png", dark: false, action: "history");  // slim scrollbar on the overflowing list
            Shot(dir, "copilot-history-dark.png", dark: true, action: "history");
            PopoverShot(dir, "copilot-popover.png", dark: false, usage: 88);  // usage popover card

            // Undo the persistence side-effect of SetDark so we don't silently
            // flip the user's Copilot theme just by taking screenshots.
            CopilotTheme.SetDark(userDark);
        }

        private static void Shot(string dir, string file, bool dark, int usage = -1, string action = null, bool seedChat = false, int planIndex = -1)
        {
            // Set the theme BEFORE constructing the panel so its constructor picks
            // the matching header icon (moon in light / sun in dark).
            CopilotTheme.SetDark(dark);

            var panel = new CopilotPanel();
            if (usage >= 0 && panel.ViewModel.Usage is MockUsageService m) m.UsagePct = usage;
            if (seedChat)
            {
                panel.ViewModel.Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = "Create exterior walls on Level 2 along grid A–F" });
                panel.ViewModel.Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = "Here's what I'll do:\n\n**Exterior walls** on Level 2, grid A → F.\n\n- Type: `Basic Wall — Exterior`\n- Height: Level 2 to Level 3\n\nSay the word and I'll generate the command.",
                });
            }

            var frame = new Frame { Content = panel };
            var win = new Window
            {
                Width = 430, Height = 860, Content = frame,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = -4000, Top = -4000, ResizeMode = ResizeMode.NoResize,
            };
            win.Show();
            Settle(200);

            if (action == "history") { panel.ViewModel.GoTab(CpTab.History); Settle(350); }
            else if (action == "rate") { panel.ViewModel.RequestRate(); Settle(450); }
            else if (action == "upgrade")
            {
                panel.ViewModel.RequestUpgrade(); Settle(500);
                if (planIndex >= 0)
                {
                    panel.GetType().GetMethod("SetPlanIndex", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.Invoke(panel, new object[] { planIndex });
                    Settle(400);   // let the carousel animate to the new centre
                }
            }

            Save(frame, Path.Combine(dir, file));
            win.Close();
        }

        // The usage popover is a WPF Popup (its own top-level window), so the normal
        // Shot() capture of the panel can't see it. Open it via reflection and render
        // just the card visual so the popover can still be verified as an image.
        private static void PopoverShot(string dir, string file, bool dark, int usage)
        {
            CopilotTheme.SetDark(dark);
            var panel = new CopilotPanel();
            if (panel.ViewModel.Usage is MockUsageService m) m.UsagePct = usage;

            var frame = new Frame { Content = panel };
            var win = new Window
            {
                Width = 430, Height = 860, Content = frame,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = -4000, Top = -4000, ResizeMode = ResizeMode.NoResize,
            };
            win.Show();
            Settle(200);

            var bar = FindVisual<PromptBar>(panel);
            var popup = bar?.GetType()
                .GetField("UsagePopup", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(bar) as Popup;
            if (popup != null)
            {
                popup.IsOpen = true;
                Settle(350);   // let the popup window lay out + menuPop settle
                if (popup.Child is FrameworkElement card)
                {
                    card.UpdateLayout();
                    Save(card, Path.Combine(dir, file));
                }
                popup.IsOpen = false;
            }
            win.Close();
        }

        private static T FindVisual<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T hit) return hit;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var found = FindVisual<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
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
