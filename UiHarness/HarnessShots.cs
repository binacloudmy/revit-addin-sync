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

            // Agent activity (design "list all doors" run): live mid-run card,
            // then the completed turn with the card expanded (nested tool cards).
            Shot(dir, "copilot-activity-live.png", dark: false, configure: p => { SeedActivity(p, done: false); return 700; });
            Shot(dir, "copilot-activity-done.png", dark: false, configure: p => { SeedActivity(p, done: true); return 700; });
            // Active step WITHOUT counts → the indeterminate working bar.
            Shot(dir, "copilot-activity-busy.png", dark: false, configure: p => { SeedActivityBusy(p); return 700; });

            // Saved Commands J1: the Save-as-command sheet over a completed turn.
            Shot(dir, "copilot-save-sheet.png", dark: false, configure: p => { SeedSaveSheet(p); return 700; });

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

        /// <summary>Seed the design's "list all doors in this model" run.
        /// done:false — turn mid-flight: thinking prose settled, step 1 done,
        /// step 2 running with the determinate scan count (card open, spinner).
        /// done:true — completed AiReply carrying the whole trail + nested tool
        /// cards; the collapsed activity card is then expanded by raising the
        /// header's real click event so the shot shows the open state.</summary>
        private static int SeedActivity(CopilotPanel panel, bool done)
        {
            var vm = panel.ViewModel;
            vm.Thread.Add(new ChatMessage
            {
                Role = "user", Kind = CpMsgKind.User, Time = "3:31 PM",
                Text = "list all doors in this model",
            });

            var thinking = "Request: list all doors in this model. I'll filter elements in the 'Doors' " +
                           "category across all levels, group them by type_name and level, then validate " +
                           "the counts before composing the answer.";
            var now = DateTime.UtcNow;
            ProgressStep Step(string id, string label, string detail, StepState st, double startAgo, double? dur,
                              int cur = -1, int tot = -1) => new ProgressStep
            {
                StepId = id, Label = label, Detail = detail ?? "", State = st,
                StartedUtc = now.AddSeconds(-startAgo),
                EndedUtc = dur.HasValue ? now.AddSeconds(-startAgo + dur.Value) : null,
                Current = cur, Total = tot,
            };
            var reasoning = new List<ReasoningStep>
            {
                new ReasoningStep { StepId = "r1", Label = "Thinking", Text = thinking, State = ReasoningState.Done,
                                    StartedUtc = now.AddSeconds(-6) },
            };

            if (!done)
            {
                var live = new List<ProgressStep>
                {
                    Step("s1", "Understood what you asked for", "Every door in the model, no filtering by level", StepState.Done, 4.0, 0.3),
                    Step("call_1", "Looking for elements on Doors", "", StepState.Running, 3.5, null, cur: 36, tot: 62),
                };
                vm.Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Thinking, Time = "3:31 PM",
                    LiveReasoningSteps = reasoning, LiveSteps = live,
                });
                return 700;
            }

            RevitWebAppSync.Services.ToolResultEvent Tool(string id, string tool, int ms, string args, string result) =>
                new RevitWebAppSync.Services.ToolResultEvent { ToolCallId = id, Tool = tool, Ok = true, DurationMs = ms,
                                      ArgsDigest = args, ResultDigest = result };
            var doneSteps = new List<ProgressStep>
            {
                Step("s1", "Understood what you asked for", "Every door in the model, no filtering by level", StepState.Done, 5.0, 0.3),
                Step("call_1", "Looked for elements on Doors", "Found 62 doors", StepState.Done, 4.6, 2.6),
                Step("call_2", "Counted by group on Doors", "10 different door types", StepState.Done, 1.9, 0.3),
                Step("s4", "Checked the numbers add up", "All 62 counted once. No duplicates, nothing skipped.", StepState.Done, 1.5, 0.4),
                Step("s5", "Wrote the answer", "A summary with the door type table", StepState.Done, 1.0, 0.5),
            };
            vm.Thread.Add(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply, Time = "3:31 PM",
                Text = "**62 doors in this model — 60 on Level 01, 2 on Level 02.**",
                ReasoningSteps = reasoning,
                Steps = doneSteps,
                Blocks = new List<TurnBlock>
                {
                    new TurnBlock { Kind = TurnBlockKind.ToolCard, ToolResult = Tool("call_1", "find_elements_by_filter", 2600,
                        "{\"category\": \"Doors\"}",
                        "{'category': 'Doors', 'matches': [{'id': 1042809, 'name': '(PTa001a) 1800 x 2100 sp-pl', 'level': 'Level 01'}, … +61 more]}") },
                    new TurnBlock { Kind = TurnBlockKind.ToolCard, ToolResult = Tool("call_2", "count_by", 300,
                        "{\"by\": [\"type_name\", \"level\"]}",
                        "{'PTh300a': 21, 'PTt760b': 15, 'PTr680a': 8, 'PTn520a': 4, 'PT2p600a': 4, 'others': 10}") },
                    new TurnBlock { Kind = TurnBlockKind.Narrative, SegmentId = "seg1",
                        Text = "**62 doors in this model — 60 on Level 01, 2 on Level 02.**\n\n" +
                               "| Door type | Size (mm) | Count |\n|---|---|---|\n" +
                               "| PTh300a | 750 × 2100 | 21 |\n| PTt760b | 3700 × 2100 | 15 |\n" +
                               "| PTr680a | 900 × 2100 | 8 |\n| PTn520a | 450 × 2100 | 4 |\n" +
                               "| PT2p600a | 900 × 2325 | 4 |\n| Others (5 types) | various | 10 |" },
                },
            });

            // Expand the collapsed activity card through its real header toggle.
            panel.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            var activity = FindDescendant<RevitWebAppSync.UI.Copilot.Controls.AgentActivityView>(panel);
            if (activity?.Child is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is Border header)
            {
                header.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            }
            return 700;
        }

        /// <summary>Turn mid-flight on a step with NO counts (an engine phase /
        /// fast scan): the active row shows the indeterminate working bar.</summary>
        private static int SeedActivityBusy(CopilotPanel panel)
        {
            var vm = panel.ViewModel;
            vm.Thread.Add(new ChatMessage
            {
                Role = "user", Kind = CpMsgKind.User, Time = "3:31 PM",
                Text = "list all doors in this model",
            });
            var now = DateTime.UtcNow;
            vm.Thread.Add(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.Thinking, Time = "3:31 PM",
                LiveReasoningSteps = new List<ReasoningStep>
                {
                    new ReasoningStep { StepId = "r1", Label = "Thinking",
                        Text = "Request: list all doors in this model. I'll query the Doors category and count by type.",
                        State = ReasoningState.Done, StartedUtc = now.AddSeconds(-4) },
                },
                LiveSteps = new List<ProgressStep>
                {
                    new ProgressStep { StepId = "s1", Label = "Read the request", Phase = "classifying",
                        Detail = "list doors → filter category Doors", State = StepState.Done,
                        StartedUtc = now.AddSeconds(-3), EndedUtc = now.AddSeconds(-2.7) },
                    new ProgressStep { StepId = "gather", Label = "Collecting information", Phase = "retrieving",
                        State = StepState.Done,
                        StartedUtc = now.AddSeconds(-2.6), EndedUtc = now.AddSeconds(-2.2) },
                    new ProgressStep { StepId = "run", Label = "Generating answer", Phase = "writing",
                        State = StepState.Running, StartedUtc = now.AddSeconds(-2) },
                },
            });
            return 700;
        }

        /// <summary>Open the Save-as-command sheet over a completed doors turn,
        /// with one input already marked — the design's "Save sheet" artboard.</summary>
        private static int SeedSaveSheet(CopilotPanel panel)
        {
            SeedActivity(panel, done: true);
            panel.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            var chat = FindDescendant<RevitWebAppSync.UI.Copilot.Screens.ChatView>(panel);
            if (chat == null) return 500;
            var layer = chat.FindName("SaveLayer") as FrameworkElement;
            var sheet = chat.FindName("SaveSheet") as RevitWebAppSync.UI.Copilot.Controls.SaveCommandSheet;
            if (layer == null || sheet == null) return 500;
            var draft = RevitWebAppSync.UI.Copilot.Model.SavedCommandDraft.FromReply(
                "Bina dinding dari CAD di Level 2, guna 150mm brick",
                new[] { "list_levels", "extract_cad_geometry", "create_wall" }, "run-shot");
            var idx = draft.Template.IndexOf("Level 2", StringComparison.Ordinal);
            draft.MarkInput(idx, "Level 2".Length, "level", out _);
            layer.Visibility = Visibility.Visible;
            sheet.Show(draft, d => System.Threading.Tasks.Task.FromResult<string>(null));
            return 700;
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
