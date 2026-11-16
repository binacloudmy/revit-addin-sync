using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BinaVibe.Mcp.Tools.Audit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Panel;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace UiHarness
{
    /// <summary>
    /// Renders the JKR Compliance pane (the Binazone shell) to PNG: Start, login
    /// required, and Results with the spec Checklist for a failing and a passing
    /// sample run (data/jkr_run_*.json — the same files SpecChecklistTests checks).
    /// The run goes through the add-in's own mapping (IssueMapper.MapAll,
    /// FromAuditRecords, AuditSummary, ToSpecResults), as a real scan does.
    /// Invoked by `UiHarness --shot-jkr &lt;dir&gt;`. No Revit, no backend.
    /// </summary>
    internal static class JkrPaneShots
    {
        private const string Title = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01";

        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            ActiveModelSource.Set(new ActiveModelInfo(Title, @"C:\Projects\Dewan\" + Title + ".rvt", false));

            Shot(dir, "jkr-start.png", s => { });
            Shot(dir, "jkr-login-required.png", s => s.Model.LoginRequired = true);
            Shot(dir, "jkr-failing.png", s => ShowRun(s, "jkr_run_failing.json"));
            Shot(dir, "jkr-failing-en.png", s => { s.Model.IsEn = true; ShowRun(s, "jkr_run_failing.json"); });
            Shot(dir, "jkr-failing-section-d.png", s => { s.Model.IsEn = true; ShowRun(s, "jkr_run_failing.json"); s.Results.SelectSection("D"); });
            Shot(dir, "jkr-failing-open-tab.png", s => { s.Model.IsEn = true; ShowRun(s, "jkr_run_failing.json"); s.Results.SelectTab(PanelTab.Open); });
            Shot(dir, "jkr-compliant.png", s => ShowRun(s, "jkr_run_compliant.json"));
            Shot(dir, "jkr-compliant-en.png", s => { s.Model.IsEn = true; ShowRun(s, "jkr_run_compliant.json"); });
        }

        /// <summary>A run as the panel builds it, then the Running → Results hand-off.</summary>
        private static void ShowRun(JkrPaneShell shell, string file)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)));
            var response = json["response"].ToObject<ModelCheckResponse>(JsonSerializer.CreateDefault());
            var records = json["audit"].Select(ToRecord).ToList();

            var issues = IssueMapper.MapAll(response);
            var known = new HashSet<string>(issues.Select(i => i.Id));
            issues.AddRange(IssueMapper.FromAuditRecords(records).Where(i => known.Add(i.Id)));
            var unmapped = new List<string>();
            var cells = AuditSummary.CellsFromRecords(records);
            cells.AddRange(IssueMapper.ToCells(response, unmapped));
            var summary = AuditSummary.Build(cells, unmapped);
            var spec = SpecReport.Build(IssueMapper.ToSpecResults(response, records));

            shell.Core.SetLod("300");
            shell.Core.BeginExternalRun();
            shell.Results.Load(issues, summary, spec);
            shell.Core.FinishExternalRun();
        }

        private static AuditRecord ToRecord(JToken a)
        {
            var key = (string)a["section"] + (string)a["row_ref"];
            var n = (int)a["element_count"];
            var reason = (string)a["not_verifiable_reason"];
            return new AuditRecord
            {
                Row = new AuditFormRow
                {
                    Section = (string)a["section"],
                    RowRef = (string)a["row_ref"],
                    Description = Bim010Form.Find(key)?.Description ?? "",
                },
                CheckerMatched = true,
                CheckerId = (string)a["checker_id"],
                Compliance = (string)a["compliance"],
                RulePattern = (string)a["rule"],
                Severity = (string)a["severity"] ?? "",
                Remark = (string)a["remark"] ?? "",
                ElementIds = Enumerable.Range(1, n).Select(i => 900000L + i).ToList(),
                Evidence = reason == null ? null : new Dictionary<string, object> { ["not_verifiable_reason"] = reason },
            };
        }

        private static void Shot(string dir, string file, Action<JkrPaneShell> configure)
        {
            var shell = new JkrPaneShell(new Grid());
            var win = new Window
            {
                Width = 400,
                Height = 1100,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -4000,
                Top = -4000,
                ResizeMode = ResizeMode.NoResize,
                Content = shell,
            };
            win.Show();
            Settle(300);
            try { configure(shell); }
            catch (Exception ex) { Console.WriteLine(file + ": seeding failed — " + ex); }
            Settle(600);
            Save(shell, Path.Combine(dir, file));
            win.Close();
            Console.WriteLine("wrote " + file);
        }

        private static void Save(FrameworkElement el, string path)
        {
            el.UpdateLayout();
            int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
            if (w <= 0 || h <= 0) { w = 400; h = 1100; }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
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
