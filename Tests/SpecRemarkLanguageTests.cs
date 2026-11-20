using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Mcp.Tools.Audit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace RevitWebAppSync.Tests
{
    [CollectionDefinition("PaneLanguage", DisableParallelization = true)]
    public class PaneLanguageCollection { }

    /// <summary>
    /// AI-421 (live check): the spec checks' OWN remarks follow the language. The real spec
    /// evaluators run on the sample models; the English Borang and the English export rows
    /// must carry no Malay remark words, and the BM ones stay BM. The Borang is English
    /// only, whatever the pane's language, so the issues are built in BM (as a BM scan does).
    /// </summary>
    [Collection("PaneLanguage")]
    public class SpecRemarkLanguageTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _lang = PaneLanguage.Current;
        public SpecRemarkLanguageTests(ITestOutputHelper o) { _out = o; }
        public void Dispose() => PaneLanguage.Set(_lang);

        private static SpecModel Model(string file)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)));
            return json["model"].ToObject<SpecModel>(JsonSerializer.CreateDefault());
        }

        // Malay words the spec remarks use (whole words, case-insensitive). The fixed HOD template
        // text and the "SEMAK MANUAL" label are not in the remark cells' scope: the label is
        // removed before the scan.
        private static readonly Regex Malay = new Regex(
            @"\b(ada|tiada|pada|elemen|kosong|dikesan|nama fail|ketinggian|saiz|amaran|setiap|bilik|dengan|dalam|tidak|atau|dan|yang|untuk|dari|kod|nilai|isi|keperluan|peraturan|ketinggian siling|saiz pintu|pintu utama|pagar hiasan|sistem bumbung|jadual|senarai|sahkan|menutup|semakan|jumpa|dijumpai|sepatutnya|bahan|nama|sistem|belum|mesti|perlu|lagi)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string StripLabel(string s) => (s ?? "").Replace("SEMAK MANUAL", "").Replace("Semak manual", "").Replace("semak manual", "");

        public static IEnumerable<object[]> Samples() => new[] { new object[] { "jkr_run_failing.json" }, new object[] { "jkr_run_compliant.json" } };

        private static List<string> BorangRemarks(BorangDoc d)
        {
            var rows = d.SecA.Concat(d.SecB).Concat(d.CViews).Concat(d.CLegends).Concat(d.CSched).Concat(d.CSheets)
                .Concat(d.SecE).Concat(new[] { d.CLegendsHead, d.CSchedHead, d.CSheetsHead, d.C42, d.C51 }.Where(r => r != null));
            return rows.Select(r => r.Remark).Concat(d.SecD.Select(r => r.Remark)).Where(t => !string.IsNullOrEmpty(t)).ToList();
        }

        private static (List<SpecResult> results, List<IssueVm> issuesBm, Bim010Summary summary) Run(string file)
        {
            var results = SpecChecks.Run(Model(file));
            var summary = SpecBorang.Build(SpecReport.Build(results), results);
            PaneLanguage.Set(PanelStrings.Ms);                       // a BM scan: issues are built in BM
            var issues = IssueMapper.FromModelResults(results, PanelStrings.Ms);
            return (results, issues, summary);
        }

        [Theory, MemberData(nameof(Samples))]
        public void English_Borang_has_no_Malay_remark_words(string file)
        {
            var (_, issues, summary) = Run(file);
            PaneLanguage.Set(PanelStrings.En);
            var doc = BorangDoc.Build(summary, issues, null);
            var bad = BorangRemarks(doc).Where(t => Malay.IsMatch(StripLabel(t))).ToList();
            foreach (var b in bad) _out.WriteLine(b);
            Assert.True(bad.Count == 0, "Malay in the English Borang:\n" + string.Join("\n", bad));
        }

        [Theory, MemberData(nameof(Samples))]
        public void English_export_rows_have_no_Malay_in_Required_and_Actual(string file)
        {
            var (_, issues, _) = Run(file);
            PaneLanguage.Set(PanelStrings.En);
            var bad = new List<string>();
            foreach (var i in issues)
            {
                var c = IssueExportRows.Cells(i);
                foreach (var cell in new[] { c[5], c[6] })
                    if (Malay.IsMatch(StripLabel(cell))) bad.Add(i.Id + ": " + cell);
            }
            foreach (var b in bad) _out.WriteLine(b);
            Assert.True(bad.Count == 0, "Malay in the English export:\n" + string.Join("\n", bad));
        }

        [Theory, MemberData(nameof(Samples))]
        public void Every_spec_result_with_a_Malay_note_has_an_English_note(string file)
        {
            var bad = SpecChecks.Run(Model(file))
                .Where(r => Malay.IsMatch(StripLabel(r.Note)) && string.IsNullOrWhiteSpace(r.NoteEn))
                .Select(r => r.CheckId + ": " + r.Note).Distinct().ToList();
            Assert.True(bad.Count == 0, "No English note for:" + Environment.NewLine + string.Join(Environment.NewLine, bad));
        }

        [Theory, MemberData(nameof(Samples))]
        public void Every_spec_result_with_a_Malay_Required_or_Actual_has_an_English_one(string file)
        {
            var bad = new List<string>();
            foreach (var r in SpecChecks.Run(Model(file)))
            {
                if (Malay.IsMatch(r.Required ?? "") && string.IsNullOrWhiteSpace(r.RequiredEn)) bad.Add(r.CheckId + " required: " + r.Required);
                if (Malay.IsMatch(r.Actual ?? "") && string.IsNullOrWhiteSpace(r.ActualEn)) bad.Add(r.CheckId + " actual: " + r.Actual);
            }
            Assert.True(bad.Count == 0, "No English for:" + Environment.NewLine + string.Join(Environment.NewLine, bad.Distinct()));
        }

        [Fact]
        public void Legacy_checker_issue_exports_its_rule_in_the_panes_language()
        {
            var i = new IssueVm { Id = "BIM010-A6", Title = "A6 · Rooms", Required = "setiap bilik (Room) ada nilai parameter 'Department'" };
            PaneLanguage.Set(PanelStrings.En);
            Assert.Equal("every room (Room) has a value in the 'Department' parameter", IssueExportRows.Cells(i)[5]);
            PaneLanguage.Set(PanelStrings.Ms);
            Assert.Equal(i.Required, IssueExportRows.Cells(i)[5]);
        }

        [Fact]
        public void Bm_mode_stays_Malay()
        {
            var (_, issues, _) = Run("jkr_run_failing.json");
            PaneLanguage.Set(PanelStrings.Ms);
            Assert.Contains(issues, i => Malay.IsMatch(i.Description ?? ""));
            Assert.Contains(issues.Select(IssueExportRows.Cells), c => Malay.IsMatch(c[5] + " " + c[6]));
        }
    }
}
