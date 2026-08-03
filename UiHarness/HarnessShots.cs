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

            // Massing / space planning, seeded with the frozen sample payload. Two
            // extra shots for the states that are easy to regress: the plan drawn on
            // the upper storey, and the rejected list expanded.
            foreach (var dark in new[] { false, true })
            {
                string s = dark ? "-dark" : "";
                Shot(dir, $"copilot-planning{s}.png", dark, configure: p => SeedPlanning(p));
                // Backend unreachable: the suggest call must soft-fail to Home with
                // the reason in the thread — never a crash and never a dead Running
                // screen. Needs no live backend precisely because that IS the case
                // under test (harness runs with no tunnel).
                Shot(dir, $"copilot-planning-offline{s}.png", dark, configure: p =>
                {
                    _ = p.ViewModel.BeginPlanningAsync("sekolah rendah, Tahun 1–6 with 3 kelas each");
                    return 900;
                });

                // Scrolled to the plan canvas — the top of the screen is the SOA, so
                // an unscrolled shot never shows the preview at all.
                Shot(dir, $"copilot-planning-preview{s}.png", dark, configure: p =>
                {
                    SeedPlanning(p);
                    ScrollPlanningToEnd(p);
                    return 400;
                });
                Shot(dir, $"copilot-planning-preview-l2{s}.png", dark, configure: p =>
                {
                    SeedPlanning(p);
                    p.ViewModel.SelectedLevel = 2;
                    ScrollPlanningToEnd(p);
                    return 400;
                });

                // Zero-scheme state: a brief too large for the generator (SK
                // Cyberjaya scale, 84 classrooms). Real backend response — every
                // candidate rejected. The screen must explain WHY, with numbers.
                Shot(dir, $"copilot-planning-noschemes{s}.png", dark, configure: p =>
                {
                    p.ViewModel.ShowPlanningPreview(
                        MassingSample.Oversized(),
                        "sekolah rendah, Tahun 1-6 with 14 kelas each, tapak 20000 m2, setback 10 m");
                    ScrollPlanningToSchemes(p);
                    return 450;
                });

                // The "space plan ready" bar: a plan is loaded but the user has
                // navigated back to the chat (which is what asking any follow-up
                // question does). Without this bar the plan is unreachable.
                Shot(dir, $"copilot-planning-resume{s}.png", dark, configure: p =>
                {
                    SeedPlanning(p);
                    // Exactly what a follow-up question does: ChatSend resets Screen
                    // to Home, stranding the plan.
                    p.ViewModel.ChatSend("what is the bounding box of the selected element in mm?");
                    return 500;
                });

                // Per-scheme rows: Preview + Build on each card, "In plan" chip, and
                // the expandable storey breakdown.
                Shot(dir, $"copilot-planning-schemes{s}.png", dark, configure: p =>
                {
                    SeedPlanning(p);
                    ScrollPlanningToSchemes(p);
                    return 400;
                });
                Shot(dir, $"copilot-planning-schemes-open{s}.png", dark, configure: p =>
                {
                    SeedPlanning(p);
                    ScrollPlanningToSchemes(p, expandFirst: true);
                    return 400;
                });

                // The floating Scheme Preview window (what the Preview button opens).
                SchemePreviewShot(dir, $"scheme-preview{s}.png", dark);
                SchemePreviewShot(dir, $"scheme-preview-l2{s}.png", dark, level: 2);
                SchemePreviewShot(dir, $"scheme-preview-collapsed{s}.png", dark, collapsed: true);
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

        // Drop the sample /planning/suggest result onto the Planning screen.
        private static int SeedPlanning(CopilotPanel panel)
        {
            panel.ViewModel.ShowPlanningPreview(
                MassingSample.School(),
                "sekolah rendah, Tahun 1–6 with 3 kelas each, plus pejabat, bilik guru, " +
                "bimbingan, keselamatan, bilik sukan, koku, 2 stor, dewan perhimpunan, " +
                "kantin, 4 tandas blocks");
            return 400;
        }

        // Scroll the Planning screen's own ScrollViewer to the bottom (the plan
        // canvas + actions). Searched from the PlanningView, not the panel, so it
        // can't grab the chat thread's scroller instead.
        private static void ScrollPlanningToEnd(CopilotPanel panel)
        {
            Settle(250);   // let the screen swap in and lay out
            var screen = FindDescendant<RevitWebAppSync.UI.Copilot.Screens.PlanningView>(panel);
            FindDescendant<ScrollViewer>(screen)?.ScrollToEnd();
        }

        // Bring the SCHEMES section into view — the per-scheme rows sit between the
        // SOA and the plan, so neither the top-of-screen nor the scroll-to-end shot
        // shows them. Optionally expands a row's storey breakdown first by clicking
        // its real chevron, so the shot proves the expand path works.
        private static void ScrollPlanningToSchemes(CopilotPanel panel, bool expandFirst = false)
        {
            Settle(250);
            var screen = FindDescendant<RevitWebAppSync.UI.Copilot.Screens.PlanningView>(panel);
            var host = screen?.FindName("SchemesHost") as FrameworkElement;
            if (expandFirst && host != null)
            {
                // First card's chevron is the only bare-template Button in its head row.
                var card = FindDescendant<Button>(host);
                var chevron = card == null ? null : FindDescendant<Button>(card);
                chevron?.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                Settle(300);
                host = screen.FindName("SchemesHost") as FrameworkElement;
            }
            host?.BringIntoView();
            Settle(250);
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

        // The floating Scheme Preview is its own top-level Window, so it can never
        // appear in a RenderTargetBitmap of the pane — show it offscreen and render
        // its content root directly (same trick as the popovers above).
        private static void SchemePreviewShot(string dir, string file, bool dark, int level = 1, bool collapsed = false)
        {
            CopilotTheme.SetDark(dark);
            var win = new RevitWebAppSync.UI.Copilot.Windows.SchemePreviewWindow
            {
                Width = 560, Height = 400, Left = -4000, Top = -4000,
            };
            win.Show();
            var sample = MassingSample.School();
            win.SetScheme(sample.Schemes[0], level);
            Settle(350);

            if (collapsed)
            {
                // Drive the real gesture rather than a test-only shortcut, so the
                // shot proves the collapse path actually works.
                var bar = win.FindName("TitleBar") as FrameworkElement;
                var glyph = win.FindName("CollapseGlyph") as UIElement;
                glyph?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = bar });
                Settle(300);
            }

            if (win.Content is FrameworkElement root) Save(root, Path.Combine(dir, file));
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
