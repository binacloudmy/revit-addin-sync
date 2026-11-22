using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-516 round 2: the parameter names of the Db.1 rows and the legacy "Missing ... Parameter" rows, on the REAL strings of the
    /// 23 Nov round-b export (MrBiggie, add-in 0.0.203): 58 Db.1 rows, 309 Missing, 148 Optional. Plus the dedupe dump.
    /// </summary>
    public class Db1ParamNameTests
    {
        private sealed class Csv { public string Id, Title, Required, Actual; }

        private static List<Csv> Load()
        {
            var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "ai516b_issues_export_round2.csv"), Encoding.UTF8).TrimStart('﻿');
            var list = new List<Csv>(); var row = new List<string>(); var cell = new StringBuilder(); var q = false; var first = true;
            Action end = () => { row.Add(cell.ToString()); cell.Clear(); if (!first && row.Count >= 7) list.Add(new Csv { Id = row[0], Title = row[1], Required = row[5], Actual = row[6] }); first = false; row = new List<string>(); };
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (q) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; } else cell.Append(c); continue; }
                if (c == '"') q = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n') end();
                else if (c != '\r') cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) end();
            return list;
        }

        private static List<string> SpecParams() =>
            Load().Where(c => c.Title.StartsWith("Db.1", StringComparison.Ordinal)).SelectMany(c => SpecDedupe.ParamsOfActual(c.Actual)).Distinct().ToList();

        private static bool IsLegacy(Csv c) => c.Title.StartsWith("Missing ", StringComparison.Ordinal) || c.Title.StartsWith("Optional ", StringComparison.Ordinal);

        [Fact]
        public void Export_has_the_row_counts_the_report_names()
        {
            var all = Load();
            Assert.Equal(58, all.Count(c => c.Title.StartsWith("Db.1", StringComparison.Ordinal)));
            Assert.Equal(309, all.Count(c => c.Title.StartsWith("Missing ", StringComparison.Ordinal)));
            Assert.Equal(148, all.Count(c => c.Title.StartsWith("Optional ", StringComparison.Ordinal)));
        }

        [Fact]
        public void Every_legacy_name_that_a_Db1_row_lists_maps_to_it_by_the_name_in_the_title_and_by_the_Required_column()
        {
            var spec = SpecParams();
            var names = Load().Where(IsLegacy).Select(c => (title: SpecDedupe.ParamKey(SpecDedupe.ParamOfRule(c.Title, c.Required)), required: SpecDedupe.ParamKey(c.Required))).Distinct().ToList();
            Assert.Equal(29, names.Count);
            var mapped = names.Where(n => SpecDedupe.MatchSpecParam(spec, n.title) != null).ToList();
            // 18 of 29 names are listed by some Db.1 row; the other 11 are not named in ANY Db.1 row (not a naming problem: nothing to match).
            Assert.Equal(18, mapped.Count);
            Assert.All(mapped, n => Assert.Equal(n.required, SpecDedupe.MatchSpecParam(spec, n.title)));
            var unmapped = names.Where(n => SpecDedupe.MatchSpecParam(spec, n.title) == null).Select(n => n.required).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "bingkai_jkr_stm", "butiran_apk_jkr_six", "kaca_jkr_stm", "kaca_jkr_stt", "kemasan_dalam_jkr_stt", "kemasan_luar_jkr_stt", "komponen_bomba_jkr_sty", "komponen_ibs_jkr_sty", "kod_ruang_berkaitan_jkr_stt", "lapisan_kalis_air_jkr_sty", "tarikh_kelulusan_apk_jkr_sit" }.OrderBy(x => x), unmapped);
            // No unmapped name has a spec name with the same base, except Lapisan_Kalis_Air: legacy _jkr_sty, spec _jkr_stx (two different suffixes = two parameters; not merged on purpose).
            Assert.All(unmapped.Where(u => u != "lapisan_kalis_air_jkr_sty"), u => Assert.DoesNotContain(SpecDedupe.ParamBaseKey(u), spec.Select(SpecDedupe.ParamBaseKey)));
            Assert.Contains("lapisan_kalis_air_jkr_stx", spec);
        }

        [Fact]
        public void A_name_with_no_suffix_matches_the_spec_name_with_one()
        {
            var spec = SpecParams();
            Assert.Equal("bidang_kejuruteraan_jkr_stt", SpecDedupe.MatchSpecParam(spec, SpecDedupe.ParamKey("Bidang Kejuruteraan")));
            Assert.Equal("nama_komponen_utama_jkr_stt", SpecDedupe.MatchSpecParam(spec, SpecDedupe.ParamKey("Nama_Komponen_Utama")));
            Assert.Equal("lod_jkr_sit", SpecDedupe.MatchSpecParam(spec, SpecDedupe.ParamKey("LOD")));
        }

        [Fact]
        public void Suffix_kinds_are_different_parameters_and_an_ambiguous_base_matches_nothing()
        {
            var spec = SpecParams();
            // Coping_jkr_stm (material) and Coping_jkr_stt (type) are both listed: the bare base is ambiguous, a suffixed name never crosses.
            Assert.Contains("coping_jkr_stm", spec); Assert.Contains("coping_jkr_stt", spec);
            Assert.Null(SpecDedupe.MatchSpecParam(spec, "coping"));
            Assert.Equal("coping_jkr_stm", SpecDedupe.MatchSpecParam(spec, "coping_jkr_stm"));
            Assert.Null(SpecDedupe.MatchSpecParam(new[] { "Kaca_jkr_stm" }, "kaca_jkr_stt"));
            Assert.Equal("bahan_jkr_stt", SpecDedupe.MatchSpecParam(new[] { "Bahan_jkr_stt" }, "bahan"));
        }

        [Fact]
        public void ParamBaseKey_strips_only_the_jkr_suffix_and_normalises_case_spaces_underscores()
        {
            Assert.Equal("bidang_kejuruteraan", SpecDedupe.ParamBaseKey("Bidang_Kejuruteraan_jkr_stt"));
            Assert.Equal("bidang_kejuruteraan", SpecDedupe.ParamBaseKey("Bidang Kejuruteraan Jkr Stt"));
            Assert.Equal("bidang_kejuruteraan", SpecDedupe.ParamBaseKey("  BIDANG  kejuruteraan "));
            Assert.Equal("lod", SpecDedupe.ParamBaseKey("LOD_jkr_sit"));
            Assert.Equal("luar_kontrak", SpecDedupe.ParamBaseKey("Luar_Kontrak_jkr_sty"));
        }

        private static SpecDedupe.Row Spec(string id, string actualParams, params long[] ids) => new SpecDedupe.Row
        {
            Id = id, CheckId = "Db.1", Source = SpecDedupe.FromModel, Category = "Walls", TypeName = "T", Family = "F",
            Params = SpecDedupe.ParamsOfActual("missing: " + actualParams), ElementIds = ids, ElementId = ids.First(), Title = "Db.1 · Mandatory", Bucket = ScanBoard.Open,
        };

        [Fact]
        public void The_dump_names_the_rule_that_dropped_held_and_kept_each_row()
        {
            var spec = Spec("SPEC-Db.1-a", "Bidang_Kejuruteraan_jkr_stt, Sistem_jkr_stt", 10, 11);
            var typeRow = new SpecDedupe.Row { Id = "L1", CheckId = "Db.1", Source = SpecDedupe.FromBackend, Fixable = true, Bucket = ScanBoard.Open, Category = "Walls", ElementId = 10, Param = "Bidang Kejuruteraan Jkr Stt", ParamRule = true, ParamKind = "type", Title = "Missing Type Parameter: Bidang Kejuruteraan Jkr Stt" };
            var noCat = new SpecDedupe.Row { Id = "L2", CheckId = "Db.1", Source = SpecDedupe.FromBackend, Fixable = true, Bucket = ScanBoard.Open, Category = "", ElementId = 0, Param = "Sistem Jkr Stt", ParamRule = true, ParamKind = "type" };
            var other = new SpecDedupe.Row { Id = "L3", CheckId = "", Source = SpecDedupe.FromBackend, Bucket = ScanBoard.Open, Category = "Doors", Param = "Kaca Jkr Stt", ParamRule = true };
            var kept = ScanBoard.Merge(new[] { typeRow, noCat, other }, null, new[] { spec });
            var root = JObject.Parse(DedupeDump.Json());
            var rows = root["rows"].ToDictionary(x => (string)x["Id"]);
            Assert.StartsWith("kept:fixable-holds-param", (string)rows["L1"]["Verdict"]);
            Assert.StartsWith("kept:not-covered", (string)rows["L2"]["Verdict"]);
            Assert.Contains("row-has-no-revit-category", (string)rows["L2"]["Verdict"]);
            Assert.StartsWith("kept:not-covered", (string)rows["L3"]["Verdict"]);
            Assert.StartsWith("trimmed:held 1 of 2", (string)rows["SPEC-Db.1-a"]["Verdict"]);
            Assert.Equal("bidang_kejuruteraan_jkr_stt", (string)rows["L1"]["ParamKey"]);
            Assert.Equal("bidang_kejuruteraan", (string)rows["L1"]["ParamBaseKey"]);
            Assert.Equal(2, (int)rows["SPEC-Db.1-a"]["ElementIdCount"]);
            Assert.Equal(4, kept.Count);   // L1, L2, L3 stay; the spec row stays, trimmed
        }

        [Fact]
        public void The_dump_goes_next_to_an_export()
        {
            ScanBoard.Merge(new SpecDedupe.Row[0], null, new[] { Spec("SPEC-Db.1-b", "Sistem_jkr_stt", 1) });
            var dir = Path.Combine(Path.GetTempPath(), "ai516-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                var written = DedupeDump.WriteBesideExport(Path.Combine(dir, "report.csv"));
                Assert.Equal(Path.Combine(dir, "report.dedupe.json"), written);
                Assert.Equal("AI-516", (string)JObject.Parse(File.ReadAllText(written))["issue"]);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
