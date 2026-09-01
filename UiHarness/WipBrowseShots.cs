using System;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RevitWebAppSync;
using RevitWebAppSync.Services;

namespace UiHarness
{
    /// <summary>
    /// Renders ModelBrowserWindow to PNG in each of the states that matter,
    /// driven by WipBrowseStubHandler — no backend, no Revit, no visible window.
    /// Invoked by `UiHarness --shot-wip &lt;dir&gt;`.
    ///
    /// The states worth capturing are the ones a happy-path demo never reaches:
    /// a folder the role cannot read, a model it may browse but not download,
    /// and a model with no docGuid. Those are exactly where the wiring is most
    /// likely to be wrong, and a screenshot proves the bindings resolve.
    /// </summary>
    internal static class WipBrowseShots
    {
        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);

            Shot(dir, "wip-browse-folders.png", w => { });
            Shot(dir, "wip-browse-models.png", w => Select(w, "FoldersListBox", 0));
            Shot(dir, "wip-browse-versions.png", w =>
            {
                Select(w, "FoldersListBox", 0);
                Select(w, "ModelsListBox", 0);
            });
            // Web upload: no docGuid, so its history resolves by designId.
            Shot(dir, "wip-browse-web-upload.png", w =>
            {
                Select(w, "FoldersListBox", 0);
                Select(w, "ModelsListBox", 2);
            });
            // Browse-only row: download button dead, and the reason said out loud.
            Shot(dir, "wip-browse-locked.png", w =>
            {
                Select(w, "FoldersListBox", 0);
                Select(w, "ModelsListBox", 3);
            });
            // A folder this role cannot read — 403, reported as no access rather
            // than as an empty folder.
            Shot(dir, "wip-browse-forbidden.png", w => Select(w, "FoldersListBox", 2));

            // Shared: promoted rows, no docGuid, so versions resolve by designId.
            // Row 1 mirrors its source version (silent); row 2 does not, and is
            // the only one that says where it came from.
            Shot(dir, "browse-shared.png", w =>
            {
                Check(w, "SharedRadio");
                Select(w, "FoldersListBox", 0);
                Select(w, "ModelsListBox", 1);
            });
            // Published: read-wide, download-narrow.
            Shot(dir, "browse-published.png", w =>
            {
                Check(w, "PublishedRadio");
                Select(w, "FoldersListBox", 0);
                Select(w, "ModelsListBox", 0);
            });

            CaptureDownload(dir);
        }

        /// <summary>
        /// Drives a real download through the stub: mid-transfer progress is
        /// captured, then the run is left to finish so the .part swap can be
        /// checked on disk. Prints the outcome — this is the only place the
        /// staging-file dance runs before the backend exists.
        /// </summary>
        private static void CaptureDownload(string dir)
        {
            string root = Path.Combine(Path.GetTempPath(), "BINA_HarnessDownloads");
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }

            var api = new SyncApiClient(
                "https://harness.invalid",
                "harness-fake-token",
                new HttpClient(new WipBrowseStubHandler()));

            var win = new ModelBrowserWindow(api, 77, "Harness Project", root)
            {
                Width = 1040, Height = 660,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = -4000, Top = -4000, ResizeMode = ResizeMode.NoResize
            };

            win.Show();
            Settle(400);
            Select(win, "FoldersListBox", 0);
            Select(win, "ModelsListBox", 0);

            var button = win.FindName("DownloadButton") as Button;
            string expected = (win.FindName("DestinationText") as TextBlock)?.Text;

            if (button == null || !button.IsEnabled)
            {
                Console.WriteLine("download: button unavailable, skipped");
                win.Close();
                api.Dispose();
                return;
            }

            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Settle(700);
            Save((FrameworkElement)win.Content, Path.Combine(dir, "wip-browse-downloading.png"));

            // 12 MB in 80 KB reads (the client's buffer) at the stub's per-read
            // delay is ~6s, so wait well past that rather than reporting a
            // still-running transfer as a failed one.
            Settle(12000);

            bool landed = expected != null && File.Exists(expected);
            bool leftovers = expected != null && File.Exists(expected + ".part");
            long size = landed ? new FileInfo(expected).Length : 0;

            Console.WriteLine("download: file=" + landed + " bytes=" + size + " partial-left=" + leftovers);
            Console.WriteLine("download: path=" + expected);

            try { win.Close(); } catch { }
            api.Dispose();
        }

        private static void Shot(string dir, string file, Action<Window> configure)
        {
            var api = new SyncApiClient(
                "https://harness.invalid",
                "harness-fake-token",
                new HttpClient(new WipBrowseStubHandler()));

            var win = new ModelBrowserWindow(
                api, 77, "Harness Project",
                Path.Combine(Path.GetTempPath(), "BINA_HarnessDownloads"))
            {
                Width = 1040,
                Height = 660,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -4000,
                Top = -4000,
                ResizeMode = ResizeMode.NoResize
            };

            win.Show();
            Settle(400);   // the folder load is a round trip through the stub

            try { configure(win); }
            catch (Exception ex) { Console.WriteLine(file + ": seeding failed — " + ex.Message); }

            Settle(400);
            Save((FrameworkElement)win.Content, Path.Combine(dir, file));
            win.Close();
            api.Dispose();
        }

        /// <summary>
        /// Selects a row by index, then pumps the dispatcher so the selection's
        /// async load finishes before the next step or the capture.
        /// </summary>
        private static void Select(Window window, string listName, int index)
        {
            var list = window.FindName(listName) as ListBox;
            if (list == null) { Console.WriteLine("no list named " + listName); return; }

            Settle(150);
            if (list.Items.Count <= index)
            {
                Console.WriteLine(listName + " has " + list.Items.Count + " rows, wanted index " + index);
                return;
            }

            list.SelectedIndex = index;
            Settle(400);
        }

        /// <summary>Flips an area radio and lets its folder reload finish.</summary>
        private static void Check(Window window, string radioName)
        {
            var radio = window.FindName(radioName) as System.Windows.Controls.RadioButton;
            if (radio == null) { Console.WriteLine("no radio named " + radioName); return; }

            radio.IsChecked = true;
            Settle(500);
        }

        private static void Save(FrameworkElement el, string path)
        {
            el.UpdateLayout();

            var m = el.Margin;
            int w = (int)Math.Ceiling(el.ActualWidth + m.Left + m.Right);
            int h = (int)Math.Ceiling(el.ActualHeight + m.Top + m.Bottom);
            if (w <= 0 || h <= 0) { w = 1040; h = 660; }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        // Pump the dispatcher for `ms` so layout and in-flight loads advance
        // (a plain Sleep would freeze the render thread).
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
