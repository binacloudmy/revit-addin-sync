using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BinaVibe.Mcp.Tools.Audit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.Services;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Export;
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

        /// <summary>Only the frames whose file name contains this (third argument); null = all.</summary>
        private static string _only;
        private static bool Skip(string file) => _only != null && file.IndexOf(_only, StringComparison.OrdinalIgnoreCase) < 0;

        public static void Capture(string dir, string only = null)
        {
            _only = string.IsNullOrWhiteSpace(only) ? null : only;
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
            Shot(dir, "jkr-failing-section-d.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.OpenGroup("D"); });
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
            // Clear Sheet result screens (Task 12): the list at 380 / 320 px, aspects, a section,
            // an aspect, the other rules, and the degraded result (LOD changed + backend down).
            Shot(dir, "jkr-failing-320.png", s => ShowRun(s, Failing), 800, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-failing-section-d-320.png", s => { ShowRun(s, Failing); s.Results.OpenGroup("D"); }, 1100, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-failing-issues-320.png", s => { ShowRun(s, Failing); s.Results.ShowIssues(); }, 800, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-failing-aspects-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.ShowAspectsView(); }, 900);
            Shot(dir, "jkr-failing-aspect-p-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.ShowAspectsView(); s.Results.OpenGroup("P"); }, 1100);
            Shot(dir, "jkr-failing-section-d-issues-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.OpenGroup("D"); s.Results.ShowSectionIssues(); }, 900);
            Shot(dir, "jkr-failing-section-d-issues-320.png", s => { ShowRun(s, Failing); s.Results.OpenGroup("D"); s.Results.ShowSectionIssues(); }, 900, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-failing-other-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing); s.Results.OpenGroup("+"); }, 900);
            Shot(dir, "jkr-compliant-aspects-en.png", s => { s.Model.IsEn = true; ShowRun(s, Compliant); s.Results.ShowAspectsView(); }, 900);
            Shot(dir, "jkr-degraded-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Core.SetLod("400"); }, 900);
            // Clear Sheet issues (Task 13): 380 EN / 320 BM, a marked-manual row, an AI fixer row.
            Shot(dir, "jkr-issues-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, tweak: IssueSamples); s.Results.ShowIssues(); }, 900);
            Shot(dir, "jkr-issues-320.png", s => { ShowRun(s, Failing, tweak: IssueSamples); s.Results.ShowIssues(); }, 900, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-issues-320-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, tweak: IssueSamples); s.Results.ShowIssues(); }, 900, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-issues-resolved-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, tweak: IssueSamples); s.Results.SelectTab(PanelTab.Resolved); }, 700);
            Shot(dir, "jkr-issues-manual-tab.png", s => { ShowRun(s, Failing, tweak: IssueSamples); s.Results.SelectTab(PanelTab.Manual); }, 900, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-degraded-320.png", s => { ShowRun(s, Failing, backendDown: true); s.Core.SetLod("400"); }, 900, (int)JkrPaneShell.MinimumWidth);

            // Live test (Revit 2027, 2026-11-19): the pane at 815 px with the backend banner.
            // F5: the Issues tab (and a section's issues, with its pill) shows at least 5 rows;
            // F1: the Checklist at least 4 section rows; F2: the legend clears the footer.
            Shot(dir, "jkr-live-issues-815-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true, tweak: IssueSamples); s.Results.ShowIssues(); }, 815);
            Shot(dir, "jkr-live-issues-815.png", s => { ShowRun(s, Failing, backendDown: true, tweak: IssueSamples); s.Results.ShowIssues(); }, 815);
            Shot(dir, "jkr-live-section-d-issues-815-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Results.OpenGroup("D"); s.Results.ShowSectionIssues(); }, 815);
            Shot(dir, "jkr-live-section-d-issues-815-320.png", s => { ShowRun(s, Failing, backendDown: true); s.Results.OpenGroup("D"); s.Results.ShowSectionIssues(); }, 815, (int)JkrPaneShell.MinimumWidth);
            Shot(dir, "jkr-live-manual-tab-815-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Results.SelectTab(PanelTab.Manual); }, 815);
            Shot(dir, "jkr-live-issues-scrolled-815-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Results.ShowIssues(); Settle(300); ScrollTo(s, "BodyScroll", 900); }, 815);
            // Live retest (R5): the pane at Revit's 652 px (125 % scaling, 524 px wide) with the banner:
            // Issues at least 4 rows at rest, the Checklist at least 3 section rows at rest.
            Shot(dir, "jkr-live-issues-652-524-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Results.ShowIssues(); }, 652, 524);
            Shot(dir, "jkr-live-issues-652-524.png", s => { ShowRun(s, Failing, backendDown: true); s.Results.ShowIssues(); }, 652, 524);
            Shot(dir, "jkr-live-section-d-issues-652-524-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Results.OpenGroup("D"); s.Results.ShowSectionIssues(); }, 652, 524);
            Shot(dir, "jkr-live-checklist-652-524-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); }, 652, 524);
            Shot(dir, "jkr-live-checklist-652-524.png", s => ShowRun(s, Failing, backendDown: true), 652, 524);
            Shot(dir, "jkr-live-issues-652-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Results.ShowIssues(); }, 652);
            Shot(dir, "jkr-live-checklist-652-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); }, 652);
            Shot(dir, "jkr-live-issues-search-652-524-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); s.Results.ShowIssues(); s.Results.Query = "door"; }, 652, 524);
            Shot(dir, "jkr-live-checklist-815-en.png", s => { s.Model.IsEn = true; ShowRun(s, Failing, backendDown: true); }, 815);
            Shot(dir, "jkr-live-checklist-815.png", s => ShowRun(s, Failing, backendDown: true), 815);
            Shot(dir, "jkr-live-checklist-end-815.png", s => { ShowRun(s, Failing, backendDown: true); Settle(300); ScrollTo(s, "BodyScroll", double.MaxValue); }, 815);
            Shot(dir, "jkr-live-aspects-end-815.png", s => { ShowRun(s, Failing, backendDown: true); s.Results.ShowAspectsView(); Settle(300); ScrollTo(s, "BodyScroll", double.MaxValue); }, 815);
            Shot(dir, "jkr-live-aspects-end-815-320.png", s => { ShowRun(s, Failing, backendDown: true); s.Results.ShowAspectsView(); Settle(300); ScrollTo(s, "BodyScroll", double.MaxValue); }, 815, (int)JkrPaneShell.MinimumWidth);

            // The issue detail and the two fix modals (Task, Verify).
            // Clear Sheet: issue with a fix, with an AI fixer proposal, semak manual issue; plus
            // the states Clear Sheet does not draw (Reopen, Mark as Manual, fix failed, AI can propose).
            Func<PanelItem, bool> fix = i => !i.IsHuman && i.Status == ItemStatus.Open && i.Fix == FixKind.Fix && !i.MarkedManual;
            Func<PanelItem, bool> noFix = i => !i.IsHuman && i.Status == ItemStatus.Open && i.Fix == FixKind.None && !i.MarkedManual;
            // Below High, so Mark as Manual is offered (JkrTierMap.CanMarkManual).
            Func<PanelItem, bool> fixMed = i => fix(i) && i.CanMarkManual;
            Func<PanelItem, bool> noFixMed = i => noFix(i) && i.CanMarkManual && i.Act.En != "—";
            DetailShot(dir, "jkr-detail-fix-en.png", fix);
            DetailShot(dir, "jkr-detail-fix-mark-en.png", fixMed);
            DetailShot(dir, "jkr-detail-fix.png", fix, en: false);
            DetailShot(dir, "jkr-detail-manual-en.png", i => i.IsHuman, tab: PanelTab.Manual);
            DetailShot(dir, "jkr-detail-manual.png", i => i.IsHuman, tab: PanelTab.Manual, en: false);
            // Harness sample: today the AI fixer proposes only during Fix all, so no row carries a
            // proposed value; this sets one to show the "Proposed by the AI fixer" state.
            Action<PanelItem> proposal = i =>
            {
                i.AiFix = true;
                i.Prop = "jkrMT_ST_Putih";
                i.AiReason = new Text("Padanan terdekat dalam senarai bahan JKR: keluli bercat, kemasan putih.",
                                      "Closest match in the JKR material list: painted steel, white finish.");
            };
            Func<PanelItem, bool> material = i => noFixMed(i)
                && (i.Title.En + " " + i.El).IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0;
            DetailShot(dir, "jkr-detail-ai-proposal-en.png", material, tweak: proposal);
            DetailShot(dir, "jkr-detail-ai-proposal.png", material, tweak: proposal, en: false);
            DetailShot(dir, "jkr-detail-ai-can-en.png", noFixMed, tweak: i => i.AiFix = true);
            DetailShot(dir, "jkr-detail-ignored-en.png", fix, tab: PanelTab.Ignore, tweak: i => i.Status = ItemStatus.Ignored);
            DetailShot(dir, "jkr-detail-marked-en.png", fixMed, tweak: i => i.MarkedManual = true);
            DetailShot(dir, "jkr-detail-marked.png", fixMed, tweak: i => i.MarkedManual = true, en: false);
            DetailShot(dir, "jkr-detail-failed-en.png", fixMed, tweak: i => i.FixError = "Type name already used in this family.");
            DetailShot(dir, "jkr-detail-steps-en.png", noFixMed, steps: true);
            ConfirmShot(dir, "jkr-confirm-en.png", ai: 0, affected: 12);
            ConfirmShot(dir, "jkr-confirm-ai-en.png", ai: 3, affected: 40);
            ConfirmShot(dir, "jkr-confirm-pause-en.png", ai: 3, affected: JkrFixBatch.PauseWarningElementCount + 120);
            ConfirmShot(dir, "jkr-confirm-ai.png", ai: 3, affected: JkrFixBatch.PauseWarningElementCount + 120, en: false);
            FixResultShot(dir, "jkr-fixresult-en.png", applied: 42);
            FixResultShot(dir, "jkr-fixresult-allwritten-en.png", applied: 42, failures: false);
            FixResultShot(dir, "jkr-fixresult-nothing-en.png", applied: 0);
            FixResultShot(dir, "jkr-fixresult.png", applied: 42, en: false);
            BorangPreviewShot(dir, "jkr-borang-preview-en.png", en: true, menu: false);
            BorangPreviewShot(dir, "jkr-borang-preview-menu-en.png", en: true, menu: true);
            BorangPreviewShot(dir, "jkr-borang-preview.png", en: false, menu: false);
            BorangPreviewShot(dir, "jkr-borang-preview-menu.png", en: false, menu: true);
            BorangPreviewShot(dir, "jkr-borang-preview-zoom-en.png", en: true, menu: false, zoom: true);
            ExportShot(dir, "jkr-export-en.png", en: true, IssueExportFormat.Excel);
            ExportShot(dir, "jkr-export-csv.png", en: false, IssueExportFormat.Csv);
        }

        /// <summary>The Borang BIM010 preview: the PDF's own pages for a sample run, with the
        /// Export issues menu open or closed.</summary>
        private static void BorangPreviewShot(string dir, string file, bool en, bool menu, bool zoom = false)
        {
            if (Skip(file)) return;
            try
            {
                PaneLanguage.Set(en ? PanelStrings.En : PanelStrings.Ms);
                var doc = BorangDoc.Build(
                    AuditSummary.Build(new[]
                    {
                        new Bim010Cell { Key = "A1", Verdict = Bim010Verdict.Fail },
                        new Bim010Cell { Key = "A2", Verdict = Bim010Verdict.Pass },
                        new Bim010Cell { Key = "B1", Verdict = Bim010Verdict.Fail, Note = "JKR_KodProjek empty on 112 elements." },
                        new Bim010Cell { Key = "C1.1", Verdict = Bim010Verdict.Pass },
                    }),
                    new IssueVm[0],
                    new BorangHeader
                    {
                        Project = "BEde1A_p14-001", Model = BorangDoc.ModelLine("AR", "300"),
                        File = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01.rvt", Date = BorangDoc.DateOf(new DateTime(2026, 11, 10)),
                    });
                var pdf = BorangPdf.Pdf(doc);
                var pages = (IReadOnlyList<byte[]>)BorangPdf.Pages(doc, dpi: 150);
                var w = (Window)Activator.CreateInstance(typeof(BorangPreviewWindow), BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { pdf, pages, null, "BIM010_model.pdf", doc.Header.File, 99, (Func<IssueExportFormat, string>)(_ => null), (Action<string>)null }, null);
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.ShowInTaskbar = false; w.Left = -4000; w.Top = -4000;
                w.Show();
                Settle(500);
                if (zoom)
                {
                    var m = typeof(BorangPreviewWindow).GetMethod("SetZoom", BindingFlags.Instance | BindingFlags.NonPublic);
                    m.Invoke(w, new object[] { 2.0 });
                    Settle(300);
                }
                if (menu) { ((Button)w.FindName("IssuesBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(300); }
                Save((FrameworkElement)w.Content, Path.Combine(dir, file), (int)Math.Ceiling(w.ActualHeight));
                w.Close();
                Console.WriteLine("wrote " + file);
            }
            catch (Exception ex) { Console.WriteLine(file + ": failed — " + ex); }
        }

        /// <summary>The export dialog over a sample issue list, with a format preselected.</summary>
        private static void ExportShot(string dir, string file, bool en, IssueExportFormat format)
        {
            if (Skip(file)) return;
            try
            {
                PaneLanguage.Set(en ? PanelStrings.En : PanelStrings.Ms);
                var vm = new PanelVm();
                foreach (var i in new[] { ("1", IssueStatus.Open), ("2", IssueStatus.Open), ("3", IssueStatus.Fixed), ("4", IssueStatus.Ignored) })
                    vm.Issues.Add(new IssueVm { Id = i.Item1, Title = "Sample " + i.Item1, Status = i.Item2 });
                var ew = new ExportWindow(vm, format); ew.Width = 460; RenderWindow(ew, dir, file);
            }
            catch (Exception ex) { Console.WriteLine(file + ": failed — " + ex); }
        }

        /// <summary>The detail window over the failing run, at the first issue that
        /// matches. The detail is opened on the core only (not Detail.Open), so the
        /// shell does not place its own window over the screen.</summary>
        private static void DetailShot(string dir, string file, Func<PanelItem, bool> pick, bool en = true,
                                       PanelTab tab = PanelTab.Open, Action<PanelItem> tweak = null, bool steps = false)
        {
            if (Skip(file)) return;
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
                shell.Model.IsEn = en;
                ShowRun(shell, Failing);
                Settle(300);
                var item = shell.Core.Issues.FirstOrDefault(pick);
                if (item == null) { Console.WriteLine(file + ": no matching issue in " + Failing); return; }
                tweak?.Invoke(item);
                if (steps)
                    shell.Detail.StepsOf = id => en
                        ? new[] { "Select the element in the model (Locate in 3D).", "Open its type properties and set the value the Borang requires.", "Re-scan to confirm." }
                        : new[] { "Pilih elemen dalam model (Cari dalam 3D).", "Buka sifat jenisnya dan tetapkan nilai yang Borang perlukan.", "Imbas semula untuk sahkan." };
                shell.Results.SelectTab(tab);
                shell.Core.Open(item.Id);
                RenderWindow(NewWindow<DetailWindow>(shell.Detail), dir, file);
            }
            catch (Exception ex) { Console.WriteLine(file + ": failed — " + ex); }
            finally { host.Close(); }
        }

        /// <summary>The confirm dialog with the failing run's first ready-made fixes and,
        /// with <paramref name="ai"/> &gt; 0, that many AI fixer proposals (harness sample:
        /// the proposals come from the backend during Fix all). From
        /// <paramref name="affected"/> 500 the "Revit may pause" note shows.</summary>
        private static void ConfirmShot(string dir, string file, int ai, int affected, bool en = true)
        {
            if (Skip(file)) return;
            try
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", Failing)));
                var response = json["response"].ToObject<ModelCheckResponse>(JsonSerializer.CreateDefault());
                var rows = IssueMapper.MapAll(response)
                    .Where(i => i.AutoFixable && !string.IsNullOrEmpty(i.FixAction))
                    .Take(4)
                    .Select(JkrConfirmRow.FromIssue)
                    .ToList();
                var samples = new[]
                {
                    new JkrConfirmRow("Railings", "Material not JKR-coded", "Metal White", "jkrMT_ST_Putih", 88213, isAiProposal: true),
                    new JkrConfirmRow("Furniture", "Material not JKR-coded", "Wood - Oak", "jkrMT_KY_Oak", 90544, isAiProposal: true),
                    new JkrConfirmRow("Doors", "Missing type parameter: Kod Jenis JKR", "", "PT1", 204511, isAiProposal: true),
                };
                rows.AddRange(samples.Take(ai));
                var model = new JkrConfirmModel(JkrConfirmKind.Fix, rows, affected, en ? PanelStrings.En : PanelStrings.Ms);
                RenderWindow(NewWindow<JkrConfirmWindow>(model), dir, file);
            }
            catch (Exception ex) { Console.WriteLine(file + ": failed — " + ex); }
        }

        /// <summary>The fix outcome: <paramref name="applied"/> written and, unless
        /// <paramref name="failures"/> is false, failures in three reasons (category
        /// without JKR parameters, model group, a reason Revit gave).</summary>
        private static void FixResultShot(string dir, string file, int applied, bool failures = true, bool en = true)
        {
            if (Skip(file)) return;
            try
            {
                var lang = en ? PanelStrings.En : PanelStrings.Ms;
                var r = new RenameResult();
                void Param(long id, string param, string why) =>
                    r.RecordFailure(RenameResult.MakeFixKey("set_parameter", id, param), id, PaneText.ParamWhat(param, id, lang), why);
                if (failures)
                {
                    for (var i = 0; i < 7; i++)
                        Param(578862, "P" + i + "_jkr_stt", $"Parameter 'P{i}_jkr_stt' not found on element 578862. Element category does not accept bound parameters.");
                    foreach (var id in new long[] { 901102, 901105, 930585 })
                        Param(id, "LOD_jkr_sit", $"Element {id} is inside a model group — Revit blocks instance parameter edits outside group edit mode.");
                    Param(921291, "Bahan_jkr_stm", "The value is outside the range Revit allows.");
                }
                var labels = new Dictionary<long, string>
                {
                    [578862] = "Generic Models · Bollard 600",
                    [901102] = "Walls · jkrAR_DD_Bata_115",
                    [901105] = "Walls · jkrAR_DD_Bata_115",
                    [930585] = "Doors · jkrAR_PT2p_900x2100",
                    [921291] = "Railings · jkrAR_RL_900",
                };
                var model = new FixResultModel(r, applied, fixAll: true, id => labels.TryGetValue(id, out var l) ? l : null, lang);
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
        private static void ShowRun(JkrPaneShell shell, string file, bool backendDown = false, Action<List<IssueVm>> tweak = null)
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

            tweak?.Invoke(issues);
            shell.Core.SetLod("300");
            shell.Core.BeginExternalRun();
            shell.Results.Load(issues, summary, spec);
            shell.Core.FinishExternalRun();
        }

        /// <summary>Issue-list samples on top of a run: the first ready-made fix is marked as
        /// manual (Mark as Manual, option b), the second is resolved, and the first issue with
        /// no fix becomes an AI fixer candidate (a backend check on a real element) and is put
        /// first, so the shots show a Manual tag, a Resolved row and the spark-bolt mark.</summary>
        private static void IssueSamples(List<IssueVm> issues)
        {
            var fixes = issues.Where(i => !i.NeedsPerson && i.AutoFixable && i.Status == IssueStatus.Open).ToList();
            if (fixes.Count > 0) fixes[0].Status = IssueStatus.ManualFixNeeded;
            if (fixes.Count > 1) fixes[1].Status = IssueStatus.Fixed;
            var ai = issues.FirstOrDefault(i => i.AiFixCandidate)
                     ?? issues.FirstOrDefault(i => !i.NeedsPerson && !i.AutoFixable && i.Status == IssueStatus.Open);
            if (ai == null) return;
            if (!ai.AiFixCandidate) { ai.CheckId = "element.material.02"; ai.RevitElementId = 412907; }
            issues.Remove(ai);
            issues.Insert(0, ai);
            if (fixes.Count > 0) { issues.Remove(fixes[0]); issues.Insert(1, fixes[0]); }
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

        private static void Shot(string dir, string file, Action<JkrPaneShell> configure, int height = 1100, int width = (int)JkrPaneShell.DesignWidth)
        {
            if (Skip(file)) return;
            var shell = new JkrPaneShell(new Grid());
            var win = new Window
            {
                Width = width,
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

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        private struct POINT { public int X, Y; }

        /// <summary>Real mouse clicks (SetCursorPos + mouse_event) on the pane, shown on screen
        /// at 652 px: an issue row must open the detail on the first click, the tabs and the
        /// pill × must act on the first click, also after the content was scrolled and after
        /// the window was deactivated. The cursor is put back afterwards.</summary>
        public static bool ClickCheck()
        {
            GetCursorPos(out var saved);
            ActiveModelSource.Set(new ActiveModelInfo(Title, @"C:\Projects\Dewan\" + Title + ".rvt", false));
            var shell = new JkrPaneShell(new Grid());
            var win = new Window
            {
                Width = JkrPaneShell.DesignWidth, Height = 652, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                Left = 40, Top = 40, ResizeMode = ResizeMode.NoResize, Topmost = true, Content = shell,
            };
            var other = new Window { Width = 200, Height = 120, Left = 500, Top = 40, Topmost = true, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            var ok = true;
            void Check(string what, bool pass) { Console.WriteLine((pass ? "PASS " : "FAIL ") + what); ok &= pass; }
            try
            {
                win.Show(); win.Activate(); Settle(300);
                shell.Model.IsEn = true;
                ShowRun(shell, Failing, backendDown: true);
                Settle(500);
                shell.UpdateLayout();
                var body = Find<ScrollViewer>(shell, "BodyScroll");
                Console.WriteLine($"stage {shell.Core.Stage}, scrollviewers {string.Join(",", FindAll<ScrollViewer>(shell).Select(v => v.Name))}");
                body.ScrollChanged += (_, e) => { if (e.VerticalChange != 0) Console.WriteLine($"  scroll {e.VerticalOffset - e.VerticalChange:0} -> {e.VerticalOffset:0}"); };
                var opened = new List<string>();
                shell.Results.OpenRequested += id => opened.Add(id);
                shell.Detail.Opened += () => { };

                void Click(FrameworkElement el, string what)
                {
                    // the test's own scroll, before the mouse goes down (BringIntoView is the pane's to refuse)
                    if (body.IsAncestorOf(el))
                    {
                        var top = el.TransformToAncestor(body).Transform(new Point(0, 0)).Y;
                        if (top < 0 || top + el.ActualHeight > body.ViewportHeight) { body.ScrollToVerticalOffset(body.VerticalOffset + top - 40); Settle(200); }
                    }
                    el.UpdateLayout();
                    var p = el.PointToScreen(new Point(Math.Min(40, el.ActualWidth / 2), el.ActualHeight / 2));
                    Console.WriteLine($"click {what} at {p.X:0},{p.Y:0} (offset {body.VerticalOffset:0}, focus {Keyboard.FocusedElement?.GetType().Name})");
                    SetCursorPos((int)p.X, (int)p.Y); Settle(80);
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Settle(80);
                    SetCursorPos((int)p.X + 1, (int)p.Y); Settle(40);   // a hand moves a pixel while pressing
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); Settle(400);
                }
                Button TabButton(string label) => FindAll<Button>(shell).FirstOrDefault(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "") == label && b.IsVisible);
                Button RowButton(int i)
                {
                    var list = Find<ItemsControl>(shell, "RowsList");
                    var c = list?.ItemContainerGenerator.ContainerFromIndex(i) as DependencyObject;
                    return c == null ? null : FindAll<Button>(c).FirstOrDefault();
                }
                void CloseDetail() { foreach (var w in Application.Current.Windows.OfType<DetailWindow>().ToList()) w.Close(); Settle(300); win.Activate(); Settle(200); }

                // 1. Issues tab, first click
                Click(TabButton(shell.Results.IssuesTabLabel), "Issues tab");
                Check("Issues tab opens the issues on the first click", shell.Results.ShowList);
                Settle(300);
                // 2. a row, first click → detail
                for (var i = 0; i < 3; i++)
                {
                    var before = opened.Count;
                    var row = RowButton(i);
                    if (row == null) { Check("row " + i + " found", false); continue; }
                    Click(row, "row " + i);
                    Check("row " + i + " opens the detail on the first click", opened.Count == before + 1);
                    CloseDetail();
                }
                // 3. after deactivation, a row
                other.Show(); other.Activate(); Settle(300);
                var n = opened.Count;
                Click(RowButton(1), "row 1 after the pane lost focus");
                Check("row opens after the pane lost focus (first click)", opened.Count == n + 1);
                CloseDetail();
                // 4. Checklist tab, then scroll the overview, then Issues tab
                Click(TabButton(shell.Results.ChecklistTabLabel), "Checklist tab");
                Check("Checklist tab on the first click", shell.Results.ShowChecklist);
                body.ScrollToVerticalOffset(250); Settle(300);
                Click(TabButton(shell.Results.IssuesTabLabel), "Issues tab after scrolling the overview");
                Check("Issues tab after scrolling, first click", shell.Results.ShowList);
                // 5. as in the live retest, every step by a click and the pane losing focus in
                //    between (Revit takes it back): section D row, "n issues in this section",
                //    the pill ×, then a row.
                void Away() { other.Activate(); Settle(250); }
                Click(TabButton(shell.Results.ChecklistTabLabel), "Checklist tab");
                Away();
                Click(FindAll<Button>(shell).First(b => b.IsVisible && (b.Tag as string) == "D"), "section D row");
                Check("section D opens on the first click", shell.Results.IsFocused);
                Away();
                Click(FindAll<Button>(shell).First(b => b.IsVisible && (System.Windows.Automation.AutomationProperties.GetName(b) ?? "") == shell.Results.SectionIssuesLabel), "n issues in this section");
                Check("the section's issues open on the first click", shell.Results.ShowList && shell.Results.HasCat);
                Away();
                Click(Find<Button>(shell, "ClearCatX"), "pill ×");
                Check("pill × clears the filter on the first click", !shell.Results.HasCat);
                Away();
                n = opened.Count;
                Click(RowButton(0), "row 0 after the pill");
                Check("row opens on the first click after the pill", opened.Count == n + 1);
                CloseDetail();
                Away();
                n = opened.Count;
                Click(RowButton(2), "row 2 after the detail closed");
                Check("row opens on the first click after the detail closed", opened.Count == n + 1);
                // 6. live R2: the pane activated again restores keyboard focus to an element now out
                //    of view (here the Checklist tab, the list scrolled down). That must not scroll
                //    the content: the press would land on one row and the release on another.
                body.ScrollToVerticalOffset(900); Settle(300);
                var before6 = body.VerticalOffset;
                Click(RowButton(RowIndexAtTop(shell, body)), "a row (mouse input)");
                CloseDetail();
                body.ScrollToVerticalOffset(900); Settle(300);
                before6 = body.VerticalOffset;
                Keyboard.Focus(TabButton(shell.Results.ChecklistTabLabel)); Settle(300);
                Check($"a focus restore after a click does not scroll ({before6:0} -> {body.VerticalOffset:0})", Math.Abs(body.VerticalOffset - before6) < 0.5);
                // 6b. the same as it happens in Revit: the press activates the pane, the dock gives
                //     keyboard focus back to the element focused last (out of view), then the button
                //     takes it. The row must still open on this first click (live R1).
                body.ScrollToVerticalOffset(900); Settle(300);
                Keyboard.Focus(FindAll<ResultsView>(shell).First()); Settle(100);
                MouseButtonEventHandler restore = null;
                restore = (_, __) => { shell.PreviewMouseLeftButtonDown -= restore; Keyboard.Focus(TabButton(shell.Results.ChecklistTabLabel)); shell.UpdateLayout(); };
                shell.PreviewMouseLeftButtonDown += restore;
                n = opened.Count;
                Click(RowButton(RowIndexAtTop(shell, body) + 1), "a row while focus is restored to the Checklist tab");
                Check("the row opens on the first click while the pane gets focus back", opened.Count == n + 1);
                CloseDetail();
                // 7. the list keys still bring the selection into view
                var rv = FindAll<ResultsView>(shell).First();
                body.ScrollToVerticalOffset(0); Settle(200);
                Keyboard.Focus(rv); Settle(200);
                for (var k = 0; k < 9; k++) { keybd_event(0x4A, 0, 0, UIntPtr.Zero); keybd_event(0x4A, 0, 2, UIntPtr.Zero); Settle(120); }
                Settle(300);
                var selRow = Find<ItemsControl>(shell, "RowsList").ItemContainerGenerator.ContainerFromIndex(shell.Core.Sel) as FrameworkElement;
                var y = selRow.TransformToAncestor(body).Transform(new Point(0, 0)).Y;
                Check($"J moves the selection ({shell.Core.Sel}) and keeps it in view (top {y:0}, viewport {body.ViewportHeight:0})",
                      shell.Core.Sel >= 8 && y >= 0 && y + selRow.ActualHeight <= body.ViewportHeight + 1);
            }
            catch (Exception ex) { Console.WriteLine("click check failed — " + ex); ok = false; }
            finally
            {
                SetCursorPos(saved.X, saved.Y);
                other.Close(); win.Close();
            }
            Console.WriteLine(ok ? "CLICK CHECK PASSED" : "CLICK CHECK FAILED");
            return ok;
        }

        /// <summary>Index of the first issue row whose top is in the viewport.</summary>
        private static int RowIndexAtTop(DependencyObject shell, ScrollViewer body)
        {
            var list = Find<ItemsControl>(shell, "RowsList");
            for (var i = 0; i < list.Items.Count; i++)
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c && c.TransformToAncestor(body).Transform(new Point(0, 0)).Y >= 0) return i;
            return 0;
        }

        private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in FindAll<T>(c)) yield return d;
            }
        }

        /// <summary>Scroll the named ScrollViewer of the shown screen (double.MaxValue = to the end).</summary>
        private static void ScrollTo(DependencyObject root, string name, double offset)
        {
            var sv = Find<ScrollViewer>(root, name);
            if (sv == null) { Console.WriteLine("no ScrollViewer " + name); return; }
            sv.UpdateLayout();
            if (offset >= double.MaxValue) sv.ScrollToEnd(); else sv.ScrollToVerticalOffset(offset);
            sv.UpdateLayout();
        }

        private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root == null) return null;
            if (root is T t && t.Name == name) return t;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var hit = Find<T>(VisualTreeHelper.GetChild(root, i), name);
                if (hit != null) return hit;
            }
            return null;
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
