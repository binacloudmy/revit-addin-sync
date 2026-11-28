using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RevitWebAppSync.UI.CadToBim.Check;
using RevitWebAppSync.UI.Copilot;

namespace UiHarness
{
    /// <summary>`UiHarness --shot-check &lt;dir&gt;` (AI-638): the Check pane's screens 2-7 at the canvas pane size
    /// (520×906), from the real ASWARA snapshot + the engine-shortcut fixture. 6 screens light @125%,
    /// plus screen 6 dark @100/125/150%. FRIDAY compares each against its artboard's pane crop.</summary>
    internal static class CheckShots
    {
        private static readonly (string File, int Steps)[] Screens =
        {
            ("w02-preflight", 0), ("w03-consultant", 1), ("w04-gate1", 2), ("w05-gate2", 3), ("w06-questions", 4), ("w07-rows", 5),
        };

        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var (file, steps) in Screens) Shot(dir, file + "-light-125.png", dark: false, steps, 1.25);
            foreach (double s in new[] { 1.0, 1.25, 1.5 }) Shot(dir, "w06-questions-dark-" + (int)(s * 100) + ".png", dark: true, 4, s);
            Shot(dir, "w02-preflight-workshared-light-125.png", dark: false, 0, 1.25, workshared: true);   // the "Preview not shown" line + file row
        }

        private static void Shot(string dir, string file, bool dark, int steps, double scale, bool workshared = false)
        {
            CopilotTheme.SetDark(dark);
            string data = Path.Combine(AppContext.BaseDirectory, "cad2bim");
            var vm = new CheckViewModel(_ => null, null, EngineShortcuts.FromJson(File.ReadAllText(Path.Combine(data, "aswara-engine-shortcuts.json"))));
            vm.Show(SheetSnapshot.FromJson(File.ReadAllText(Path.Combine(data, "aswara-sheet.json"))),
                    new TargetInfo { DocTitle = "ASWARA_ARCH", Workshared = workshared, Levels = { new LevelInfo { Name = "Level 1", Id = 311 } } });
            for (int i = 0; i < steps; i++) vm.Next();
            if (steps == 4) vm.Answer(vm.Questions[0], vm.Questions[0].Options[0]);     // W06: card 1 answered, card 2 focused
            if (steps == 4) vm.Focus(vm.Questions[1]);
            if (steps == 5)
            {
                foreach (var q in vm.Questions) vm.Answer(q, q.Options[0]);
                vm.Next();
                vm.ToggleRow(vm.Rows[0]);                                                 // W07: the layers row is open
            }

            Render(new CheckPane(vm), Path.Combine(dir, file), scale);
        }

        /// <summary>Show the pane at 520x906 off-screen, settle, and write it as a PNG at the given DPI scale (AI-642 reuses it).</summary>
        internal static void Render(CheckPane pane, string path, double scale)
        {
            pane.Width = 520;
            pane.Height = 906;   // the pane itself is the canvas size; the window only hosts it
            var win = new Window
            {
                Content = pane, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = -4000, Top = -4000, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            };
            win.Show();
            pane.SwapTheme();
            win.SetResourceReference(Control.BackgroundProperty, "Cb.Pane");
            Settle(300);

            pane.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)(520 * scale), (int)(906 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(pane);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            win.Close();
        }

        private static void Settle(int ms)
        {
            var frame = new DispatcherFrame();
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
