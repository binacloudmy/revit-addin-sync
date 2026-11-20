using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Mcp.Tools.Audit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services;
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

        // Names the sample models give their own things (levels, links, sheets, views, schedules, families,
        // grids, parameter values ...) are data, not remark wording: exactly those strings are not scanned.
        // Nothing else is skipped: a quoted sentence or text between two apostrophes is still read.
        private static readonly Lazy<string[]> ModelNames = new Lazy<string[]>(() =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in new[] { "jkr_run_failing.json", "jkr_run_compliant.json" })
            {
                var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)));
                foreach (var t in json["model"].SelectTokens("$..*").OfType<JValue>())
                    if (t.Type == JTokenType.String && t.ToString().Trim().Length >= 3) names.Add(t.ToString().Trim());
            }
            return names.OrderByDescending(n => n.Length).ToArray();
        });

        private static string StripLabel(string s)
        {
            var t = (s ?? "").Replace("SEMAK MANUAL", "").Replace("Semak manual", "").Replace("semak manual", "");
            foreach (var n in ModelNames.Value) t = t.Replace(n, "");
            return t;
        }

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
        public void English_Borang_has_no_Malay_remark_words_for_the_sample_models(string file)
        {
            var (_, issues, summary) = Run(file);
            PaneLanguage.Set(PanelStrings.En);
            var doc = BorangDoc.Build(summary, issues, null);
            var bad = BorangRemarks(doc).Where(t => Malay.IsMatch(StripLabel(t))).ToList();
            foreach (var b in bad) _out.WriteLine(b);
            Assert.True(bad.Count == 0, "Malay in the English Borang:\n" + string.Join("\n", bad));
        }

        [Theory, MemberData(nameof(Samples))]
        public void English_export_rows_have_no_Malay_in_Required_and_Actual_for_the_sample_models(string file)
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
        public void Every_spec_result_with_a_Malay_note_has_an_English_note_for_the_sample_models(string file)
        {
            var bad = SpecChecks.Run(Model(file))
                .Where(r => Malay.IsMatch(StripLabel(r.Note)) && string.IsNullOrWhiteSpace(r.NoteEn))
                .Select(r => r.CheckId + ": " + r.Note).Distinct().ToList();
            Assert.True(bad.Count == 0, "No English note for:" + Environment.NewLine + string.Join(Environment.NewLine, bad));
        }

        [Theory, MemberData(nameof(Samples))]
        public void Every_spec_result_with_a_Malay_Required_or_Actual_has_an_English_one_for_the_sample_models(string file)
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

        // ── structural: every non-empty BM text has a non-empty English twin ──

        // Values that read the same in both languages: numbers / units / ids / codes (no letters apart from
        // units and code-like tokens), or one of these explicit names (a Revit parameter, a mode word).
        private static readonly HashSet<string> Neutral = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Loaded", "NotFound", "solid core", "hollow core", "material", "Model",
            // model data / English already: a material name, Revit's default project value, a set name.
            "Glass (door)", "Enter address here", "template BOMBA", "template PBT", "template Dokumen",
        };

        private static bool IsNeutral(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0 || Neutral.Contains(s)) return true;
            // numbers, units, comparison signs, ids, element / family / sheet names and codes are data:
            // no lower-case word of 3+ letters standing apart from a code (jkrAR_…, PT1p_850x2100, 12345).
            return !Regex.IsMatch(s, @"(?<![\w_])[a-z]{3,}(?![\w_])");
        }

        private static void NeedsTwin(List<string> bad, string who, string field, string bm, string en)
        {
            if (!IsNeutral(bm) && string.IsNullOrWhiteSpace(en)) bad.Add(who + " " + field + ": " + bm);
        }

        [Theory, MemberData(nameof(Samples))]
        public void Every_SpecResult_text_has_an_English_twin(string file)
        {
            var bad = new List<string>();
            foreach (var r in SpecChecks.Run(Model(file)))
            {
                var who = r.CheckId;
                NeedsTwin(bad, who, "Note", r.Note, r.NoteEn);
                NeedsTwin(bad, who, "Actual", r.Actual, r.ActualEn);
                NeedsTwin(bad, who, "Required", r.Required, r.RequiredEn);
            }
            Assert.True(bad.Count == 0, "No English twin:" + Environment.NewLine + string.Join(Environment.NewLine, bad.Distinct()));
        }

        [Theory, MemberData(nameof(Samples))]
        public void Every_IssueVm_text_has_an_English_twin(string file)
        {
            var (_, issues, _) = Run(file);
            var bad = new List<string>();
            foreach (var i in issues)
            {
                NeedsTwin(bad, i.Id, "Title", i.Title, i.TitleEn);
                NeedsTwin(bad, i.Id, "Description", i.Description, i.DescriptionEn);
                NeedsTwin(bad, i.Id, "Required", i.Required, i.RequiredEn);
                NeedsTwin(bad, i.Id, "Actual", i.Actual, i.ActualEn);
            }
            Assert.True(bad.Count == 0, "No English twin:" + Environment.NewLine + string.Join(Environment.NewLine, bad.Distinct()));
        }

        // ── the audit snapshot keeps the English (restored Fixed issues) ──

        private static IssueVm FirstSpecIssueWithText(string file) =>
            Run(file).issuesBm.First(i => Malay.IsMatch(i.Description + " " + i.Title) && !string.IsNullOrEmpty(i.DescriptionEn));

        [Fact]
        public void Snapshot_round_trip_keeps_the_English_and_the_Borang_and_export_show_it()
        {
            var vm = FirstSpecIssueWithText("jkr_run_failing.json");
            var json = JsonConvert.SerializeObject(IssueSnapshot.From(vm));
            var back = JsonConvert.DeserializeObject<IssueSnapshot>(json).ToIssueVm(vm.Id);
            Assert.Equal(vm.TitleEn, back.TitleEn);
            Assert.Equal(vm.DescriptionEn, back.DescriptionEn);
            Assert.Equal(vm.RequiredEn, back.RequiredEn);
            Assert.Equal(vm.ActualEn, back.ActualEn);

            // A restored Fixed issue and an Ignored one print English on the English Borang remark and export.
            foreach (var status in new[] { IssueStatus.Fixed, IssueStatus.Ignored })
            {
                back.Status = status; back.FixVerified = false;
                back.BorangKey = back.BorangKey.Length > 0 ? back.BorangKey : "A1";
                PaneLanguage.Set(PanelStrings.En);
                var (remark, _) = BorangDoc.RemarkFor(back.BorangKey, "n", null, new[] { back });
                Assert.False(Malay.IsMatch(StripLabel(remark)), status + ": " + remark);
                var cells = IssueExportRows.Cells(back);
                Assert.Equal(vm.TitleEn, cells[1]);
                Assert.False(Malay.IsMatch(cells[5] + " " + cells[6]), string.Join(" | ", cells));
                PaneLanguage.Set(PanelStrings.Ms);
            }
        }

        [Fact]
        public void An_old_snapshot_without_the_English_fields_still_loads_and_falls_back_to_BM()
        {
            const string old = "{\"title\":\"A1.1 · Ketinggian siling\",\"description\":\"Aras 01: siling 2700 mm\",\"required\":\"≥ 3000 mm\",\"actual\":\"2700 mm\"}";
            var snap = JsonConvert.DeserializeObject<IssueSnapshot>(old);
            var vm = snap.ToIssueVm("SPEC-A1.1-x");
            Assert.Equal("", vm.TitleEn);
            Assert.Equal("A1.1 · Ketinggian siling", vm.Title);
            PaneLanguage.Set(PanelStrings.En);
            Assert.Equal("A1.1 · Ketinggian siling", IssueExportRows.Cells(vm)[1]);   // BM fallback, no crash
            PaneLanguage.Set(PanelStrings.Ms);
            // and a snapshot written now omits empty English fields (old readers ignore them anyway)
            Assert.DoesNotContain("title_en", JsonConvert.SerializeObject(IssueSnapshot.From(new IssueVm { Title = "x" })));
        }

        [Fact]
        public void Bm_mode_stays_Malay()
        {
            var (_, issues, _) = Run("jkr_run_failing.json");
            PaneLanguage.Set(PanelStrings.Ms);
            Assert.Contains(issues, i => Malay.IsMatch(i.Description ?? ""));
            Assert.Contains(issues.Select(IssueExportRows.Cells), c => Malay.IsMatch(c[5] + " " + c[6]));
        }

        [Fact]
        public void The_scan_still_flags_a_quoted_Malay_sentence_and_Malay_between_English_apostrophes()
        {
            Assert.True(Malay.IsMatch(StripLabel("Schedule 'semua elemen tiada material' is wrong")));
            Assert.True(Malay.IsMatch(StripLabel("it's tiada material and the wall's fine")));
        }

        [Fact]
        public void The_scan_skips_only_the_models_own_names()
        {
            Assert.False(Malay.IsMatch(StripLabel("Schedule 'Jadual SoA' lacks the columns")));
            Assert.True(Malay.IsMatch(StripLabel("Schedule 'Jadual Rawak' lacks the columns")));    // a name the model does not have
        }
    }
}
