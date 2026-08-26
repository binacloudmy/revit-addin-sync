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
    /// Renders SyncOptionsWindow to PNG in the states the lineage picker has to
    /// get right, driven by SyncOptionsStubHandler — no backend, no Revit, no
    /// visible window. Invoked by `UiHarness --shot-sync &lt;dir&gt;`.
    ///
    /// The states worth capturing are the ones a happy-path demo never reaches:
    /// a folder that already holds a model of this document's name (where "new
    /// model" cannot be honoured), a target whose name differs from the local
    /// file (where the upload name is not the local one), a GUID-less web upload
    /// as the target, and a folder with nothing to join.
    /// </summary>
    internal static class SyncOptionsShots
    {
        private const string LocalFile = "jkrAR27_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a.rvt";
        private const string LocalGuid = "abcdabcd-0000-4e55-8f06-eeff00112233";

        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);

            // Default state: new model, no name clash — the common first sync.
            Shot(dir, "sync-new-model.png", LocalFile, w => { });

            // Same folder, but this document is named like one already there.
            // bina-be matches lineage on the filename, so "new model" is not on
            // offer whatever the radio says — both warnings must show.
            Shot(dir, "sync-collision.png", SyncOptionsStubHandler.ExistingFileName, w =>
            {
                // The matching row is pre-selected on load; the radio stays on
                // "new model" so the collision warning is what is under test.
            });

            // The routine re-sync: same name AND same provenance GUID as the row
            // already there, so this is just v8 of the user's own model and the
            // clash warning must stay away.
            Shot(dir, "sync-own-chain.png", SyncOptionsStubHandler.ExistingFileName,
                w => { }, docGuid: SyncOptionsStubHandler.ExistingDocGuid);

            // The same clash, confirmed: the row carrying this document's name is
            // badged and pre-selected, and no rename warning applies.
            Shot(dir, "sync-target-matches.png", SyncOptionsStubHandler.ExistingFileName,
                w => Check(w, "ExistingModelRadio"));

            // Existing-model mode with nothing picked yet.
            Shot(dir, "sync-pick-model.png", LocalFile, w => Check(w, "ExistingModelRadio"));

            // Target picked, and its name differs from the local file — the
            // "stored in BINA as" warning is the whole safety story here.
            Shot(dir, "sync-target-selected.png", LocalFile, w =>
            {
                Check(w, "ExistingModelRadio");
                Select(w, "ModelsListBox", 0);
            });

            // A web upload carries no docGuid. It is still targetable: the
            // add-in sends null and lets the head's GUID stand.
            Shot(dir, "sync-target-web-upload.png", LocalFile, w =>
            {
                Check(w, "ExistingModelRadio");
                Select(w, "ModelsListBox", 2);
            });

            // Empty folder: nothing to join, so the second radio is disabled.
            Shot(dir, "sync-empty-folder.png", LocalFile, w =>
            {
                SelectCombo(w, "FolderCombo", 1);
            });

            // Enough models for the search box, and a partial page, so the count
            // has to admit it is not showing everything.
            Shot(dir, "sync-many-models.png", LocalFile, w =>
            {
                SelectCombo(w, "FolderCombo", 2);
                Check(w, "ExistingModelRadio");
            });

            // A discipline with no WIP folders at all — the dialog explains
            // itself instead of showing an empty combo.
            Shot(dir, "sync-no-folders.png", LocalFile, w =>
            {
                SelectCombo(w, "DisciplineCombo", 3);   // Electrical
            });
        }

        private static void Shot(string dir, string file, string fileName, Action<Window> configure,
                                 string docGuid = LocalGuid)
        {
            var api = new SyncApiClient(
                "https://harness.invalid",
                "harness-fake-token",
                new HttpClient(new SyncOptionsStubHandler()));

            var win = new SyncOptionsWindow(
                api,
                fileName: fileName,
                docGuid: docGuid,
                defaultProjectId: 77,
                defaultProjectName: "Harness Project",
                suggestedDiscipline: DisciplineTypes.Architecture)
            {
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -4000,
                Top = -4000
            };

            win.Show();
            Settle(600);   // projects -> folders -> models -> head, all round trips

            try { configure(win); }
            catch (Exception ex) { Console.WriteLine(file + ": seeding failed — " + ex.Message); }

            Settle(500);
            Save((FrameworkElement)win.Content, Path.Combine(dir, file));
            win.Close();
            api.Dispose();
        }

        /// <summary>Selects a list row, then lets the target panel catch up.</summary>
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
            Settle(300);
        }

        /// <summary>Changes a combo and lets the reload it triggers finish.</summary>
        private static void SelectCombo(Window window, string comboName, int index)
        {
            var combo = window.FindName(comboName) as ComboBox;
            if (combo == null) { Console.WriteLine("no combo named " + comboName); return; }

            Settle(150);
            if (combo.Items.Count <= index)
            {
                Console.WriteLine(comboName + " has " + combo.Items.Count + " rows, wanted index " + index);
                return;
            }

            combo.SelectedIndex = index;
            Settle(600);
        }

        private static void Check(Window window, string radioName)
        {
            var radio = window.FindName(radioName) as RadioButton;
            if (radio == null) { Console.WriteLine("no radio named " + radioName); return; }
            if (!radio.IsEnabled) { Console.WriteLine(radioName + " is disabled"); return; }

            radio.IsChecked = true;
            Settle(300);
        }

        private static void Save(FrameworkElement el, string path)
        {
            el.UpdateLayout();

            var m = el.Margin;
            int w = (int)Math.Ceiling(el.ActualWidth + m.Left + m.Right);
            int h = (int)Math.Ceiling(el.ActualHeight + m.Top + m.Bottom);
            if (w <= 0 || h <= 0) { w = 620; h = 720; }

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
