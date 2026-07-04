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

            // Sheets
            Shot(dir, "copilot-rate-sheet.png", dark: true, configure: p => { p.ViewModel.RequestRate(); return 450; });
            Shot(dir, "copilot-upgrade-sheet.png", dark: false, configure: p => { p.ShowUpgradeSheet(); return 500; });

            // Thread: user msg + AI answer + Proposed command card
            Shot(dir, "copilot-thread.png", dark: false, configure: p => { SeedThread(p, applied: false); return 500; });
            // Applied command card (+ rating nudge)
            Shot(dir, "copilot-applied.png", dark: false, configure: p => { SeedThread(p, applied: true); return 500; });

            // Footer meter ramp
            Shot(dir, "copilot-meter-22.png", dark: false, configure: p => SetUsage(p, 22));
            Shot(dir, "copilot-meter-88.png", dark: false, configure: p => SetUsage(p, 88));
            Shot(dir, "copilot-meter-97.png", dark: false, configure: p => SetUsage(p, 97));

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
        private static int SetUsage(CopilotPanel panel, int pct, bool atLimit = false, bool isAdmin = true)
        {
            panel.ViewModel.UsageService = new StubUsageService("Free", pct, atLimit, isAdmin);
            _ = panel.ViewModel.RefreshUsageAsync();
            return 400;
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
