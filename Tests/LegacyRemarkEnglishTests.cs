using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-421 (round 2): every legacy checker remark has an English text (PaneText.AuditRemarkEn).
    /// AuditCheckers needs a live Revit Document, so the BM samples below are the literal remark
    /// templates of BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs (+ AuditTools.cs) filled with sample
    /// values; <see cref="Checker_remark_sites_are_all_covered"/> reads the checker SOURCE and fails when a
    /// Remark statement changes or a new one appears.
    /// </summary>
    public class LegacyRemarkEnglishTests
    {
        private const string RFile = ">=3 segmen dipisah '-' atau '_'";
        private const string RGrid = "grid > 0 dan aras > 0; setiap grid dan aras ada nama yang unik (tiada nama kosong/berganda); setiap pelan lantai terikat pada satu aras";
        private const string RDept = "setiap bilik (Room) ada nilai parameter 'Department'";
        private const string RMat = "setiap elemen Walls/Floors/Ceilings/Roofs ada sekurang-kurangnya satu material";
        private const string RProj = "Project Name/Number/Address/Client Name/Status/Issue Date terisi dan bukan nilai lalai Revit";
        private const string RVt = "setiap view grafik ada View Template";
        private const string RBucket = "sekurang-kurangnya satu view dengan 'BOMBA' dalam namanya";
        private const string RArea = "sekurang-kurangnya satu Area Plan untuk analisis ruang/zon";
        private const string RLegend = "sekurang-kurangnya satu view Legend untuk komponen dan nota am";
        private const string RSched = "nama jadual mengandungi (termasuk awalan JKR): Schedule of Accommodation (accommodation|soa) / Building Component Schedule (component) / Material Takeoff Schedule (mto) / jadual komponen (door|window|wall|floor|ceiling|roof|stair|railing + schedule)";
        private const string RSheet = "setiap Sheet mengandungi sekurang-kurangnya satu view";
        private const string RTb = "nama family/type title block mengandungi 'JKR'";
        private const string RLink = "setiap Revit link berstatus 'Loaded'";
        private const string RBomba = "setiap view dengan 'BOMBA' dalam namanya ada View Template";
        private const string RType = "nama jenis (ElementType) yang digunakan ikut format JKR — kod JKR '(XXnnn)' / awalan 'jkrAR' / nama bahan";
        private const string RRoom = "setiap bilik (Room) yang diletakkan ada Number dan Name";
        private const string Q = " (Kualiti/maklumat/geometri: semak manual.)";
        private const string Full = " Senarai penuh 12 item ada dalam draft_export (element_ids).";
        private const string Cap = " Imbasan dihadkan kepada 5000 elemen; elemen selebihnya tidak disemak.";
        private const string Unact = " 2 boleh tindakan, 1 tiada template jenis tersebut wujud dalam model (Section ×1) — tidak boleh disapukan sehingga template jenis itu diwujudkan.";

        // name -> the BM remark of one branch of a checker
        internal static readonly Dictionary<string, string> Samples = new()
        {
            ["nothing-grids"] = $"Tiada grid, aras atau pelan lantai dalam model, jadi peraturan \"{RGrid}\" tiada data untuk disemak (0 diperiksa) — semak manual.",
            ["nothing-materials"] = $"Tiada elemen Walls/Floors/Ceilings/Roofs dalam model, jadi peraturan \"{RMat}\" tiada data untuk disemak (0 diperiksa) — semak manual.",
            ["nothing-views"] = $"Tiada view grafik dalam model, jadi peraturan \"{RVt}\" tiada data untuk disemak (0 diperiksa) — semak manual.",
            ["nothing-bucket"] = $"Tiada view grafik dalam model, jadi peraturan \"{RBucket}\" tiada data untuk disemak (0 diperiksa) — semak manual.",
            ["filename-pass"] = $"Format dijangka: {RFile}; dijumpai: \"JKR-AR-001\" (3 segmen) — patuh.",
            ["filename-suggest"] = $"Format dijangka: {RFile}; dijumpai: \"Model1\" (1 segmen). Cadangan nama: \"JKR-AR-Model1\".",
            ["filename-nosuggest"] = $"Format dijangka: {RFile}; dijumpai: \"Model1\" (1 segmen). Tiada cadangan automatik yang yakin — namakan semula fail secara manual mengikut garis panduan penamaan.",
            ["grid-notverifiable"] = "Semakan perlukan Project Base Point berada <=10 mm dari persilangan grid 'A' dan '1'. Model ada 3 grid (G1, G2, G3) tetapi tiada yang bernama 'A' atau '1' (atau berawalan projek, cth 'PT-A' / 'PT1'). Semak manual.",
            ["grid-parallel"] = "Grid 'A' dan grid '1' selari pada pelan, jadi tiada persilangan untuk diukur — semak manual.",
            ["basepoint-pass"] = "Had dijangka: <=10 mm dari persilangan grid A/1; dijumpai sisihan 2.5 mm — patuh.",
            ["basepoint-fail"] = "Had dijangka: <=10 mm dari persilangan grid A/1; dijumpai sisihan 25.4 mm. Selaraskan kedudukan model ke grid A dan 1.",
            ["basepoint-fail-prefixed"] = "Had dijangka: <=10 mm dari persilangan grid PT-A/PT1 (Grid A/1); dijumpai sisihan 25.4 mm. Selaraskan kedudukan model ke grid PT-A dan PT1.",
            ["grids-pass"] = $"Peraturan: {RGrid}. Dijumpai 8 grid (3 huruf, 5 nombor, 2 sub-grid; awalan projek 'PT' pada 6 grid), 4 aras, semua bernama unik; semua 4 pelan lantai terikat pada aras — patuh.",
            ["grids-pass-plain"] = $"Peraturan: {RGrid}. Dijumpai 8 grid (3 huruf, 5 nombor), 4 aras, semua bernama unik; semua 4 pelan lantai terikat pada aras — patuh.",
            ["grids-fail"] = $"Peraturan: {RGrid}. Dijumpai 8 grid, 4 aras. 1/8 grid tanpa nama. 2 nama grid berganda (cth: A x2, B x2). 1/4 aras tanpa nama. 1 nama aras berganda (cth: Level 1 x2). 2/4 pelan lantai tanpa aras (cth: Plan A, Plan B)." + Full,
            ["grids-fail-none"] = $"Peraturan: {RGrid}. Dijumpai 0 grid, 0 aras. Tiada grid dalam model. Tiada aras dalam model.",
            ["dept-zero"] = $"Peraturan: {RDept}. Dijumpai 0 bilik yang diletakkan (placed Room) dalam model — letak Room dahulu sebelum kategori jabatan boleh disemak.",
            ["dept-pass"] = $"Peraturan: {RDept}. Semua 10 bilik ada nilai Department — patuh.",
            ["dept-fail"] = $"Peraturan: {RDept}. 3/10 bilik tiada nilai (cth: G03, G04, 103). Isi parameter Department untuk bilik tersebut.",
            ["materials-capped"] = $"Peraturan: {RMat}. 5000 elemen pertama semuanya ada material (Walls 0/3000, Floors 0/2000), tetapi model melebihi had imbasan jadi elemen selebihnya tidak disemak — semak manual." + Cap,
            ["materials-pass"] = $"Peraturan: {RMat}. Semua 40 elemen ada material (Walls 0/20, Floors 0/20) — patuh.",
            ["materials-pass-capped"] = $"Peraturan: {RMat}. Semua 40 elemen ada material (Walls 0/20, Floors 0/20) — patuh." + Cap,
            ["materials-fail"] = $"Peraturan: {RMat}. 2/40 elemen tiada material (tanpa material/diimbas: Walls 2/20; cth: Walls W1 id 11, Walls W2 id 12). Tetapkan material pada jenis elemen berkenaan.",
            ["materials-fail-full-capped"] = $"Peraturan: {RMat}. 12/40 elemen tiada material (tanpa material/diimbas: Walls 12/20; cth: Walls W1 id 11). Tetapkan material pada jenis elemen berkenaan." + Full + Cap,
            ["projinfo-none"] = "Dokumen tiada elemen Project Information untuk dibaca — semak manual di Manage > Project Information.",
            ["projinfo-pass"] = $"Peraturan: {RProj}. Keenam-enam medan terisi (Project Name \"Clinic\", Project Number \"P-01\") — patuh.",
            ["projinfo-fail"] = $"Peraturan: {RProj}. 2/6 medan belum dikemaskini: Project Name (kosong), Client Name (\"Owner\" — nilai lalai). Isi di Manage > Project Information.",
            ["vt-pass"] = $"Peraturan: {RVt}. Semua 12 view ada template — patuh.",
            ["vt-fail"] = $"Peraturan: {RVt}. 3/12 view tiada template (cth: Level 1, Level 2, Section 1). Sapukan template daripada templat seni bina.",
            ["vt-fail-unactionable"] = $"Peraturan: {RVt}. 3/12 view tiada template (cth: Level 1, Level 2). Sapukan template daripada templat seni bina." + Unact + Full,
            ["vt-partial"] = $"Peraturan: {RVt}. 9/12 view ada template; 3 view tiada template tetapi tiada template jenis tersebut wujud dalam model (Section ×3; cth: Section 1, Section 2, Section 3) — tidak boleh tindakan. Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula.",
            ["bucket-pass"] = $"Peraturan: {RBucket}. Dijumpai 2 view (cth: BOMBA Level 1, BOMBA Level 2) — patuh.",
            ["bucket-fail"] = $"Peraturan: {RBucket}. Tiada nama view mengandungi 'BOMBA' daripada 12 view yang disemak. Wujudkan view BOMBA untuk tujuan tersebut.",
            ["areaplan-pass"] = $"Peraturan: {RArea}. Dijumpai 2 Area Plan (Area 1, Area 2) — patuh.",
            ["areaplan-fail"] = $"Peraturan: {RArea}. Dijumpai 0 Area Plan dalam model — jana analisis ruang/zon daripada Area Plan.",
            ["legend-pass"] = $"Peraturan: {RLegend}. Dijumpai 2 Legend (Notes, Symbols) — patuh.",
            ["legend-fail"] = $"Peraturan: {RLegend}. Dijumpai 0 view Legend dalam model — sediakan Legend untuk komponen dan nota am.",
            ["sched-pass"] = $"Peraturan: {RSched}. Ketiga-tiga jadual wajib dijumpai daripada 9 jadual dalam model (Schedule of Accommodation: SOA; Building Component Schedule: Door schedule; Material Takeoff Schedule: MTO) — patuh.",
            ["sched-fail"] = $"Peraturan: {RSched}. Tiada nama jadual yang sepadan untuk: Material Takeoff Schedule (9 jadual dalam model disemak). Jana jadual tersebut.",
            ["sheets-zero"] = $"Peraturan: {RSheet}. Dijumpai 0 Sheet dalam model — sediakan helaian lukisan seni bina.",
            ["sheets-pass"] = $"Peraturan: {RSheet}. Semua 10 sheet mengandungi view — patuh.",
            ["sheets-fail"] = $"Peraturan: {RSheet}. 2/10 sheet kosong (cth: A-101 Plan, A-102 Plan). Susun view pada sheet berkenaan.",
            ["tb-zero"] = $"Peraturan: {RTb}. Dijumpai 0 title block pada mana-mana sheet — gunakan JKR Title Block.",
            ["tb-pass"] = $"Peraturan: {RTb}. Semua 10 title block sepadan — patuh.",
            ["tb-fail"] = $"Peraturan: {RTb}. 2/10 title block tidak sepadan (cth: A-101: Generic; A-102: Generic). Tukar kepada JKR Title Block.",
            ["links-none"] = "Tiada Revit link dalam model, jadi tiada status pautan untuk disemak — sahkan secara manual sama ada pautan memang tidak diperlukan.",
            ["links-pass"] = $"Peraturan: {RLink}. Semua 3 link berstatus Loaded — patuh.",
            ["links-fail"] = $"Peraturan: {RLink}. 1/3 link bukan Loaded: Site (NotLoaded). Reload atau betulkan laluan link.",
            ["viewset-none"] = "Baris tidak menyatakan set view (BOMBA/PBT) yang dimaksudkan — semak manual.",
            ["viewset-zero"] = $"Peraturan: {RBomba}. Tiada nama view mengandungi 'BOMBA' daripada 12 view yang disemak — wujudkan view BOMBA dan sapukan view template.",
            ["viewset-pass"] = $"Peraturan: {RBomba}. Semua 4 view BOMBA ada template — patuh.",
            ["viewset-fail"] = $"Peraturan: {RBomba}. 1/4 view BOMBA tiada template (cth: BOMBA 1). Sapukan template BOMBA.",
            ["viewset-fail-unactionable"] = $"Peraturan: {RBomba}. 2/4 view BOMBA tiada template (cth: BOMBA 1). Sapukan template BOMBA." + Unact,
            ["viewset-partial"] = $"Peraturan: {RBomba}. 3/4 view BOMBA ada template; 1 view BOMBA tiada template tetapi tiada template jenis tersebut wujud dalam model (Section ×1; cth: BOMBA 1) — tidak boleh tindakan. Wujudkan template jenis itu dahulu jika dikehendaki, kemudian semak semula.",
            ["empty-expected"] = "0 elemen Columns dijumpai dalam model walaupun kategori ini biasanya wajib bagi model seni bina — sahkan sama ada memang tiada dalam skop projek. (Kualiti/maklumat/geometri: semak manual.)",
            ["empty-optional"] = "0 elemen Columns dijumpai dalam model dan kategori ini tidak wajib — baris ini tidak berkenaan, atau semak manual jika sepatutnya ada.",
            ["types-none"] = $"3 elemen Rooms tetapi 0 jenis (ElementType) untuk disemak, jadi peraturan \"{RType}\" tidak terpakai di sini — semak manual." + Q,
            ["types-unused"] = $"Peraturan: {RType}. 3 elemen Columns tetapi tiada satu pun menggunakan 2 jenis kategori ini (0 contoh diletakkan bagi setiap jenis) — tiada nama jenis yang digunakan untuk dinilai; semak manual." + Q,
            ["types-pass"] = $"Peraturan: {RType}. 12 elemen, 3 jenis digunakan — semua nama jenis yang digunakan patuh. 2 jenis tanpa contoh diletakkan tidak dikira (lihat unused_types). 1 elemen tanpa jenis tidak dapat dinilai." + Q,
            ["types-fail"] = $"Peraturan: {RType}. 2/3 jenis yang digunakan tidak patuh, meliputi 5/12 elemen (cth: \"Curtain Wall\" ×3, \"Generic\" ×2). Cadangan: \"Generic\" → \"jkrAR_Generic\". 1 nama tiada cadangan automatik yang yakin — namakan semula secara manual mengikut format JKR. 2 jenis tanpa contoh diletakkan tidak dikira (lihat unused_types)." + Q + Full,
            ["types-fail-plain"] = $"Peraturan: {RType}. 1/3 jenis yang digunakan tidak patuh, meliputi 3/12 elemen (cth: \"Curtain Wall\" ×3)." + Q,
            ["rooms-zero"] = $"Peraturan: {RRoom}. Dijumpai 0 bilik yang diletakkan (placed Room) dalam model. Letak Room dahulu sebelum Number/Name boleh disemak." + Q,
            ["rooms-zero-unplaced"] = $"Peraturan: {RRoom}. Dijumpai 0 bilik yang diletakkan (placed Room) — 4 Room wujud tetapi tidak diletakkan (Area 0). Letak Room dahulu sebelum Number/Name boleh disemak." + Q,
            ["rooms-optional"] = "0 bilik diletakkan dalam model dan kategori ini tidak wajib — baris ini tidak berkenaan, atau semak manual jika sepatutnya ada.",
            ["rooms-unreadable"] = $"Peraturan: {RRoom}. 10 bilik diletakkan tetapi parameter Number/Name tidak dapat dibaca — semak manual.",
            ["rooms-pass"] = $"Peraturan: {RRoom}. Semua 8 bilik yang diletakkan ada Number dan Name — patuh. (2 bilik tidak dapat dibaca, tidak dikira.)" + Q,
            ["rooms-fail"] = $"Peraturan: {RRoom}. 2/10 bilik tidak lengkap (1 tiada Number, 1 tiada Name; cth: [101], [102]). Isi Number dan Name untuk bilik tersebut." + Q,
            ["rooms-fail-unread-full"] = $"Peraturan: {RRoom}. 2/8 bilik tidak lengkap (1 tiada Number, 1 tiada Name; cth: [101], [102]). Isi Number dan Name untuk bilik tersebut. (2 bilik tidak dapat dibaca, tidak dikira.)" + Q + Full,
            ["heading"] = "Tajuk seksyen — tiada kriteria untuk diskor. Konteks model: 2 workset. Lihat baris di bawah.",
            ["nocheck"] = "Tiada semakan automatik untuk baris ini. Konteks model: 0 workset. Semak manual.",
            ["checker-error"] = "Semakan automatik gagal (grids_levels) — semak manual.",
        };

        public static TheoryData<string> SampleNames()
        {
            var d = new TheoryData<string>();
            foreach (var k in Samples.Keys) d.Add(k);
            return d;
        }

        // Malay words of the legacy remarks. "semak"/"manual" are the deliberate "Semak manual" label and stay.
        private static readonly Regex Malay = new(@"\b(dijangka|dijumpai|segmen|patuh|elemen|tiada|bilik|medan|Peraturan|Isi|Semua|Tetapkan|Selaraskan|dari|dan|atau|setiap|ada|nilai|kosong|lalai|jenis|sepadan|gagal|dikira|boleh|tindakan|wujudkan|jadual|laluan|betulkan|diletakkan|disemak|bernama|berganda|tanpa|Sapukan|Susun|Tukar|Jana|Letak|Cadangan|namakan|semula|daripada|untuk|yang|pada|dalam|jadi|dibaca|dikemaskini|belum|terisi|Dokumen|Baris|menyatakan|dimaksudkan|Keenam|Ketiga|wajib|helaian|lukisan|sediakan|gunakan|tidak|diimbas|pautan|kategori|nama|selebihnya|dihadkan|imbasan)\b", RegexOptions.IgnoreCase);

        [Theory]
        [MemberData(nameof(SampleNames))]
        public void Every_legacy_remark_template_has_an_English_text(string name)
        {
            var bm = Samples[name];
            var en = PaneText.AuditRemarkEn(bm);
            Assert.True(en != null, $"[{name}] AuditRemarkEn returned null (BM shown in English mode): {bm}");
            Assert.False(Malay.IsMatch(en), $"[{name}] 'English' text still has Malay: {en}");
            Assert.Equal(bm, PaneText.AuditRemark(bm).In(PanelStrings.Ms));
            Assert.Equal(en, PaneText.AuditRemark(bm).In(PanelStrings.En));
        }

        [Fact]
        public void English_texts_are_exact_for_representative_templates()
        {
            Assert.Equal("Rule: every placed room (Room) has a Number and a Name. 2/10 rooms incomplete (1 without Number, 1 without Name; e.g. [101], [102]). Fill in Number and Name for those rooms. (Quality/information/geometry: semak manual.)",
                PaneText.AuditRemarkEn(Samples["rooms-fail"]));
            Assert.Equal("Rule: every view with 'BOMBA' in its name has a View Template. 2/4 BOMBA views have no template (e.g. BOMBA 1). Apply the BOMBA template. 2 actionable, 1 have no template of that type in the model (Section ×1) — cannot be applied until a template of that type is created.",
                PaneText.AuditRemarkEn(Samples["viewset-fail-unactionable"]));
            Assert.Equal("Rule: the used type names (ElementType) follow the JKR format — JKR code '(XXnnn)' / prefix 'jkrAR' / material name. 1/3 used types do not comply, covering 3/12 elements (e.g. \"Curtain Wall\" ×3). (Quality/information/geometry: semak manual.)",
                PaneText.AuditRemarkEn(Samples["types-fail-plain"]));
            Assert.Equal("Expected limit: <=10 mm from the intersection of grid PT-A/PT1 (Grid A/1); found offset 25.4 mm. Adjust the model position to grid PT-A and PT1.",
                PaneText.AuditRemarkEn(Samples["basepoint-fail-prefixed"]));
            Assert.Equal("The automatic check failed (grids_levels) — semak manual.", PaneText.AuditRemarkEn(Samples["checker-error"]));
        }

        [Theory]
        [InlineData("sekurang-kurangnya satu view dengan 'PBT' dalam namanya", "at least one view with 'PBT' in its name")]
        [InlineData("setiap view dengan 'PBT' dalam namanya ada View Template", "every view with 'PBT' in its name has a View Template")]
        public void Dynamic_rule_strings_have_an_English_text(string bm, string en) => Assert.Equal(en, PaneText.AuditRuleEn(bm));

        [Fact]
        public void Schedule_rule_string_has_an_English_text()
        {
            var en = PaneText.AuditRuleEn(RSched);
            Assert.StartsWith("schedule name contains (including the JKR prefix): Schedule of Accommodation (accommodation|soa)", en);
            Assert.EndsWith("/ component schedule (door|window|wall|floor|ceiling|roof|stair|railing + schedule)", en);
            Assert.False(Malay.IsMatch(en), en);
        }

        [Fact]
        public void A_remark_with_an_unknown_rule_or_tail_stays_as_written()
        {
            Assert.Null(PaneText.AuditRemarkEn("Peraturan: satu peraturan baharu. Semua 3 bilik ada nilai — patuh."));
            Assert.Null(PaneText.AuditRemarkEn($"Peraturan: {RVt}. Ayat baharu yang belum diterjemah."));
            Assert.Null(PaneText.AuditRemarkEn("Format dijangka: x; dijumpai: y."));
            Assert.Equal("satu peraturan baharu", PaneText.AuditRuleEn("satu peraturan baharu"));
        }

        // ── guard: a new or reworded Remark in the checker source must get a template here ──
        // hash of the normalised Remark statement -> the samples above that cover its branches.
        // When this test fails: add/adjust the PaneText template, add a sample, then update the hash.
        private static readonly (string Hash, string[] Names)[] Registry =
        {
            ("98bb0db6a4", new[] { "nothing-grids", "nothing-materials", "nothing-views", "nothing-bucket" }),
            ("557dc905d3", new[] { "filename-pass" }),
            ("a330bae6c9", new[] { "filename-suggest", "filename-nosuggest" }),
            ("3f9af2a0d4", new[] { "grid-notverifiable" }),
            ("6a997466c5", new[] { "grid-parallel" }),
            ("f14c198d70", new[] { "basepoint-pass", "basepoint-fail", "basepoint-fail-prefixed" }),
            ("8bf7be4a60", new[] { "grids-pass", "grids-pass-plain", "grids-fail", "grids-fail-none" }),
            ("43864db6f9", new[] { "dept-zero" }),
            ("7962f703a1", new[] { "dept-pass", "dept-fail" }),
            ("5598e62244", new[] { "materials-capped" }),
            ("0e8124a444", new[] { "materials-pass", "materials-pass-capped", "materials-fail", "materials-fail-full-capped" }),
            ("a1487644f9", new[] { "projinfo-none" }),
            ("17a4be8f70", new[] { "projinfo-pass", "projinfo-fail" }),
            ("7685ab7620", new[] { "vt-pass", "vt-fail", "vt-fail-unactionable", "vt-partial" }),
            ("c0c33b4895", new[] { "bucket-pass", "bucket-fail" }),
            ("2e2ace6c00", new[] { "areaplan-pass", "areaplan-fail" }),
            ("20eb43e393", new[] { "legend-pass", "legend-fail" }),
            ("ec82944bf9", new[] { "sched-pass", "sched-fail" }),
            ("3b35759183", new[] { "sheets-zero" }),
            ("b00bbb09f0", new[] { "sheets-pass", "sheets-fail" }),
            ("b47a11a9b3", new[] { "tb-zero" }),
            ("ac7c620d7d", new[] { "tb-pass", "tb-fail" }),
            ("04e5473b92", new[] { "links-none" }),
            ("5aa438f4b3", new[] { "links-pass", "links-fail" }),
            ("bc254412d0", new[] { "viewset-none" }),
            ("817e8618da", new[] { "viewset-zero" }),
            ("90317f7086", new[] { "viewset-pass", "viewset-fail", "viewset-fail-unactionable", "viewset-partial" }),
            ("683c89c436", new[] { "empty-expected" }),
            ("5769e41bbc", new[] { "empty-optional" }),
            ("95e743ac56", new[] { "types-none" }),
            ("f1f86ad75c", new[] { "types-unused" }),
            ("4085529218", new[] { "types-pass", "types-fail", "types-fail-plain" }),
            ("c7dea07f7d", new[] { "rooms-zero", "rooms-zero-unplaced" }),
            ("2df74a78cf", new[] { "rooms-optional" }),
            ("d9a0364341", new[] { "rooms-unreadable" }),
            ("cec3413ca7", new[] { "rooms-pass", "rooms-fail", "rooms-fail-unread-full" }),
            ("e681e6b684", new[] { "heading" }),
            ("2fe4381436", new[] { "nocheck" }),
            ("ff090697d0", new[] { "checker-error" }),
        };

        [Fact]
        public void Checker_remark_sites_are_all_covered()
        {
            var sites = new[] { @"BinaVibe\Mcp\Tools\Audit\AuditCheckers.cs", @"BinaVibe\Mcp\Tools\Audit\AuditTools.cs" }
                .SelectMany(f => LegacyRemarkSites.In(LegacyRemarkSites.SourceOf(f))).ToList();
            var known = Registry.Select(r => r.Hash).ToHashSet();

            var added = sites.Where(s => !known.Contains(s.Hash)).Select(s => s.Hash + " | " + s.Text).ToList();
            Assert.True(added.Count == 0,
                "A Remark in the checker source is new or reworded; give it a PaneText.AuditRemarkEn template + a sample, then register its hash:\n" + string.Join("\n", added));

            var gone = known.Except(sites.Select(s => s.Hash)).ToList();
            Assert.True(gone.Count == 0, "Registered remark sites no longer in the source (reworded or removed): " + string.Join(", ", gone));

            foreach (var (hash, names) in Registry)
            {
                Assert.NotEmpty(names);
                foreach (var n in names)
                {
                    Assert.True(Samples.ContainsKey(n), $"[{hash}] unknown sample '{n}'");
                    Assert.NotNull(PaneText.AuditRemarkEn(Samples[n]));
                }
            }
            var covered = Registry.SelectMany(r => r.Names).ToHashSet();
            Assert.Empty(Samples.Keys.Where(k => !covered.Contains(k)));
        }

        // ── review nits (L-n1 .. L-n3) ──

        private const string GridRule = "Project Base Point berada <=10 mm dari persilangan grid 'A' dan '1'";

        [Fact]
        public void A_regex_timeout_leaves_the_remark_in_BM()
        {
            Assert.Null(PaneText.TranslateOrNull(_ => throw new RegexMatchTimeoutException("x", "y", System.TimeSpan.FromMilliseconds(1)), "text"));
            Assert.Equal("ok", PaneText.TranslateOrNull(s => "ok", "text"));

            // a real catastrophic-backtracking match with a tiny timeout really throws, and is caught
            var slow = new Regex("^(a+)+$", RegexOptions.None, System.TimeSpan.FromMilliseconds(1));
            var input = new string('a', 60) + "b";
            Assert.Throws<RegexMatchTimeoutException>(() => slow.IsMatch(input));
            Assert.Null(PaneText.TranslateOrNull(s => slow.IsMatch(s) ? "x" : "y", input));
        }

        [Fact]
        public void Every_translator_regex_has_a_match_timeout()
        {
            var seen = 0;
            foreach (var f in typeof(PaneText).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            {
                var v = f.GetValue(null);
                var items = v is System.Array arr ? arr.Cast<object>() : new[] { v };
                foreach (var item in items)
                {
                    var regexes = item is Regex r ? new[] { r }
                        : item is System.Runtime.CompilerServices.ITuple tup
                            ? Enumerable.Range(0, tup.Length).Select(i => tup[i]).OfType<Regex>().ToArray()
                            : new Regex[0];
                    foreach (var re in regexes)
                    {
                        seen++;
                        Assert.True(re.MatchTimeout != Regex.InfiniteMatchTimeout, $"{f.Name}: regex without a match timeout: {re}");
                    }
                }
            }
            Assert.True(seen > 60, "expected to inspect the translator regexes, saw " + seen);
        }

        [Fact]
        public void Grid_names_with_the_connector_word_are_not_rewritten()
        {
            var curved = $"Semakan perlukan {GridRule}. Grid 'Blok A dan B' dan '1' bukan garisan lurus, jadi persilangan tidak boleh dihitung. Semak manual.";
            Assert.Equal($"The check needs the Project Base Point to be within 10 mm of the intersection of grids 'A' and '1'. Grid 'Blok A dan B' and '1' is not a straight line, so the intersection cannot be calculated. Semak manual.",
                PaneText.AuditRemarkEn(curved));
            var single = $"Semakan perlukan {GridRule}. Grid 'Blok A dan B' bukan garisan lurus, jadi persilangan tidak boleh dihitung. Semak manual.";
            Assert.Contains("Grid 'Blok A dan B' is not a straight line", PaneText.AuditRemarkEn(single));

            var missing = $"Semakan perlukan {GridRule}. Model ada 2 grid (G1, G2) tetapi tiada yang bernama 'A' atau '1' (atau berawalan projek, cth 'PT-A' / 'PT1'). Semak manual.";
            Assert.Contains("none is named 'A' or '1' (or prefixed", PaneText.AuditRemarkEn(missing));
            // an unexpected missing-name text is not guessed at
            Assert.Null(PaneText.AuditRemarkEn(missing.Replace("'A' atau '1'", "'Blok A atau B'")));

            var bp = "Had dijangka: <=10 mm dari persilangan grid Blok A dan B/1 (Grid A/1); dijumpai sisihan 25.4 mm. Selaraskan kedudukan model ke grid Blok A dan B dan 1.";
            Assert.Equal("Expected limit: <=10 mm from the intersection of grid Blok A dan B/1 (Grid A/1); found offset 25.4 mm. Adjust the model position to grid Blok A dan B and 1.",
                PaneText.AuditRemarkEn(bp));
            // two slashes in a name: the names are kept verbatim, nothing is split
            var bp2 = "Had dijangka: <=10 mm dari persilangan grid A/B/1 (Grid A/1); dijumpai sisihan 25.4 mm. Selaraskan kedudukan model ke grid A dan B dan 1.";
            Assert.Contains("Adjust the model position to grid A/B/1.", PaneText.AuditRemarkEn(bp2));
            // inconsistent text: stays BM
            Assert.Null(PaneText.AuditRemarkEn(bp.Replace("grid Blok A dan B dan 1", "grid X")));
        }

        [Fact]
        public void Project_information_values_with_the_marker_words_are_not_rewritten()
        {
            var pass = $"Peraturan: {RProj}. Keenam-enam medan terisi (Project Name \"Klinik (kosong) nilai lalai\", Project Number \"P (kosong)\") — patuh.";
            Assert.Contains("(Project Name \"Klinik (kosong) nilai lalai\", Project Number \"P (kosong)\")", PaneText.AuditRemarkEn(pass));

            var fail = $"Peraturan: {RProj}. 2/6 medan belum dikemaskini: Project Name (kosong), Client Name (\"Own (kosong) nilai lalai er\" — nilai lalai). Isi di Manage > Project Information.";
            Assert.Equal("Rule: Project Name/Number/Address/Client Name/Status/Issue Date are filled and not the Revit default. 2/6 fields not yet updated: Project Name (empty), Client Name (\"Own (kosong) nilai lalai er\" — Revit default). Fill them in under Manage > Project Information.",
                PaneText.AuditRemarkEn(fail));
            // a list that is not made of the checker's items stays BM
            Assert.Null(PaneText.AuditRemarkEn(fail.Replace("Project Name (kosong)", "Nama Projek (kosong)")));
        }

        [Fact]
        public void The_source_scan_also_sees_Remark_plus_equals()
        {
            var src = string.Join(System.Environment.NewLine, "o.Remark = \"Satu.\";", " o.Remark += \" Dua.\";", " Remark += \" Tiga.\";", " var r = o.Remark;");
            Assert.Equal(3, LegacyRemarkSites.In(src).Count);
        }
    }
}
