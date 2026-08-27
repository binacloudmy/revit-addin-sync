using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RevitWebAppSync.UI;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace UiHarness
{
    /// <summary>
    /// Renders the JKR Audit Copilot panel to PNG files off-screen — no Revit, no
    /// visible window — so the build can be checked 1:1 against the Claude Design
    /// canvas (docs/model-audit/2-design/claude-design/"JKR Audit Copilot.dc.html").
    /// Invoked by <c>UiHarness --shot-jkr &lt;dir&gt;</c>.
    ///
    /// Every screen is driven through the real PanelVm against FixtureCopilotSource,
    /// so what is captured is the actual binding graph, not a mock-up.
    /// </summary>
    internal static class JkrShots
    {
        // Docked-panel proportions, matching the dc.html canvas.
        private const int PanelW = 430;
        private const int PanelH = 860;

        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);

            // S1 — run start: project / LOD / discipline / report language.
            // Captured twice: nothing chosen yet (Run disabled) and LOD chosen (Run enabled),
            // because C1 made LOD an explicit required selection.
            Shot(dir, "jkr-s1-run-start.png", vm => { });
            Shot(dir, "jkr-s1-run-ready.png", vm => { vm.SelectedLodLevel = 300; });

            // S2 — run progress. CurrentScreen is deliberately read-only from
            // outside, so rather than forcing it we hold the run open with a
            // delaying source: RunAsync parks the VM on S2 until the source
            // returns. Advance the scan marker part-way so the per-section states
            // (done / active / pending) are all visible at once.
            Shot(dir, "jkr-s2-progress.png", vm =>
            {
                vm.SelectedLodLevel = 300;
                vm.CopilotSource = new DelayedFixtureSource(TimeSpan.FromSeconds(30));
                var _ = vm.RunAsync();          // intentionally not awaited — leaves the VM on S2
                Settle(150);
                vm.AdvanceRunStep();
                vm.AdvanceRunStep();
            });

            // S3 — the Fix Queue: the main screen (verified %, open cells, section nav, groups).
            Shot(dir, "jkr-s3-fix-queue.png", vm => Run(vm));

            // S3 tabs — Open / Manual / Ignored / Resolved.
            foreach (var tab in new[] { CopilotTab.Open, CopilotTab.Manual, CopilotTab.Ignored, CopilotTab.Resolved })
            {
                var t = tab;
                Shot(dir, $"jkr-s3-tab-{t.ToString().ToLowerInvariant()}.png",
                     vm => { Run(vm); vm.ActiveCopilotTab = t; });
            }

            // S4 — issue detail pane (REQUIRED/ACTUAL, citation, proposed fix).
            Shot(dir, "jkr-s4-detail.png", vm => { Run(vm); OpenFirstDetail(vm, manual: false); });

            // S4 — a human-decides cell: this is principle 0.4 rendered. Must show
            // "WHY THIS IS YOURS TO CALL" + the three verdict buttons, and must NOT
            // show an AI comply/not-comply guess.
            Shot(dir, "jkr-s4-detail-manual.png", vm => { Run(vm); OpenFirstDetail(vm, manual: true); });

            // S5 — manual decisions queue.
            Shot(dir, "jkr-s5-manual.png", vm => { Run(vm); vm.ShowManual(); });

            // S6 — export handoff (Borang BIM005 / BIM010).
            Shot(dir, "jkr-s6-export.png", vm => { Run(vm); vm.GoExport(); });

            // The Zoom window — the view the Build Diff was written about. Captured at
            // its own size so the six regions (toolbar, hero+bar, leverage band, rail,
            // tabs/list, status bar) can be checked against the design in one image.
            ZoomShot(dir, "jkr-zoom-window.png");

            // Comfortable density — the header toggle changes row heights throughout.
            Shot(dir, "jkr-s3-density-large.png", vm => { Run(vm); vm.IsDensityComfortable = true; });
        }

        // The zoom window is a Window, not a panel, so it gets its own capture path.
        private static void ZoomShot(string dir, string file)
        {
            var vm = new PanelVm();
            Run(vm);
            var win = new RevitWebAppSync.UI.Jkr.ZoomWindow(vm)
            {
                Width = 1180, Height = 820,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false, Left = -4000, Top = -4000,
            };
            win.Show();
            Settle(500);
            var root = win.Content as FrameworkElement;
            if (root != null) Save(root, Path.Combine(dir, file));
            win.Close();
            Console.WriteLine($"wrote {file}");
        }

        // Drive a full fixture run so the VM lands on S3 with real data.
        private static void Run(PanelVm vm)
        {
            vm.SelectedLodLevel = 300;
            Pump(vm.RunAsync());
        }

        // Select the first rule row, optionally the first human-decides one.
        // Manual rules only appear in the Manual tab's groups, so switch there first.
        private static void OpenFirstDetail(PanelVm vm, bool manual)
        {
            if (manual) vm.ActiveCopilotTab = CopilotTab.Manual;
            foreach (var g in vm.Groups)
            {
                foreach (var r in g.Rules)
                {
                    bool isManual = string.Equals(r.Kind, "manual", StringComparison.OrdinalIgnoreCase);
                    if (isManual == manual) { vm.OpenDetail(r); return; }
                }
            }
        }

        /// <summary>
        /// Run a Task to completion on the STA UI thread. FixtureCopilotSource
        /// completes synchronously, but awaiting it still posts continuations to the
        /// dispatcher — blocking on .Wait() would deadlock, so pump frames instead.
        /// </summary>
        private static void Pump(System.Threading.Tasks.Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) Settle(20);
            if (task.IsFaulted) throw task.Exception;
        }

        /// <summary>Fixture data that arrives late, so the panel can be photographed
        /// mid-run on S2. The screenshot is taken while this is still pending; the
        /// window is closed straight after, so the delay is never actually waited out.</summary>
        private sealed class DelayedFixtureSource : RevitWebAppSync.Services.IJkrCopilotSource
        {
            private readonly TimeSpan _delay;
            public DelayedFixtureSource(TimeSpan delay) { _delay = delay; }

            public async System.Threading.Tasks.Task<RevitWebAppSync.Models.JkrCopilotRunData> LoadRunAsync(
                RevitWebAppSync.Models.PanelRunRequest request)
            {
                await System.Threading.Tasks.Task.Delay(_delay);
                return RevitWebAppSync.Services.FixtureCopilotSource.Build();
            }
        }

        private static void Shot(string dir, string file, Action<PanelVm> configure)
        {
            var panel = new JkrComplianceDashboardPanel();
            var frame = new Frame { Content = panel };
            var win = new Window
            {
                Width = PanelW, Height = PanelH, Content = frame,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = -4000, Top = -4000, ResizeMode = ResizeMode.NoResize,
            };
            win.Show();
            Settle(200);

            // State seeding is best-effort: one screen failing must not abort the run.
            try { configure(panel.ViewModel); }
            catch (Exception ex) { Console.Error.WriteLine($"{file}: seeding failed — {ex.Message}"); }
            Settle(250);

            Save(frame, Path.Combine(dir, file));
            win.Close();
            Console.WriteLine($"wrote {file}");
        }

        private static void Save(FrameworkElement el, string path)
        {
            el.UpdateLayout();
            var bmp = new RenderTargetBitmap(
                (int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        // Let WPF lay out / animate before the next step.
        private static void Settle(int ms)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                Dispatcher.CurrentDispatcher.Invoke(
                    DispatcherPriority.Background, new Action(() => { }));
                System.Threading.Thread.Sleep(5);
            }
        }
    }
}
