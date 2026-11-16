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
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace UiHarness
{
    /// <summary>
    /// Renders the JKR Compliance pane (the Binazone shell) to PNG: Start, login
    /// required, and Results with the spec Checklist for a failing and a passing
    /// sample run (data/jkr_run_*.json — the same files SpecChecklistTests checks),
    /// and a run whose backend check failed. The run goes through the add-in's own
    /// mapping (IssueMapper.MapAll, FromAuditRecords, AuditSummary, ToSpecResults),
    /// as a real scan does; the sample's "model" snapshot goes through the add-in's
    /// spec evaluators (SpecChecks.Run). Evaluators not written yet give no result,
    /// and those checks show as not checked.
    /// Invoked by `UiHarness --shot-jkr &lt;dir&gt;`. No Revit, no backend.
    /// </summary>
    internal static class JkrPaneShots
    {
        private const string Title = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01";
        private const string Failing = "jkr_run_failing.json";
        private const string Compliant = "jkr_run_compliant.json";
        private const string BackendTimeout = "The backend check timed out after 60 s.";

        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            ActiveModelSource.Set(new ActiveModelInfo(Title, @"C:\Projects\Dewan\" + Title + ".rvt", false));

            Shot(dir, "jkr-start.png", s => { });
            Shot(dir, "jkr-login-required.png", s => s.Model.LoginRequired = true);
            Shot(dir, "jkr-failing.png", s => ShowRun(s, Failing));
            Shot(dir, "jkr-failing-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); });
            // The pane at a typical docked height: the Checklist must start above the fold.
            Shot(dir, "jkr-failing-790.png", s => ShowRun(s, Failing), 790);
            Shot(dir, "jkr-failing-790-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); }, 790);
            Shot(dir, "jkr-failing-section-d.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SelectSection("D"); });
            Shot(dir, "jkr-failing-filter-fails-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SetStateFilter("fail"); });
            Shot(dir, "jkr-failing-filter-person.png", s => { ShowRun(s, Failing); s.Results.GoNeedsPerson(); });
            Shot(dir, "jkr-failing-filter-partly-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SetStateFilter("partial"); });
            Shot(dir, "jkr-failing-open-tab.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SelectTab(PanelTab.Open); });
            Shot(dir, "jkr-compliant.png", s => ShowRun(s, Compliant));
            Shot(dir, "jkr-compliant-en.png", s => { s.Model.IsEn = true; ShowRun(s, Compliant); });
            Shot(dir, "jkr-compliant-filter-pass-en.png", s => { s.Model.IsEn = true; ShowRun(s, Compliant); s.Results.SetStateFilter("pass"); });
            // The backend check failed: the add-in's own checks and the BIM010 checker still answer.
            Shot(dir, "jkr-backend-down.png", s => ShowRun(s, Failing, backendDown: true), 790);
            Shot(dir, "jkr-backend-down-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); });
        }

        /// <summary>A run as the panel builds it, then the Running → Results hand-off.
        /// <paramref name="backendDown"/>: the backend check failed, so the run has only
        /// the BIM010 checker records and the model results, and the report says so.</summary>
        private static void ShowRun(JkrPaneShell shell, string file, bool backendDown = false)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)));
            var response = backendDown ? null : json["response"].ToObject<ModelCheckResponse>(JsonSerializer.CreateDefault());
            var records = json["audit"].Select(ToRecord).ToList();
            var modelResults = RunModel(json["model"]);

            var issues = response != null ? IssueMapper.MapAll(response) : new List<IssueVm>();
            var known = new HashSet<string>(issues.Select(i => i.Id));
            issues.AddRange(IssueMapper.FromAuditRecords(records).Where(i => known.Add(i.Id)));
            issues.AddRange(ModelIssues(modelResults, shell.Core.Lang).Where(i => known.Add(i.Id)));
            var unmapped = new List<string>();
            var cells = AuditSummary.CellsFromRecords(records);
            if (response != null) cells.AddRange(IssueMapper.ToCells(response, unmapped));
            var summary = AuditSummary.Build(cells, unmapped);
            var results = IssueMapper.ToSpecResults(response, records);
            results.AddRange(modelResults);
            var spec = SpecReport.Build(results);
            if (backendDown) spec.BackendError = BackendTimeout;

            shell.Core.SetLod("300");
            shell.Core.BeginExternalRun();
            shell.Results.Load(issues, summary, spec);
            shell.Core.FinishExternalRun();
        }

        /// <summary>The sample's model snapshot through the add-in's spec evaluators. A
        /// missing snapshot, or evaluators that throw, give no model results: the shot
        /// still renders (those checks show as not checked).</summary>
        private static List<SpecResult> RunModel(JToken snapshot)
        {
            if (snapshot == null || snapshot.Type != JTokenType.Object) return new List<SpecResult>();
            try
            {
                var model = snapshot.ToObject<SpecModel>(JsonSerializer.CreateDefault());
                var list = SpecChecks.Run(model) ?? new List<SpecResult>();
                Console.WriteLine("model: " + list.Count + " result(s) from " + SpecChecks.Rules.Count + " evaluator(s)");
                return list;
            }
            catch (Exception ex)
            {
                Console.WriteLine("model: evaluators failed — " + ex.Message);
                return new List<SpecResult>();
            }
        }

        /// <summary>
        /// Pane issues for the model results, the way the panel adds them: one per failed
        /// or unjudged result (a pass, a listing or a not-applicable result has none).
        /// The panel has its own version (with IssueMapper); this one is the harness's,
        /// so the shots show model findings in the Checklist and in the issue tabs.
        /// </summary>
        private static IEnumerable<IssueVm> ModelIssues(IEnumerable<SpecResult> results, string lang)
        {
            var en = string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase);
            foreach (var r in results)
            {
                if (r == null || r.NotApplicable || string.IsNullOrEmpty(r.IssueId) || SpecReport.IsPass(r.Status)) continue;
                var check = SpecCatalog.Find(r.CheckId);
                var unjudged = !SpecReport.IsFail(r.Status);
                var note = en && !string.IsNullOrWhiteSpace(r.NoteEn) ? r.NoteEn : r.Note ?? "";
                var elementId = r.ElementId > 0 ? r.ElementId : (r.ElementIds ?? new List<long>()).FirstOrDefault();
                yield return new IssueVm
                {
                    Id = r.IssueId,
                    CheckId = "SPEC-" + r.CheckId,
                    Domain = "spec",
                    BorangKey = check?.Item ?? "",
                    Category = check != null ? "Borang " + check.Section : "",
                    Title = r.CheckId + " · " + (check?.Title.In(lang) ?? r.Rule ?? ""),
                    Description = note,
                    Actual = r.Actual ?? "",
                    Required = r.Required ?? "",
                    RevitElementId = elementId,
                    Element = new ElementRef { Id = elementId > 0 ? elementId.ToString() : "—", Name = r.ElementName ?? "" },
                    Verification = unjudged ? IssueVerification.Human : IssueVerification.Ai,
                    Spec = new SpecRef { Doc = "", Clause = check?.Reference ?? "" },
                };
            }
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

        private static void Shot(string dir, string file, Action<JkrPaneShell> configure, int height = 1100)
        {
            var shell = new JkrPaneShell(new Grid());
            var win = new Window
            {
                Width = 400,
                Height = height,
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
            Save(shell, Path.Combine(dir, file), height);
            win.Close();
            Console.WriteLine("wrote " + file);
        }

        private static void Save(FrameworkElement el, string path, int height)
        {
            el.UpdateLayout();
            int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
            if (w <= 0 || h <= 0) { w = 400; h = height; }
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
