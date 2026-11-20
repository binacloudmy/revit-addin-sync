// JkrAutoFixPayload.Trim — the request sent to bina-ai /v1/compliance/jkr-autofix,
// cut to what the backend fixer reads (app/services/jkr_autofix.py). Pinned here:
// every element and field the issues need survives, the rest goes, nulls are safe,
// and the sample request shrinks.
//
// Set JKR_AUTOFIX_DUMP_DIR to write the sample before/after JSON there (offline
// token measurement against the backend's prompt builder).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.Services;
using Xunit;
using Xunit.Abstractions;

namespace RevitWebAppSync.Tests
{
    public class JkrAutoFixPayloadTests
    {
        private readonly ITestOutputHelper _out;
        public JkrAutoFixPayloadTests(ITestOutputHelper output) { _out = output; }

        private static JkrAutoFixIssue Issue(string checkId, long elementId, string rule,
            string actual = "(not found)", string expected = "", string reason = "", string category = "Doors") =>
            new JkrAutoFixIssue
            {
                CheckId = checkId, Rule = rule, ElementId = elementId, Category = category,
                TypeName = "some type", ActualValue = actual, ExpectedValue = expected, Reason = reason,
            };

        private static JkrElementData Element(long id, string category, string typeName, Dictionary<string, string> parameters) =>
            new JkrElementData
            {
                ElementId = id, Category = category, TypeName = typeName, ElementName = typeName,
                FamilyName = "Single-Flush", LevelName = "L01 +0.000", JkrCode = "", Parameters = parameters,
            };

        private static Dictionary<string, string> DoorParams() => new Dictionary<string, string>
        {
            ["Kod_Jenis_jkr_stt"] = "PTU-01",
            ["Sistem_jkr_stt"] = "Bukaan",
            ["Bidang_Kejuruteraan_jkr_stt"] = "Arkitek",
            ["Bahan_jkr_stt"] = "(ky) Kayu",
            ["Kumai_jkr_stm"] = "jkrAR_(ky) Kayu",
            ["Panel_jkr_stt"] = "Panel Kaca",
            ["Coping_jkr_stt"] = "Konkrit",
            ["Alamat_Kontraktor_jkr_sit"] = "Lot 12, Jalan Perindustrian 3, 48300 Rawang, Selangor",
            ["Kontraktor_jkr_sit"] = "Syarikat Binaan Maju Jaya Sdn. Bhd.",
            ["Kod_Komponen_Projek_jkr_stt"] = "PRJ-2026-AR-0012",
            ["Warna_Kelulusan_PBT_jkr_sit"] = "Hijau",
        };

        // ── what it keeps ──────────────────────────────────────────────────

        [Fact]
        public void Keeps_only_the_elements_the_issues_point_at()
        {
            var req = new JkrAutoFixRequest
            {
                Elements = { Element(1, "Doors", "D1", DoorParams()), Element(2, "Doors", "D2", DoorParams()), Element(3, "Walls", "W", DoorParams()) },
                Issues = { Issue("c1", 1, "Missing Type parameter: Kumai_jkr_stt"), Issue("c3", 3, "Naming convention") },
            };

            var t = JkrAutoFixPayload.Trim(req);

            Assert.Equal(new long[] { 1, 3 }, t.Elements.Select(e => e.ElementId));
            Assert.Equal(new[] { "c1", "c3" }, t.Issues.Select(i => i.CheckId));
        }

        [Fact]
        public void Keeps_the_named_parameter_its_twin_the_feature_group_and_the_context_parameters()
        {
            var req = new JkrAutoFixRequest
            {
                Elements = { Element(1, "Doors", "D1", DoorParams()) },
                Issues = { Issue("c1", 1, "Missing Type parameter: Kumai_jkr_stt", expected: "Kumai_jkr_stt") },
            };

            var p = JkrAutoFixPayload.Trim(req).Elements.Single().Parameters;

            Assert.Equal("jkrAR_(ky) Kayu", p["Kumai_jkr_stm"]);       // twin of the named param
            Assert.Equal("Panel Kaca", p["Panel_jkr_stt"]);            // door feature group
            Assert.Equal("Konkrit", p["Coping_jkr_stt"]);
            Assert.Equal("PTU-01", p["Kod_Jenis_jkr_stt"]);            // context
            Assert.Equal("Arkitek", p["Bidang_Kejuruteraan_jkr_stt"]);
            Assert.Equal("(ky) Kayu", p["Bahan_jkr_stt"]);
            Assert.False(p.ContainsKey("Alamat_Kontraktor_jkr_sit"));  // asset data nobody derives
            Assert.False(p.ContainsKey("Kontraktor_jkr_sit"));
            Assert.False(p.ContainsKey("Kod_Komponen_Projek_jkr_stt"));
            Assert.False(p.ContainsKey("Warna_Kelulusan_PBT_jkr_sit"));
        }

        [Fact]
        public void A_parameter_named_by_an_issue_is_kept_verbatim_even_when_long_or_blank()
        {
            var longValue = new string('x', 900);
            var ps = new Dictionary<string, string>
            {
                ["Catatan_Attribut_jkr_sit"] = longValue,
                ["Kemasan_Luar_jkr_stt"] = "",
                ["No_Siri_jkr_sit"] = "SN-1",
            };
            var req = new JkrAutoFixRequest
            {
                Elements = { Element(5, "Walls", "W", ps) },
                Issues =
                {
                    Issue("a", 5, "Catatan_Attribut_jkr_sit value", category: "Walls"),
                    Issue("b", 5, "Empty Type parameter: Kemasan_Luar_jkr_stt", category: "Walls"),
                },
            };

            var p = JkrAutoFixPayload.Trim(req).Elements.Single().Parameters;

            Assert.Equal(longValue, p["Catatan_Attribut_jkr_sit"]);    // becomes old_value: never cut
            Assert.True(p.ContainsKey("Kemasan_Luar_jkr_stt"));
            Assert.Equal("", p["Kemasan_Luar_jkr_stt"]);
            Assert.False(p.ContainsKey("No_Siri_jkr_sit"));
        }

        [Fact]
        public void A_finish_issue_keeps_the_other_finish_parameters()
        {
            var ps = new Dictionary<string, string>
            {
                ["Kemasan_Dalam_jkr_stt"] = "Cat Emulsi",
                ["Kemasan_jkr_stm"] = "jkrAR_(ct) Cat",
                ["Pengilang_jkr_sit"] = "Nippon",
            };
            var req = new JkrAutoFixRequest
            {
                Elements = { Element(5, "Walls", "W", ps) },
                Issues = { Issue("a", 5, "Missing Type parameter: Kemasan_Luar_jkr_stt", category: "Walls") },
            };

            var p = JkrAutoFixPayload.Trim(req).Elements.Single().Parameters;

            Assert.Equal(new[] { "Kemasan_Dalam_jkr_stt", "Kemasan_jkr_stm" }, p.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        [Fact]
        public void Keeps_the_project_fields_the_prompt_reads_and_empties_the_rest()
        {
            var req = new JkrAutoFixRequest
            {
                Project = new JkrProjectMetadata
                {
                    ProjectName = "Hospital", FileName = "jkrAR_HSP_L01", Discipline = "AR", LodLevel = 350,
                    ProjectPhase = "Rekabentuk", TemplateUsed = "JKR 2024", HasBpep = true,
                    SharedParamFiles = { @"C:\JKR\shared.txt" }, ProjectInfo = { ["Client Name"] = "JKR" },
                    BasePointE = 1, BasePointN = 2, BasePointElev = 3, GridNames = { "A", "B" }, GridIds = { 10, 11 },
                },
                Model = new JkrModelMetadata
                {
                    HasLinkedModels = true, LinkedModelNames = { "ST.rvt" }, Levels = { "L01" },
                    LevelElevations = { 0 }, LevelIds = { 99 },
                },
            };

            var t = JkrAutoFixPayload.Trim(req);

            Assert.Equal("jkrAR_HSP_L01", t.Project.FileName);
            Assert.Equal("AR", t.Project.Discipline);
            Assert.Equal(350, t.Project.LodLevel);
            Assert.Equal("Rekabentuk", t.Project.ProjectPhase);
            Assert.Equal("", t.Project.ProjectName);
            Assert.Equal("", t.Project.TemplateUsed);
            Assert.False(t.Project.HasBpep);
            Assert.Empty(t.Project.SharedParamFiles);
            Assert.Empty(t.Project.ProjectInfo);
            Assert.Null(t.Project.BasePointE);
            Assert.Empty(t.Project.GridNames);
            Assert.Empty(t.Project.GridIds);
            Assert.False(t.Model.HasLinkedModels);
            Assert.Empty(t.Model.Levels);
            Assert.Empty(t.Model.LevelIds);
            // Never null: the backend's ProjectMetadata/ModelMetadata do not accept null.
            Assert.NotNull(t.Project);
            Assert.NotNull(t.Model);
        }

        [Fact]
        public void Keeps_every_issue_field_the_prompt_reads()
        {
            var req = new JkrAutoFixRequest
            {
                Issues = { new JkrAutoFixIssue
                {
                    CheckId = "L-1", Rule = "Level naming convention", Domain = "project_setup", Status = "fail",
                    ElementId = 311, Category = "Levels", TypeName = "8mm Head",
                    ActualValue = "Level 2", ExpectedValue = "L{NN} +elev", Reason = "Level 'Level 2' (index 2, +4.500) is not L{NN}.",
                } },
            };

            var i = JkrAutoFixPayload.Trim(req).Issues.Single();

            Assert.Equal("L-1", i.CheckId);
            Assert.Equal("Level naming convention", i.Rule);
            Assert.Equal("project_setup", i.Domain);
            Assert.Equal("fail", i.Status);
            Assert.Equal(311, i.ElementId);
            Assert.Equal("Levels", i.Category);
            Assert.Equal("Level 2", i.ActualValue);   // old_value of a level rename (no element sent)
            Assert.Equal("L{NN} +elev", i.ExpectedValue);
            Assert.Equal("Level 'Level 2' (index 2, +4.500) is not L{NN}.", i.Reason);
            Assert.Equal("", i.TypeName);             // not in the prompt
        }

        // ── what it drops ──────────────────────────────────────────────────

        [Fact]
        public void Drops_issues_without_a_check_id_and_repeats_of_one()
        {
            var req = new JkrAutoFixRequest
            {
                Issues =
                {
                    Issue("c1", 1, "Naming convention"),
                    Issue("", 1, "Naming convention"),
                    Issue(null, 1, "Naming convention"),
                    Issue("c1", 1, "Naming convention"),
                    Issue("c2", 1, "Valid subtype code"),   // same element, other rule: kept
                },
            };

            Assert.Equal(new[] { "c1", "c2" }, JkrAutoFixPayload.Trim(req).Issues.Select(i => i.CheckId));
        }

        [Fact]
        public void Caps_long_reason_and_expected_value_but_never_actual_value_or_rule()
        {
            var big = string.Join(", ", Enumerable.Range(0, 200).Select(n => $"s{n}=Subjenis {n}"));
            var req = new JkrAutoFixRequest
            {
                Issues = { Issue("c1", 1, "Valid subtype code " + big, actual: big, expected: big, reason: big) },
            };

            var i = JkrAutoFixPayload.Trim(req).Issues.Single();

            Assert.Equal(JkrAutoFixPayload.DefaultMaxTextLength, i.ExpectedValue.Length);
            Assert.EndsWith("\u2026", i.ExpectedValue);
            Assert.StartsWith(big.Substring(0, 200), i.ExpectedValue);
            Assert.Equal(JkrAutoFixPayload.DefaultMaxTextLength, i.Reason.Length);
            Assert.Equal(big, i.ActualValue);
            Assert.Equal("Valid subtype code " + big, i.Rule);
            // 0 turns the cap off
            Assert.Equal(big, JkrAutoFixPayload.Trim(req, 0).Issues.Single().Reason);
        }

        [Fact]
        public void Blanks_element_name_and_jkr_code_only_where_they_repeat_something_sent()
        {
            var same = Element(1, "Doors", "jkrAR_dor-k_(ky) Pintu", new Dictionary<string, string> { ["Kod_DAK_Komponen_jkr_stt"] = "AR-PTU-01" });
            same.JkrCode = "AR-PTU-01";
            var differ = Element(2, "Doors", "jkrAR_dor-k_(ky) Pintu", new Dictionary<string, string>());
            differ.ElementName = "Pintu Utama";
            differ.JkrCode = "AR-PTU-02";
            var req = new JkrAutoFixRequest
            {
                Elements = { same, differ },
                Issues = { Issue("a", 1, "Naming convention"), Issue("b", 2, "Naming convention") },
            };

            var t = JkrAutoFixPayload.Trim(req).Elements;

            Assert.Equal("", t[0].ElementName);            // backend reads element_name or type_name
            Assert.Equal("jkrAR_dor-k_(ky) Pintu", t[0].TypeName);
            Assert.Equal("", t[0].JkrCode);                // same value is in Kod_DAK_Komponen_jkr_stt
            Assert.Equal("Pintu Utama", t[1].ElementName);
            Assert.Equal("AR-PTU-02", t[1].JkrCode);        // its parameter was not sent
            Assert.All(t, e => Assert.Equal("", e.LevelName));
            Assert.All(t, e => Assert.Equal("Single-Flush", e.FamilyName));
        }

        [Fact]
        public void On_a_repeated_element_id_the_last_one_wins_like_the_backend()
        {
            var first = Element(1, "Doors", "old", new Dictionary<string, string>());
            var last = Element(1, "Doors", "new", new Dictionary<string, string>());
            var req = new JkrAutoFixRequest { Elements = { first, last }, Issues = { Issue("a", 1, "Naming convention") } };

            var t = JkrAutoFixPayload.Trim(req).Elements;

            Assert.Equal("new", t.Single().TypeName);
        }

        // ── null safety, purity ────────────────────────────────────────────

        [Fact]
        public void Null_request_gives_an_empty_request()
        {
            var t = JkrAutoFixPayload.Trim(null);
            Assert.NotNull(t.Project);
            Assert.NotNull(t.Model);
            Assert.Empty(t.Elements);
            Assert.Empty(t.Issues);
        }

        [Fact]
        public void Nulls_inside_are_skipped_or_become_empty()
        {
            var el = new JkrElementData { ElementId = 4, Category = null, TypeName = null, ElementName = null, FamilyName = null, JkrCode = null, Parameters = null };
            var el2 = new JkrElementData { ElementId = 5, Category = "Walls", Parameters = new Dictionary<string, string> { ["Bahan_jkr_stt"] = null, ["Kumai_jkr_stt"] = null } };
            var req = new JkrAutoFixRequest
            {
                Project = null,
                Model = null,
                Elements = new List<JkrElementData> { null, el, el2 },
                Issues = new List<JkrAutoFixIssue>
                {
                    null,
                    new JkrAutoFixIssue { CheckId = "x", ElementId = 4, Rule = null, Category = null, ActualValue = null, ExpectedValue = null, Reason = null, Domain = null, Status = null },
                    new JkrAutoFixIssue { CheckId = "y", ElementId = 5, Rule = "Missing Type parameter: Kumai_jkr_stt" },
                },
            };

            var t = JkrAutoFixPayload.Trim(req);

            Assert.Equal(2, t.Elements.Count);
            Assert.Equal("", t.Elements[0].Category);
            Assert.Empty(t.Elements[0].Parameters);
            Assert.Equal(new[] { "Kumai_jkr_stt" }, t.Elements[1].Parameters.Keys);   // named: kept even null
            var i = t.Issues[0];
            Assert.Equal("", i.Rule);
            Assert.Equal("", i.Reason);
            Assert.Equal("", i.ExpectedValue);
            Assert.Equal("", i.ActualValue);
            Assert.Equal("AR", t.Project.Discipline);
            JsonConvert.SerializeObject(t);   // serialises without throwing

            var noLists = new JkrAutoFixRequest { Elements = null, Issues = null };
            Assert.Empty(JkrAutoFixPayload.Trim(noLists).Issues);
        }

        [Fact]
        public void Leaves_the_input_untouched_and_gives_the_same_output_every_time()
        {
            var req = SamplePayload.Build();
            var before = JsonConvert.SerializeObject(req);

            var a = JsonConvert.SerializeObject(JkrAutoFixPayload.Trim(req));
            var b = JsonConvert.SerializeObject(JkrAutoFixPayload.Trim(req));

            Assert.Equal(before, JsonConvert.SerializeObject(req));
            Assert.Equal(a, b);
        }

        // ── the sample request ─────────────────────────────────────────────

        [Fact]
        public void Sample_keeps_every_issue_and_every_element_an_issue_needs()
        {
            var req = SamplePayload.Build();
            var t = JkrAutoFixPayload.Trim(req);

            Assert.Equal(50, req.Issues.Count);
            Assert.Equal(40, req.Elements.Count);
            Assert.Equal(40, t.Elements.Count);
            Assert.Equal(req.Issues.Select(i => i.CheckId), t.Issues.Select(i => i.CheckId));
            var ids = new HashSet<long>(t.Elements.Select(e => e.ElementId));
            Assert.All(t.Issues, i => Assert.Contains(i.ElementId, ids));

            // Every parameter an issue names, that the element has, is still there with its value.
            var src = req.Elements.ToDictionary(e => e.ElementId);
            foreach (var el in t.Elements)
            {
                var text = string.Join("\n", req.Issues.Where(i => i.ElementId == el.ElementId).Select(i => i.Rule + "\n" + i.ExpectedValue));
                foreach (var kv in src[el.ElementId].Parameters.Where(kv => text.Contains(kv.Key)))
                    Assert.Equal(kv.Value, el.Parameters[kv.Key]);
                Assert.Equal(src[el.ElementId].TypeName, el.TypeName);
                Assert.Equal(src[el.ElementId].Category, el.Category);
            }
        }

        [Fact]
        public void Sample_shrinks()
        {
            var req = SamplePayload.Build();
            var lean = SamplePayload.Build(fullTemplate: false);
            var beforeJson = JsonConvert.SerializeObject(req);
            var afterJson = JsonConvert.SerializeObject(JkrAutoFixPayload.Trim(req));
            var leanBefore = JsonConvert.SerializeObject(lean);
            var leanAfter = JsonConvert.SerializeObject(JkrAutoFixPayload.Trim(lean));

            _out.WriteLine($"full template: {req.Issues.Count} issues, {req.Elements.Count} elements, " +
                           $"{beforeJson.Length} -> {afterJson.Length} chars ({100.0 * afterJson.Length / beforeJson.Length:F0}%)");
            _out.WriteLine($"lean model:    {leanBefore.Length} -> {leanAfter.Length} chars ({100.0 * leanAfter.Length / leanBefore.Length:F0}%)");

            Assert.True(afterJson.Length * 2 < beforeJson.Length, $"{beforeJson.Length} -> {afterJson.Length}");
            Assert.True(leanAfter.Length < leanBefore.Length);

            var dir = Environment.GetEnvironmentVariable("JKR_AUTOFIX_DUMP_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "full_before.json"), beforeJson);
                File.WriteAllText(Path.Combine(dir, "full_after.json"), afterJson);
                File.WriteAllText(Path.Combine(dir, "lean_before.json"), leanBefore);
                File.WriteAllText(Path.Combine(dir, "lean_after.json"), leanAfter);
            }
        }
    }

    /// <summary>
    /// A realistic AI-fix request: 50 candidate issues over 40 element types, built the
    /// way the panel builds it (FetchAiProposalAsync) from a JkrBuildingInfoExtractor
    /// scan. fullTemplate: the JKR template's asset/FM shared parameters are filled
    /// (contractor, supplier, dates, documents), as on a project handed over at LOD
    /// 400+; lean: only the classification parameters.
    /// </summary>
    internal static class SamplePayload
    {
        private static readonly (string Cat, string Prefix, string Bidang, string Nama, string Bahan, int Count)[] Kinds =
        {
            ("Walls", "wll", "Arkitek", "Dinding Batu Bata", "(bb02) Batu Bata", 12),
            ("Doors", "dor", "Arkitek", "Pintu", "(ky) Kayu", 8),
            ("Windows", "wdw", "Arkitek", "Tingkap", "(al) Aluminium", 6),
            ("Floors", "flr", "Arkitek", "Lantai Jubin", "(jb) Jubin", 5),
            ("Ceilings", "clg", "Arkitek", "Siling", "(gp) Papan Gipsum", 3),
            ("Roofs", "rof", "Arkitek", "Bumbung", "(gm) Genting Metal", 2),
            ("Structural Columns", "col", "Struktur", "Tiang", "(kt) Konkrit", 2),
            ("Stairs", "str", "Arkitek", "Tangga", "(kt) Konkrit", 2),
        };

        public static JkrAutoFixRequest Build(bool fullTemplate = true)
        {
            var req = new JkrAutoFixRequest
            {
                Project = new JkrProjectMetadata
                {
                    ProjectName = "jkrAR_HKL_BlokB_L01-L08",
                    FileName = "jkrAR_HKL_BlokB_L01-L08",
                    Discipline = "AR",
                    LodLevel = 300,
                    ProjectPhase = "",
                    TemplateUsed = "JKR Template (inferred from filename)",
                    SharedParamFiles = { @"C:\ProgramData\JKR\BIM\JKR_SharedParameters_2024.txt" },
                    ProjectInfo =
                    {
                        ["Client Name"] = "Jabatan Kerja Raya Malaysia",
                        ["Project Name"] = "Hospital Kuala Lumpur Blok B (Pembesaran Wad)",
                        ["Project Number"] = "JKR/HKL/2026/0042",
                        ["Project Address"] = "Jalan Pahang, 50586 Kuala Lumpur, Wilayah Persekutuan",
                        ["Building Name"] = "Blok B",
                        ["Organization Name"] = "Cawangan Arkitek, Ibu Pejabat JKR",
                    },
                    BasePointE = 401234.567, BasePointN = 351987.654, BasePointElev = 52.3,
                },
                Model = new JkrModelMetadata { HasLinkedModels = true, LinkedModelNames = { "jkrST_HKL_BlokB.rvt", "jkrME_HKL_BlokB.rvt" } },
            };
            foreach (var g in "ABCDEFGHJKLMNPQRSTUV") { req.Project.GridNames.Add(g.ToString()); req.Project.GridIds.Add(900000 + g); }
            for (int n = 1; n <= 12; n++) req.Project.GridNames.Add(n.ToString());
            for (int n = 1; n <= 12; n++) req.Project.GridIds.Add(900100 + n);
            for (int l = 0; l < 9; l++)
            {
                req.Model.Levels.Add($"L{l:00} +{l * 4.5:0.000}");
                req.Model.LevelElevations.Add(l * 4500);
                req.Model.LevelIds.Add(311 + l);
            }

            long id = 1_204_000;
            int seq = 0;
            var index = new List<int>();
            foreach (var k in Kinds)
            {
                for (int n = 0; n < k.Count; n++, id += 137, seq++)
                {
                    bool jkrNamed = n % 3 != 0 || !(k.Cat == "Walls" || k.Cat == "Doors" || k.Cat == "Windows");
                    var typeName = jkrNamed
                        ? $"jkr{(k.Bidang == "Struktur" ? "ST" : "AR")}_{k.Prefix}-b_{k.Bahan.Split(' ')[0]} {k.Nama} {100 + n * 15}"
                        : $"Generic - {100 + n * 15}mm {k.Cat.TrimEnd('s')}";
                    var kod = $"{(k.Bidang == "Struktur" ? "ST" : "AR")}-{k.Prefix.ToUpperInvariant()}-{n + 1:000}";
                    var p = new Dictionary<string, string>
                    {
                        ["Kod_Jenis_jkr_stt"] = $"{k.Prefix.ToUpperInvariant()}-{(n % 4) + 1:00}",
                        ["Sistem_jkr_stt"] = k.Cat == "Doors" || k.Cat == "Windows" ? "Bukaan" : "Struktur Bangunan",
                        ["Sub_Sistem_jkr_stt"] = k.Cat == "Walls" ? "Dinding Dalaman" : k.Nama,
                        ["Bidang_Kejuruteraan_jkr_stt"] = k.Bidang,
                        ["Nama_Komponen_Utama_jkr_stt"] = k.Nama,
                        ["Kod_DAK_Komponen_jkr_stt"] = kod,
                        ["Kod_Komponen_jkr_stt"] = kod,
                        ["Bahan_jkr_stt"] = k.Bahan,
                        ["LOD_jkr_sit"] = "300",
                        ["Komponen_Bomba_jkr_sty"] = n % 5 == 0 ? "1" : "0",
                        ["Kod_Komponen_Projek_jkr_stt"] = $"HKL-B-{kod}",
                        ["Kod_Komponen_Sumber_jkr_stt"] = $"JKR-SRC-{kod}",
                        ["Kod_Ruang_Berkaitan_jkr_stt"] = $"B-L0{n % 8 + 1}-R{n + 10:000}, B-L0{n % 8 + 1}-R{n + 11:000}",
                    };
                    if (k.Cat == "Walls" || k.Cat == "Floors") p["Komponen_IBS_jkr_sty"] = "0";
                    if (k.Cat == "Doors" || k.Cat == "Windows")
                    {
                        p["Bingkai_jkr_stt"] = k.Cat == "Doors" ? "Kayu Keras" : "Aluminium Anodized";
                        p["Bingkai_jkr_stm"] = k.Cat == "Doors" ? "jkrAR_(ky) Kayu" : "jkrAR_(al) Aluminium";
                        p["Panel_jkr_stt"] = n % 2 == 0 ? "Panel Kaca" : "Panel Kayu";
                        p["Panel_jkr_stm"] = n % 2 == 0 ? "jkrAR_(kc) Kaca" : "jkrAR_(ky) Kayu";
                        p["Kod_Rekabentuk_jkr_stt"] = $"RB-{k.Prefix.ToUpperInvariant()}-{n + 1:00}";
                    }
                    if (fullTemplate)
                    {
                        p["Kontraktor_jkr_sit"] = "Syarikat Binaan Maju Jaya Sdn. Bhd.";
                        p["Alamat_Kontraktor_jkr_sit"] = "Lot 12, Jalan Perindustrian 3, Kawasan Perindustrian Bukit Beruntung, 48300 Rawang, Selangor";
                        p["No_Tel_Kontraktor_jkr_sit"] = "+603-6028 1145";
                        p["Pembekal_jkr_sit"] = "Bekalan Bahan Binaan Utara Sdn. Bhd.";
                        p["Alamat_Pembekal_jkr_sit"] = "No. 8, Jalan Industri 2/4, Taman Perindustrian Rawang, 48000 Rawang, Selangor";
                        p["No_Tel_Pembekal_jkr_sit"] = "+603-6092 7781";
                        p["Pengilang_jkr_sit"] = "Kilang Produk Binaan Nusantara Sdn. Bhd.";
                        p["Jenama_jkr_sit"] = "NusaBina";
                        p["Model_jkr_sit"] = $"NB-{k.Prefix.ToUpperInvariant()}-{100 + n}";
                        p["Kod_PTJ_jkr_sit"] = "240101";
                        p["Kos_Perolehan_jkr_sic"] = $"{1250 + n * 75}.00";
                        p["No_Pesanan_Rasmi_Kerajaan_jkr_sit"] = $"PO-JKR-2026-{4512 + seq:000000}";
                        p["No_Sijil_Pendaftaran_jkr_sit"] = "CIDB-G7-0123456789";
                        p["Tarikh_Perolehan_jkr_sit"] = "2026-02-01";
                        p["Tarikh_Dipasang_jkr_sit"] = "2026-03-15";
                        p["Tarikh_Tamat_DLP_jkr_sit"] = "2028-03-15";
                        p["Tarikh_Waranti_Tamat_jkr_sit"] = "2031-03-15";
                        p["Jangka_Hayat_jkr_sit"] = "25 tahun";
                        p["Status_Komponen_jkr_sit"] = "Baharu";
                        p["Saiz_Fizikal_jkr_sit"] = $"{100 + n * 15} x 3000 mm";
                        p["Catatan_Attribut_jkr_sit"] = "Maklumat aset diisi oleh kontraktor semasa serahan projek; rujuk Lampiran B, Senarai Aset Tak Alih.";
                        p["Dokumen_Berkaitan_01_jkr_sit"] = $"DOC-AR-SPEC-00{12 + n} Spesifikasi Seni Bina Rev C.pdf";
                        p["Dokumen_Berkaitan_02_jkr_sit"] = "DOC-AR-DWG-0401 Lukisan Butiran Rev B.pdf";
                        p["No_Rujukan_Berkaitan_01_jkr_sit"] = $"JKR.HKL.B.{k.Prefix.ToUpperInvariant()}.{n + 1:000}";
                        p["No_Rujukan_Berkaitan_02_jkr_sit"] = "JKR.HKL.B.LAMP-B";
                        p["No_Siri_jkr_sit"] = $"SN{20260000 + seq * 17}";
                        p["No_Teg_Label_jkr_sit"] = $"TAG-B-{k.Prefix.ToUpperInvariant()}-{seq:0000}";
                        p["Warna_Kelulusan_PBT_jkr_sit"] = "Hijau";
                    }
                    index.Add(n);
                    req.Elements.Add(new JkrElementData
                    {
                        ElementId = id,
                        Category = k.Cat,
                        TypeName = typeName,
                        ElementName = typeName,
                        FamilyName = k.Cat == "Doors" ? "M_Single-Flush" : k.Cat == "Windows" ? "M_Fixed" : $"Basic {k.Cat.TrimEnd('s')}",
                        LevelName = $"L0{n % 8 + 1} +{(n % 8) * 4.5:0.000}",
                        JkrCode = kod,
                        Parameters = p,
                    });
                }
            }

            // 50 issues on the 40 types, the kind that come back fixable=false: missing
            // params the backend can't infer, naming without a suggestion, a subtype
            // warning. Every element has at least one (the panel sends only those).
            int c = 0;
            for (int e = 0; e < req.Elements.Count; e++)
            {
                var el = req.Elements[e];
                foreach (var r in IssuesFor(el, index[e], req.Project.LodLevel))
                {
                    req.Issues.Add(new JkrAutoFixIssue
                    {
                        CheckId = $"{(el.ElementId * 31 + c * 7919):x12}".Substring(0, 12),
                        Rule = r.Rule,
                        Domain = "element",
                        Status = "fail",
                        ElementId = el.ElementId,
                        Category = el.Category,
                        TypeName = el.TypeName,
                        ActualValue = r.Actual,
                        ExpectedValue = r.Expected,
                        Reason = r.Reason,
                    });
                    c++;
                }
            }
            return req;
        }

        private static IEnumerable<(string Rule, string Actual, string Expected, string Reason)> IssuesFor(JkrElementData el, int n, int lod)
        {
            string Missing(string kind, string param) => $"Missing {kind} parameter: {param}";
            (string, string, string, string) Miss(string kind, string param) =>
                (Missing(kind, param), "(not found)", param, $"{kind} parameter '{param}' required at LOD {lod} for {el.Category}.");
            var name = el.TypeName;
            if (!name.StartsWith("jkr", StringComparison.Ordinal))
                yield return ("Naming convention", name, "jkr{Discipline}_{...}", $"Name '{name}' does not follow JKR naming pattern.");
            switch (el.Category)
            {
                case "Walls":
                    yield return Miss("Type", "Kod_Tinggi_jkr_jkr_stt");
                    break;
                case "Doors":
                    yield return Miss("Type", "Kumai_jkr_stt");
                    if (n == 1) yield return ("Valid subtype code", "b",
                        "s=Pintu Single, d=Pintu Double, k=Pintu Kayu, l=Pintu Logam, kc=Pintu Kaca, r=Pintu Roller Shutter, g=Pintu Gelangsar, f=Pintu Kalis Api, p=Pintu Panel",
                        $"Subtype 'b' not in known subtypes for {el.Category}.");
                    break;
                case "Windows":
                    yield return Miss("Type", "Coping_jkr_stt");
                    break;
                case "Floors":
                    yield return Miss("Type", "Lapisan_Kalis_Air_jkr_stx");
                    break;
                default:
                    yield return Miss("Instance", "Kaedah_Pemasangan_jkr_sit");
                    break;
            }
        }
    }
}
