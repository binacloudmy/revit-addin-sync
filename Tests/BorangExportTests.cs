using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 14: the Borang Model Audit BIM010 export (frames e-p1, e-sum;
    /// borang-export-template.html). Marks come from the run's BIM010 summary —
    /// every row, passing ones included — remarks from the issues; three-state
    /// rule; the AI % and the manual count kept apart.
    /// </summary>
    public class BorangExportTests
    {
        private static Bim010Cell C(string key, Bim010Verdict v, string note = "", string col = "") =>
            new Bim010Cell { Key = key, Verdict = v, Note = note, Column = col };

        private static IssueVm Issue(string id, string key, string title, IssueStatus st = IssueStatus.Open,
                                     bool human = false, string desc = "", string checkId = "element.x")
            => new IssueVm
            {
                Id = id, BorangKey = key, Title = title, Description = desc, Status = st, CheckId = checkId,
                Verification = human ? IssueVerification.Human : IssueVerification.Ai,
                Element = new ElementRef { Id = "1", Name = "x" }, Spec = new SpecRef { Doc = "" },
            };

        private static Bim010Summary Sample() => AuditSummary.Build(new[]
        {
            C("A1", Bim010Verdict.Fail), C("A1", Bim010Verdict.Manual, "Waterproofing is not modelled as an identifiable layer."),
            C("A2", Bim010Verdict.Pass), C("A3", Bim010Verdict.Manual, "Whether the split is appropriate is a human call."),
            C("A4", Bim010Verdict.Pass), C("A5", Bim010Verdict.Fail), C("A6", Bim010Verdict.Fail), C("A7", Bim010Verdict.Fail, "Material not using JKR naming"),
            C("B1", Bim010Verdict.Fail, "JKR_KodProjek empty on 112 elements."),
            C("C1.1", Bim010Verdict.Pass), C("C1.2", Bim010Verdict.Pass), C("C1.3", Bim010Verdict.Fail, "PBT views absent."),
            C("C1.4", Bim010Verdict.Pass), C("C1.5", Bim010Verdict.Pass), C("C1.6", Bim010Verdict.Manual, "Zoning analysis is a reviewer judgement."),
            C("C2.1", Bim010Verdict.Pass), C("C3.1", Bim010Verdict.Fail, "Schedule of Accommodation not generated."),
            C("C4.1", Bim010Verdict.Fail, "Project title blank on 12 sheets."), C("C4.2", Bim010Verdict.Fail, "Project title blank on 12 sheets."),
            C("C5.1", Bim010Verdict.Manual, "No revision stamp to compare the links."),
            C("D2", Bim010Verdict.Fail, "Type \"Generic - 200mm\" outside the JKR template.", Bim010Column.Naming),
            C("D2", Bim010Verdict.Pass, "", Bim010Column.Geometry),
            C("D8", Bim010Verdict.Pass, "", Bim010Column.Naming), C("D8", Bim010Verdict.Manual, "Catatan Rekabentuk wording is a content judgement.", Bim010Column.Information),
            C("D10", Bim010Verdict.Fail, "", Bim010Column.Naming), C("D10", Bim010Verdict.Pass, "", Bim010Column.Quality),
            C("D1", Bim010Verdict.Pass, "", Bim010Column.Naming), C("D23", Bim010Verdict.Pass, "", Bim010Column.Naming),
            C("E1.1", Bim010Verdict.Pass), C("E1.2", Bim010Verdict.Fail, "BOMBA view uses PBT template"),
        });

        private static IssueVm[] SampleIssues() => new[]
        {
            Issue("1", "A1", "Single door below minimum width"),
            Issue("2", "A1", "Flat roof waterproofing", human: true, desc: "Waterproofing is not modelled as an identifiable layer. The AI cannot determine whether it exists."),
            Issue("3", "A5", "Grid sequence has a gap", IssueStatus.Fixed),
            Issue("4", "A6", "Slab with no room", IssueStatus.Ignored),
            Issue("5", "D10", "Door type name does not follow JKR naming"),
        };

        private static BorangDoc Build() => BorangDoc.Build(Sample(), SampleIssues(), new BorangHeader
        {
            Project = BorangDoc.ProjectName("Project Name", "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a.rvt"),
            Model = BorangDoc.ModelLine("AR", "300"),
            File = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a.rvt",
            Date = BorangDoc.DateOf(new DateTime(2026, 11, 10)),
        });

        [Fact]
        public void Every_row_of_the_form_is_there_in_template_order()
        {
            var d = Build();
            Assert.Equal(7, d.SecA.Count);
            Assert.Single(d.SecB);
            Assert.Equal(new[] { "1.1", "1.2", "1.3", "1.4", "1.5", "1.6" }, d.CViews.Select(r => r.No));
            Assert.Equal(2, d.CLegends.Count);
            Assert.Equal(3, d.CSched.Count);
            Assert.Equal(10, d.CSheets.Count);
            Assert.Equal(23, d.SecD.Count);
            Assert.Equal("Pipes — RWDP", d.SecD[19].Cat);
            Assert.Equal(new[] { "1.1", "1.2" }, d.SecE.Select(r => r.No));
            Assert.StartsWith("Architectural Model files are named according to the PWD’s BIM Guidelines and Standards. Please specify", d.SecA[1].Text);
        }

        [Fact]
        public void Three_state_marks_and_a_person_decides_is_never_ticked()
        {
            var d = Build();
            Assert.Equal(("", "X"), (d.SecA[0].Yes, d.SecA[0].No2));             // A1 fail (+ manual)
            Assert.Equal(("√", ""), (d.SecA[1].Yes, d.SecA[1].No2));        // A2 pass
            Assert.Equal(("√", ""), (d.SecA[2].Yes, d.SecA[2].No2));              // A3 a person decides: demo, no Fail → tick
            Assert.Equal("", d.SecA[2].Remark);                                    // demo: no SEMAK MANUAL text
            Assert.Equal(("√", ""), (d.C51.Yes, d.C51.No2));   // demo: no Fail → tick
            Assert.Equal(("√", ""), (d.SecE[0].Yes, d.SecE[0].No2));
            Assert.Equal(("", "X"), (d.SecE[1].Yes, d.SecE[1].No2));
        }

        [Fact]
        public void A_marked_manual_issue_prints_as_handled_manually_not_ignored()
        {
            var issues = SampleIssues().ToList();
            issues[2] = Issue("3", "A5", "Grid sequence has a gap", IssueStatus.ManualFixNeeded);
            var d = BorangDoc.Build(Sample(), issues, new BorangHeader());
            Assert.EndsWith("(handled manually)", d.SecA[4].Remark);
            Assert.DoesNotContain("ignored by user", d.SecA[4].Remark);
            Assert.Equal(("", "X"), (d.SecA[4].Yes, d.SecA[4].No2));     // still not compliant
        }

        [Fact]
        public void Remarks_list_what_failed_with_its_state_then_what_a_person_decides()
        {
            var d = Build();
            Assert.Equal("Single door below minimum width",
                         d.SecA[0].Remark);
            Assert.Equal(BorangDoc.Red, d.SecA[0].RemarkColor);
            Assert.Equal("Grid sequence has a gap (fixed, not yet re-scanned)", d.SecA[4].Remark);
            Assert.Equal(("", "X"), (d.SecA[4].Yes, d.SecA[4].No2));              // fixed ≠ passed until re-scan
            Assert.Equal("Slab with no room (ignored by user)", d.SecA[5].Remark);
            Assert.Equal(("", "X"), (d.SecA[5].Yes, d.SecA[5].No2));              // ignored still prints as not comply
            Assert.Equal("Material not using JKR naming", d.SecA[6].Remark);       // no issue: the summary's note
            Assert.Equal("", d.SecA[1].Remark);
        }

        [Fact]
        public void Section_d_marks_each_aspect_and_a_person_decides_shows_a_dash()
        {
            var d = Build();
            var walls = d.SecD[1];
            Assert.Equal(("n", "y", "y", "y"), (walls.N, walls.Q, walls.I, walls.G));   // demo: blank columns tick
            var room = d.SecD[7];
            Assert.Equal(("y", "y"), (room.N, room.I));   // demo: no Fail → tick
            Assert.Equal(BorangDoc.Tick, BorangDoc.DGlyph(walls.Q));               // demo: not assessed ticks
            Assert.Equal("Door type name does not follow JKR naming", d.SecD[9].Remark);
            Assert.Equal(("y", "y", "y", "y"), (d.SecD[2].N, d.SecD[2].Q, d.SecD[2].I, d.SecD[2].G));   // no cells: demo ticks
        }

        [Fact]
        public void Items_checked_as_one_row_are_marked_on_their_numbered_row_only()
        {
            var d = Build();
            Assert.Equal("y", d.CLegendsHead.Mark);
            Assert.All(d.CLegends, r => Assert.Equal("", r.Mark));
            Assert.Equal("n", d.CSchedHead.Mark);
            Assert.Equal("Schedule of Accommodation not generated.", d.CSchedHead.Remark);
            Assert.All(d.CSched, r => Assert.True(r.Indent && r.Mark == ""));
            Assert.Equal("n", d.CSheetsHead.Mark);
            Assert.Equal("Time stamp should be included on the Sheets.", d.C42.TextItalic);
        }

        [Fact]
        public void The_summary_keeps_the_ai_percent_and_the_manual_count_apart()
        {
            var s = Sample();
            var d = Build();
            Assert.Equal(s.Pct.ToString(), d.SumPct);
            Assert.Equal(s.AiTotal.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), d.SumTotal);
            Assert.Equal(s.Pending, d.SumManual);
            Assert.Equal("—", BorangDoc.Build(null, null, null).SumPct);
        }

        [Fact]
        public void Header_lines()
        {
            Assert.Equal("BEde1A_p14-001", BorangDoc.ProjectName("Project Name", "jkrAR24_5a_(BEde1A_p14-001)_A1.rvt"));
            Assert.Equal("Klinik Kesihatan Tapah", BorangDoc.ProjectName("Klinik Kesihatan Tapah", "x.rvt"));
            Assert.Equal("plain", BorangDoc.ProjectName("", "plain.rvt"));
            Assert.Equal("Architecture · AR · LOD 300", BorangDoc.ModelLine("AR", "300"));
            Assert.Equal("ST · LOD 200", BorangDoc.ModelLine("ST", "200"));
            Assert.Equal("10.11.2026", BorangDoc.DateOf(new DateTime(2026, 11, 10)));
        }

        [Fact]
        public void A_long_list_of_issues_is_capped_in_the_remark()
        {
            var issues = Enumerable.Range(1, 9).Select(i => Issue(i.ToString(), "A7", "Problem " + i)).ToArray();
            var d = BorangDoc.Build(Sample(), issues, new BorangHeader());
            Assert.EndsWith("Problem 5; +4 more", d.SecA[6].Remark);
        }

        [Fact]
        public void The_pdf_renders_with_every_page_and_the_preview_images()
        {
            var doc = Build();
            var pdf = BorangPdf.Pdf(doc);
            Assert.True(pdf.Length > 5000);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
            var pages = BorangPdf.Pages(doc, dpi: 72);
            Assert.True(pages.Count >= 5);

            // Set BINA_BORANG_OUT to a folder to look at the result.
            var outDir = Environment.GetEnvironmentVariable("BINA_BORANG_OUT");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllBytes(Path.Combine(outDir, "borang.pdf"), pdf);
                var hi = BorangPdf.Pages(doc, dpi: 110);
                for (var i = 0; i < hi.Count; i++) File.WriteAllBytes(Path.Combine(outDir, $"page{i + 1}.png"), hi[i]);
            }
        }
    }

    /// <summary>
    /// The Borang with a spec-based summary (SpecBorang): the summary box and the
    /// Running screen show the Results screen's numbers, and the remarks follow the
    /// spec marks (an older checker's X on a row a person decides prints as semak
    /// manual). Here, not in SpecBorangTests, because IssueVm / ResultsScreenModel /
    /// BorangDoc do not build on the Mac harness.
    /// </summary>
    public class SpecBorangExportTests
    {
        public static IEnumerable<object[]> Samples() =>
            new[] { new object[] { "jkr_run_failing.json" }, new object[] { "jkr_run_compliant.json" } };

        private static IssueVm Vm(string id, string key, string title, string desc = "", IssueStatus st = IssueStatus.Open) => new IssueVm
        {
            Id = id, CheckId = id, BorangKey = key, Title = title, Description = desc, Status = st,
            Verification = IssueVerification.Ai, Element = new ElementRef { Id = "1", Name = "x" }, Spec = new SpecRef { Doc = "" },
        };

        [Theory]
        [MemberData(nameof(Samples))]
        public void The_Borang_summary_box_and_the_Running_screen_equal_the_Results_headline(string sample)
        {
            var results = SpecBorangTests.Results(SpecBorangTests.Run(sample));
            var report = SpecReport.Build(results);
            var summary = SpecBorang.Build(report, results);

            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var screen = new ResultsScreenModel(core, new StartScreenModel(core));
            screen.Load(new List<IssueVm>(), summary, report);

            var doc = BorangDoc.Build(summary, new List<IssueVm>(), null);
            Assert.Equal(screen.HeadPctLabel, doc.SumPct == BorangDoc.Dash ? "—" : doc.SumPct + "%");
            var (_, total) = SpecChecklist.Score(report);
            Assert.Equal(total.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), doc.SumTotal);
            Assert.Equal(screen.PendTotal, doc.SumManual);

            var run = RunScreenModel.FromSummary(summary);
            foreach (var tile in screen.Sections)
            {
                Assert.Equal(tile.PctLabel, run[tile.Key].Pct.HasValue ? run[tile.Key].Pct + "%" : "—");
                Assert.Equal(tile.PendLabel, run[tile.Key].Pend > 0 ? "+" + run[tile.Key].Pend : "");
            }
            // A row a person decides is never ticked on the printed form.
            foreach (var row in doc.SecA.Concat(doc.SecB).Concat(doc.CViews).Concat(doc.SecE).Where(r => r.Mark == "m"))
            {
                Assert.Equal("", row.Yes);
                Assert.Equal("", row.No2);
            }
        }

        [Fact]
        public void The_Department_checker_saying_no_is_printed_as_semak_manual()
        {
            var s = SpecBorangTests.Summary(new SpecResult
            {
                Source = SpecReport.Audit, IssueId = "BIM010-A6", CheckerId = "rooms_department",
                RowKey = "A6", Status = "fail", Note = "12 rooms have no Department", ElementCount = 12,
            });
            var doc = BorangDoc.Build(s, new[] { Vm("BIM010-A6", "A6", "A6 · Rooms", "12 rooms have no Department") }, null);
            var a6 = doc.SecA[5];
            Assert.Equal("y", a6.Mark);
            Assert.Equal("", a6.No2);
            Assert.Equal("", a6.Remark);   // demo: no SEMAK MANUAL text
        }

        [Fact]
        public void A_spec_X_lists_its_failing_issues_with_their_state_and_a_tick_has_no_remark()
        {
            var s = SpecBorangTests.Summary(
                new SpecResult { Source = SpecReport.Model, CheckId = "E.1", Status = "fail", Category = "PBT", IssueId = "pbt-1", Note = "PBT view X has no view template" },
                new SpecResult { Source = SpecReport.Model, CheckId = "E.1", Status = "pass", Category = "BOMBA" });
            var doc = BorangDoc.Build(s, new[]
            {
                Vm("pbt-1", "E1.2", "E.1 · View template", st: IssueStatus.Ignored),
                Vm("old-1", "E1.1", "E1.1 · old checker"),   // not behind the spec's √: not printed beside it
            }, null);
            Assert.Equal(BorangDoc.Tick, doc.SecE[0].Yes);
            Assert.Equal("", doc.SecE[0].Remark);
            Assert.Equal(BorangDoc.Cross, doc.SecE[1].No2);
            Assert.Equal("E.1 · View template (ignored by user)", doc.SecE[1].Remark);
            Assert.Equal(BorangDoc.Red, doc.SecE[1].RemarkColor);
        }

        [Fact]
        public void The_old_remark_path_is_unchanged_for_a_BIM010_cell_summary()
        {
            var old = AuditSummary.Build(new List<Bim010Cell> { new Bim010Cell { Key = "A2", Verdict = Bim010Verdict.Fail, Note = "bad name" } });
            var (text, color) = BorangDoc.RemarkFor("A2", "n", old, new List<IssueVm>());
            Assert.Equal("bad name", text);
            Assert.Equal(BorangDoc.Red, color);
        }
    }
}
