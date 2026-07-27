using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Model;
using RevitWebAppSync.UI.Copilot.Services;

namespace UiHarness
{
    /// <summary>
    /// Renders the Copilot panel to PNG files off-screen — no Revit, no visible
    /// window — so UI changes can be reviewed as images. Invoked by
    /// `UiHarness --shot &lt;dir&gt;`. A fresh panel is built per state so the header
    /// icon matches, and the user's real theme preference is restored afterward.
    /// </summary>
    internal static class HarnessShots
    {
        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            bool userDark = CopilotPrefs.Load().Dark;   // restore this at the end

            // Base states
            Shot(dir, "copilot-light.png", dark: false);
            Shot(dir, "copilot-dark.png", dark: true);
            Shot(dir, "copilot-history.png", dark: false, configure: p => { p.ViewModel.GoTab(CpTab.History); return 0; });
            Shot(dir, "copilot-library.png", dark: false, configure: p => { p.ViewModel.GoTab(CpTab.Library); return 250; });
            Shot(dir, "copilot-library-dark.png", dark: true, configure: p => { p.ViewModel.GoTab(CpTab.Library); return 250; });

            // Sheets
            Shot(dir, "copilot-rate-sheet.png", dark: true, configure: p => { p.ViewModel.RequestRate(); return 450; });
            Shot(dir, "copilot-upgrade-sheet.png", dark: false, configure: p => { p.ShowUpgradeSheet(); return 500; });

            // Thread: user msg + AI answer + Proposed command card
            Shot(dir, "copilot-thread.png", dark: false, configure: p => { SeedThread(p, applied: false); return 500; });
            // Applied command card (+ rating nudge)
            Shot(dir, "copilot-applied.png", dark: false, configure: p => { SeedThread(p, applied: true); return 500; });

            // Footer plan-name button + severity dot (no full-width meter):
            // Free 20% (no dot) · Free 88% (amber) · Free 96% (red) · Pro 30% (no dot).
            foreach (var dark in new[] { false, true })
            {
                string s = dark ? "-dark" : "";
                Shot(dir, $"copilot-usage-free-20{s}.png", dark, configure: p => SetUsage(p, 20));
                Shot(dir, $"copilot-usage-free-88{s}.png", dark, configure: p => SetUsage(p, 88));
                Shot(dir, $"copilot-usage-free-96{s}.png", dark, configure: p => SetUsage(p, 96));
                Shot(dir, $"copilot-usage-pro-30{s}.png", dark, configure: p => SetUsage(p, 30, plan: "Pro"));
                // Near-limit notice above the composer: amber + dismissible at 80-94,
                // red "Running low" + Upgrade and NO dismiss at >=95.
                Shot(dir, $"copilot-notice-warn{s}.png", dark, configure: p => SetUsage(p, 85));
                Shot(dir, $"copilot-notice-critical{s}.png", dark, configure: p => SetUsage(p, 96));
                // Uncapped wallet: no severity dot, and the popover reads "No limit"
                // with neither an Upgrade CTA nor a reset row.
                Shot(dir, $"copilot-usage-unlimited{s}.png", dark,
                    configure: p => SetUsage(p, 0, plan: "Unlimited (internal)", unlimited: true));
                // Usage popover — a WPF Popup lives in its own window, so render its
                // card visual directly rather than the panel frame. Two variants: with
                // headroom (reset row) and in the warn band (Upgrade CTA).
                PopoverShot(dir, $"copilot-usage-popover{s}.png", dark, pct: 88);
                PopoverShot(dir, $"copilot-usage-popover-reset{s}.png", dark, pct: 22);
                // Kebab menu (Rate · Report · WhatsApp · divider · Version) — also
                // a Popup, so render its card directly.
                KebabShot(dir, $"copilot-kebab{s}.png", dark);
            }

            // Usage-limit blocked states
            Shot(dir, "copilot-blocked-admin.png", dark: false,
                configure: p => SetUsage(p, 100, atLimit: true, isAdmin: true));
            Shot(dir, "copilot-blocked-member.png", dark: false,
                configure: p => SetUsage(p, 100, atLimit: true, isAdmin: false));

            // Undo the persistence side-effect of SetDark so we don't silently
            // flip the user's Copilot theme just by taking screenshots.
            CopilotTheme.SetDark(userDark);
        }

        // Inject a stub usage snapshot and refresh; returns extra settle time.
        // resetsAt defaults to a FIXED date so the popover's "Resets 1 Aug" row is
        // deterministic across runs (a moving date would churn every screenshot).
        private static int SetUsage(CopilotPanel panel, int pct, bool atLimit = false, bool isAdmin = true,
            string plan = "Free", string resetsAt = "2026-08-01", bool unlimited = false)
        {
            panel.ViewModel.UsageService = new StubUsageService(
                plan, pct, atLimit, isAdmin, resetsAt, unlimited);
            _ = panel.ViewModel.RefreshUsageAsync();
            return 400;
        }

        // Render the usage popover card. The Popup is hosted in its own top-level
        // window, so a RenderTargetBitmap of the panel frame never contains it —
        // instead open it and render its Child visual directly.
        private static void PopoverShot(string dir, string file, bool dark, int pct = 88)
        {
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
            Settle(250);
            SetUsage(panel, pct);
            Settle(400);

            var prompt = FindDescendant<RevitWebAppSync.UI.Copilot.Controls.PromptBar>(panel);
            var popup = prompt?.FindName("UsagePopup") as System.Windows.Controls.Primitives.Popup;
            if (popup != null)
            {
                popup.IsOpen = true;
                Settle(350);
                if (popup.Child is FrameworkElement card) Save(card, Path.Combine(dir, file));
                popup.IsOpen = false;
            }
            win.Close();
        }

        // Render the kebab (⋮) menu card. Like the usage popover it is a Popup in
        // its own window, so open it and render its Child visual directly.
        private static void KebabShot(string dir, string file, bool dark)
        {
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
            Settle(250);

            var popup = panel.FindName("MenuPopup") as System.Windows.Controls.Primitives.Popup;
            if (popup != null)
            {
                popup.IsOpen = true;
                Settle(350);
                if (popup.Child is FrameworkElement card) Save(card, Path.Combine(dir, file));
                popup.IsOpen = false;
            }
            win.Close();
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T hit) return hit;
                var deep = FindDescendant<T>(child);
                if (deep != null) return deep;
            }
            return null;
        }

        private static int SeedThread(CopilotPanel panel, bool applied)
        {
            var vm = panel.ViewModel;
            vm.Thread.Add(new ChatMessage
            {
                Role = "user", Kind = CpMsgKind.User, Time = "2:25 PM",
                Text = "Create exterior walls on Level 2 along grid A–F",
            });
            vm.Thread.Add(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply, Time = "2:25 PM",
                Text = "I'll create the walls. Review the proposed action below and apply when ready.",
            });
            if (applied)
            {
                vm.Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Result, ToolId = "create-walls", Time = "2:26 PM",
                    Result = new ResultModel { Kind = CpResultKind.Plain, Headline = "6 walls created on Level 2." },
                });
            }
            else
            {
                vm.Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Proposal, ToolId = "create-walls", Time = "2:25 PM",
                    PlanSteps = new List<string>
                    {
                        "Find grid lines A through F on Level 2",
                        "Create Generic — 200 mm walls along each segment",
                        "Set wall height to 3,200 mm",
                    },
                    Code = "// generated C#\nvar level = FindLevel(\"Level 2\");",
                });
            }
            return 500;
        }

        private static void Shot(string dir, string file, bool dark, Func<CopilotPanel, int> configure = null)
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

            if (configure != null)
            {
                int extra = 0;
                try { extra = configure(panel); } catch { /* state seeding is best-effort */ }
                Settle(Math.Max(200, extra));
            }

            Save(frame, Path.Combine(dir, file));
            win.Close();
        }

        private static void Save(FrameworkElement el, string path)
        {
            el.UpdateLayout();
            // RenderTargetBitmap.Render applies the element's layout offset — i.e. its
            // Margin — so a bitmap sized to ActualWidth/Height alone draws the content
            // shifted down-right into a canvas that is too small, silently cropping the
            // right and bottom edges along with any drop shadow. That made the usage
            // popover look mis-aligned in screenshots when the geometry was correct.
            // Include the margins so the capture is honest.
            var m = el.Margin;
            int w = (int)Math.Ceiling(el.ActualWidth + m.Left + m.Right);
            int h = (int)Math.Ceiling(el.ActualHeight + m.Top + m.Bottom);
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
