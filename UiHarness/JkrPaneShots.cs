using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BinaVibe.Mcp.Tools.Audit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.Services;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Modals;
using RevitWebAppSync.UI.Jkr.Panel;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace UiHarness
{
    /// <summary>
    /// Renders the JKR Compliance pane (the Binazone shell) to PNG: Start, login
    /// required, and Results with the spec Checklist for a failing and a passing
    /// sample run (data/jkr_run_*.json — the same files SpecChecklistTests checks),
    /// and a run whose backend check failed; the Person filter with A7.2's warnings, and
    /// a list-only check expanded. The run goes through the add-in's own
    /// mapping (IssueMapper.MapAll, FromAuditRecords, AuditSummary, ToSpecResults),
    /// as a real scan does; the sample's "model" snapshot goes through the add-in's
    /// spec evaluators (SpecChecks.Run). Evaluators not written yet give no result,
    /// and those checks show as not checked.
    /// Also the issue detail (a fixable and a manual issue of the failing run), the
    /// confirm dialog before a write, and the Fix all result window.
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
            FontCheck();
            ActiveModelSource.Set(new ActiveModelInfo(Title, @"C:\Projects\Dewan\" + Title + ".rvt", false));

            Shot(dir, "jkr-start.png", s => { });
            Shot(dir, "jkr-start-en.png", s => s.Model.IsEn = true);
            Shot(dir, "jkr-start-lod300-en.png", s => { s.Model.IsEn = true; s.Start.LodValue = "300"; });
            Shot(dir, "jkr-start-lod100.png", s => s.Start.LodValue = "100");
            // Running (r-a, r-mid, r-done, r-fail): the sections complete every 750 ms on the
            // shell's real clock, so each frame waits for the time it needs (Shot adds 600 ms).
            Shot(dir, "jkr-run-scan.png", s => RunFrame(s, 0, false), 800);
            Shot(dir, "jkr-run-scan-en.png", s => { s.Model.IsEn = true; RunFrame(s, 0, false); }, 800);
            Shot(dir, "jkr-run-a-en.png", s => { s.Model.IsEn = true; RunFrame(s, 20, true); }, 800);
            Shot(dir, "jkr-run-mid.png", s => RunFrame(s, 1000, true), 800);
            Shot(dir, "jkr-run-mid-en.png", s => { s.Model.IsEn = true; RunFrame(s, 1000, true); }, 800);
            Shot(dir, "jkr-run-done-en.png", s => { s.Model.IsEn = true; RunFrame(s, 3500, true); }, 800);
            // production failure: the scan errors before any section is revealed, no section known
            Shot(dir, "jkr-run-fail.png", s => RunFrame(s, 0, false, fail: true), 800);
            Shot(dir, "jkr-run-fail-en.png", s => { s.Model.IsEn = true; RunFrame(s, 0, false, fail: true); }, 800);
            // variant for a scan that reports the section it stopped in
            Shot(dir, "jkr-run-fail-section-c-en.png", s => { s.Model.IsEn = true; RunFrame(s, 1500, true, fail: true, stoppedIn: "C"); }, 800);
            Shot(dir, "jkr-login-required.png", s => s.Model.LoginRequired = true);
            Shot(dir, "jkr-login-required-en.png", s => { s.Model.IsEn = true; s.Model.LoginRequired = true; }, 800);
            Shot(dir, "jkr-no-model.png", s => s.Model.Model = ActiveModelInfo.None, 800);
            Shot(dir, "jkr-no-model-en.png", s => { s.Model.IsEn = true; s.Model.Model = ActiveModelInfo.None; }, 800);
            Shot(dir, "jkr-toast-en.png", s => { s.Model.IsEn = true; s.ShowToast("Copied the issue list to the clipboard."); }, 800);
            Shot(dir, "jkr-failing.png", s => ShowRun(s, Failing));
            Shot(dir, "jkr-failing-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); });
            // The pane at a typical docked height: the Checklist must start above the fold.
            Shot(dir, "jkr-failing-790.png", s => ShowRun(s, Failing), 790);
            Shot(dir, "jkr-failing-790-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); }, 790);
            Shot(dir, "jkr-failing-section-d.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SelectSection("D"); });
            Shot(dir, "jkr-failing-filter-fails-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SetStateFilter("fail"); });
            Shot(dir, "jkr-failing-filter-person.png", s => { ShowRun(s, Failing); s.Results.GoNeedsPerson(); });
            // Revision 2: A7.2's non-JKR materials are warnings a person judges, not a fail.
            Shot(dir, "jkr-failing-person-warnings-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SetStateFilter("person"); }, 1500);
            // A list-only check expanded: the WIP views per level (collapsed by default).
            Shot(dir, "jkr-failing-list-expanded.png", s =>
            {
                ShowRun(s, Failing);
                s.Results.SelectSection("C");
                s.Results.ToggleShowAll("C1.2.1");
            }, 1500);
            Shot(dir, "jkr-failing-filter-partly-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SetStateFilter("partial"); });
            Shot(dir, "jkr-failing-open-tab-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.SelectTab(PanelTab.Open); });
            Shot(dir, "jkr-compliant.png", s => ShowRun(s, Compliant));
            Shot(dir, "jkr-compliant-en.png", s => { s.Model.IsEn = true; ShowRun(s, Compliant); });
            Shot(dir, "jkr-compliant-filter-pass-en.png", s => { s.Model.IsEn = true; ShowRun(s, Compliant); s.Results.SetStateFilter("pass"); });
            // The backend check failed: the add-in's own checks and the BIM010 checker still answer.
            Shot(dir, "jkr-backend-down.png", s => ShowRun(s, Failing, backendDown: true), 790);
            Shot(dir, "jkr-backend-down-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); });

            // The issue detail and the two fix modals (Direction C: Task, Verify).
            DetailShot(dir, "jkr-detail-fix-en.png", i => !i.IsHuman && i.Status == ItemStatus.Open && i.Fix == FixKind.Fix);
            DetailShot(dir, "jkr-detail-manual-en.png", i => i.IsHuman);
            ConfirmShot(dir, "jkr-confirm-en.png");
            FixResultShot(dir, "jkr-fixresult-en.png");
        }

        /// <summary>The detail window over the failing run, at the first issue that
        /// matches. The detail is opened on the core only (not Detail.Open), so the
        /// shell does not place its own window over the screen.</summary>
        private static void DetailShot(string dir, string file, Func<PanelItem, bool> pick)
        {
            var shell = new JkrPaneShell(new Grid());
            var host = new Window
            {
                Width = 400, Height = 1100, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = -4000, Top = -4000, ResizeMode = ResizeMode.NoResize, Content = shell,
            };
            host.Show();
            Settle(300);
            try
            {
                shell.Model.IsEn = true;
                ShowRun(shell, Failing);
                Settle(300);
                var item = shell.Core.Issues.FirstOrDefault(pick);
                if (item == null) { Console.WriteLine(file + ": no matching issue in " + Failing); return; }
                shell.Core.Open(item.Id);
                RenderWindow(NewWindow<DetailWindow>(shell.Detail), dir, file);
            }
            catch (Exception ex) { Console.WriteLine(file + ": failed — " + ex); }
            finally { host.Close(); }
        }

        /// <summary>The confirm dialog with the failing run's first ready-made fixes,
        /// over the pause threshold so the "Revit may pause" panel shows.</summary>
        private static void ConfirmShot(string dir, string file)
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", Failing)));
                var response = json["response"].ToObject<ModelCheckResponse>(JsonSerializer.CreateDefault());
                var rows = IssueMapper.MapAll(response)
                    .Where(i => i.AutoFixable && !string.IsNullOrEmpty(i.FixAction))
                    .Take(5)
                    .Select(JkrConfirmRow.FromIssue)
                    .ToList();
                var model = new JkrConfirmModel(JkrConfirmKind.Fix, rows, JkrFixBatch.PauseWarningElementCount + 120, PanelStrings.En);
                RenderWindow(NewWindow<JkrConfirmWindow>(model), dir, file);
            }
            catch (Exception ex) { Console.WriteLine(file + ": failed — " + ex); }
        }

        /// <summary>The Fix all result: 42 written, and failures in three reasons
        /// (category without JKR parameters, model group, a reason Revit gave).</summary>
        private static void FixResultShot(string dir, string file)
        {
            try
            {
                var r = new RenameResult();
                void Param(long id, string param, string why) =>
                    r.RecordFailure(RenameResult.MakeFixKey("set_parameter", id, param), id, PaneText.ParamWhat(param, id, PanelStrings.En), why);
                for (var i = 0; i < 7; i++)
                    Param(578862, "P" + i + "_jkr_stt", $"Parameter 'P{i}_jkr_stt' not found on element 578862. Element category does not accept bound parameters.");
                foreach (var id in new long[] { 901102, 901105, 930585 })
                    Param(id, "LOD_jkr_sit", $"Element {id} is inside a model group — Revit blocks instance parameter edits outside group edit mode.");
                Param(921291, "Bahan_jkr_stm", "The value is outside the range Revit allows.");
                var model = new FixResultModel(r, 42, fixAll: true, id => id == 578862 ? "Generic Models" : null, PanelStrings.En);
                RenderWindow(NewWindow<FixResultWindow>(model), dir, file);
            }
            catch (Exception ex) { Console.WriteLine(file + ": failed — " + ex); }
        }

        /// <summary>The pop-ups take their model in a private constructor (their public
        /// entry points place them over Revit's window and show them modally).</summary>
        private static T NewWindow<T>(object model) where T : Window =>
            (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { model }, null);

        /// <summary>Off-screen, settle, save the window's content, close.</summary>
        private static void RenderWindow(Window w, string dir, string file)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.ShowInTaskbar = false;
            w.Left = -4000;
            w.Top = -4000;
            w.Show();
            Settle(600);
            var root = w.Content as FrameworkElement ?? w;
            Save(root, Path.Combine(dir, file), (int)Math.Ceiling(w.ActualHeight));
            w.Close();
            Console.WriteLine("wrote " + file);
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
            issues.AddRange(IssueMapper.FromModelResults(modelResults, shell.Core.Lang).Where(i => known.Add(i.Id)));
            var unmapped = new List<string>();
            var cells = AuditSummary.CellsFromRecords(records);
            if (response != null) cells.AddRange(IssueMapper.ToCells(response, unmapped));
            var summary = AuditSummary.Build(cells, unmapped);
            // As the panel does (RunScanInner): backend + checker + model results, and the
            // backend error passed to Build so backend-only checks stay "not checked".
            var spec = SpecReport.Build(IssueMapper.ToSpecResults(response, records, modelResults),
                                        backendDown ? BackendTimeout : null);

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

        /// <summary>Prints which embedded font file WPF resolves for each weight of Bz.Font.Sans / Bz.Font.Mono.</summary>
        private static void FontCheck()
        {
            var tokens = new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/RevitWebAppSync;component/UI/Jkr/Panel/PanelTokens.xaml")
            };
            foreach (var key in new[] { "Bz.Font.Sans", "Bz.Font.Mono" })
            {
                var ff = (System.Windows.Media.FontFamily)tokens[key];
                foreach (var w in new[] { 400, 500, 600, 700 })
                {
                    var tf = new System.Windows.Media.Typeface(ff, System.Windows.FontStyles.Normal,
                        System.Windows.FontWeight.FromOpenTypeWeight(w), System.Windows.FontStretches.Normal);
                    if (tf.TryGetGlyphTypeface(out var gt))
                        Console.WriteLine($"fontcheck {key} {w}: {gt.FontUri} (face weight {gt.Weight.ToOpenTypeWeight()}, simulated bold {gt.Weight != System.Windows.FontWeight.FromOpenTypeWeight(w) || tf.IsBoldSimulated})");
                    else Console.WriteLine($"fontcheck {key} {w}: NO glyph typeface (fallback)");
                }
            }
        }

        /// <summary>The Running screen: scan started, then (reveal) the sections with
        /// numbers, waiting <paramref name="waitMs"/>; optionally the scan fails there.</summary>
        private static void RunFrame(JkrPaneShell s, int waitMs, bool reveal, bool fail = false, string stoppedIn = null)
        {
            s.Core.SetLod("300");
            s.Core.BeginExternalRun();
            s.Running.Begin("300", "AR");
            if (reveal) s.Running.Complete(new Dictionary<string, (int? Pct, int Pend)>
            {
                ["A"] = (44, 5), ["B"] = (0, 0), ["C"] = (21, 6), ["D"] = (20, 2), ["E"] = (60, 0),
            });
            if (waitMs > 0) Settle(waitMs);
            if (fail) s.Running.Fail("the model is synchronising with central. Run again when it finishes.", stoppedIn);
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
