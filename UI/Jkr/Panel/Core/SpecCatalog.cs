using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>How a spec check is judged (JKR-Model-Audit-Spec.xlsx, sheet
    /// "Peraturan comply", column "Kelas cell").</summary>
    public enum SpecCellClass
    {
        /// <summary>AI-verifiable: the scan can say comply / not comply.</summary>
        Ai,
        /// <summary>Human-decides (principle 0.4): never ✓/✗, never in the %.</summary>
        Human,
        /// <summary>List only: shown to the reader, no verdict, no pending count.</summary>
        List,
    }

    /// <summary>A Borang item ("A2", "C1.6", "D-b"). The title is the spec's "Borang A-E"
    /// "Tajuk", verbatim, as BM (A–C are the printed form's English wording, D is BM);
    /// EN is the same for A–C and a translation for D.</summary>
    public sealed class SpecItem
    {
        public SpecItem(string section, string no, Text title) { Section = section; No = no; Title = title; }
        public string Section { get; }
        public string No { get; }
        public Text Title { get; }
    }

    public sealed class SpecCheck
    {
        public SpecCheck(string id, string item, Text title, SpecCellClass cls, string reference)
        {
            Id = id; Item = item; Title = title; Class = cls; Reference = reference;
        }
        /// <summary>The spec's "ID semakan": "A2.1", "C1.6.3", "Db.1", "E.1".</summary>
        public string Id { get; }
        public string Item { get; }
        /// <summary>BM: the spec's "Apa yang disemak", verbatim; EN: its translation.</summary>
        public Text Title { get; }
        public SpecCellClass Class { get; }
        /// <summary>The spec's "Rujukan" (NS clause, Buku Parameter page, Borang row, meeting time).</summary>
        public string Reference { get; }
        public string Section => SpecCatalog.SectionOf(Item);
        /// <summary>The spec's "COMPLY (✓) bila" (BM verbatim; EN translation). "—" = the spec has none.</summary>
        public Text Comply { get; set; } = Text.Same("");
        /// <summary>The spec's "TIDAK COMPLY (✗) bila" (BM verbatim; EN translation).</summary>
        public Text NotComply { get; set; } = Text.Same("");
        /// <summary>The spec's "SEMAK MANUAL / nota" (BM verbatim; EN translation).</summary>
        public Text Manual { get; set; } = Text.Same("");
    }

    /// <summary>
    /// The 61 checks of the JKR Model Audit spec (JKR-Model-Audit-Spec.xlsx, sheet
    /// "Peraturan comply"), grouped as the spec groups them: Borang BIM010
    /// section (A–E) → item → check. Totals as the spec states them: 61 checks —
    /// 39 AI-verifiable, 8 human-decides, 14 list only. WPF- and Revit-free.
    /// </summary>
    public static class SpecCatalog
    {
        private const SpecCellClass Ai = SpecCellClass.Ai;
        private const SpecCellClass Human = SpecCellClass.Human;
        private const SpecCellClass List = SpecCellClass.List;

        private static Text T(string ms, string en) => new Text(ms, en);
        private static Text Same(string s) => Text.Same(s);
        /// <summary>The spec's empty cell, kept verbatim.</summary>
        public const string EmptyCell = "—";
        private static readonly Text Dash = Text.Same(EmptyCell);

        /// <summary>True when a criterion cell is the spec's "—" (or blank).</summary>
        public static bool IsEmpty(Text cell) => cell == null || string.IsNullOrWhiteSpace(cell.Ms) || cell.Ms.Trim() == EmptyCell;

        /// <summary>Borang A-E items, "Tajuk" verbatim (the sheet has no E row: E keeps
        /// the heading of model-audit-spec.md §E).</summary>
        public static readonly IReadOnlyList<SpecItem> Items = new[]
        {
            new SpecItem("A", "A1", Same("Architectural Model developed in accordance to specified architectural design")),
            new SpecItem("A", "A2", Same("Model files named according to PWD BIM Guidelines")),
            new SpecItem("A", "A3", Same("Model structured according to rules that are easily managed (block / tower / podium)")),
            new SpecItem("A", "A4", Same("Model positioned to Project Base Point (Grid A/1)")),
            new SpecItem("A", "A5", Same("Model organised using architectural gridlines and levels")),
            new SpecItem("A", "A6", Same("Rooms are categorised by departments")),
            new SpecItem("A", "A7", Same("All materials included in model elements")),
            new SpecItem("B", "B1", Same("Project Information should be updated")),
            new SpecItem("C", "C1.0", Same("Views (Architectural)")),
            new SpecItem("C", "C1.1", Same("Views created based on PWD architectural template")),
            new SpecItem("C", "C1.2", Same("WIP views")),
            new SpecItem("C", "C1.3", Same("PBT views")),
            new SpecItem("C", "C1.4", Same("BOMBA views")),
            new SpecItem("C", "C1.5", Same("Dokumen views")),
            new SpecItem("C", "C1.6", Same("Spatial & Zoning Analysis generated from Area Plan")),
            new SpecItem("C", "C2.1", Same("Legends")),
            new SpecItem("C", "C3.1", Same("Schedules / Quantities")),
            new SpecItem("C", "C4.1", Same("Sheets")),
            new SpecItem("C", "C4.2", Same("JKR Title Block with furnished information")),
            new SpecItem("C", "C5.1", Same("Link Files")),
            new SpecItem("D", "D-a", T("Naming ikut coding JKR", "Naming follows JKR coding")),
            new SpecItem("D", "D-b", T("Maklumat / parameter ikut Buku Parameter mengikut LOD", "Information / parameters per the Buku Parameter, by LOD")),
            new SpecItem("D", "D-c", T("Naming kena tally dengan parameter (kualiti integriti / geometri)", "Naming must tally with the parameters (integrity / geometry quality)")),
            new SpecItem("D", "D-d", T("Integriti: nama ↔ kategori", "Integrity: name ↔ category")),
            new SpecItem("D", "D-e", T("Baris khusus", "Special rows")),
            new SpecItem("E", "E", Same("Properties and Visibility Graphic: View Templates")),
        };

        /// <summary>"Peraturan comply", row by row: every cell verbatim in BM.</summary>
        public static readonly IReadOnlyList<SpecCheck> Checks = new[]
        {
            // ── A ──
            new SpecCheck("A1.1", "A1", T("Ketinggian siling (NS §4.1.9)",
                          "Ceiling height (NS §4.1.9)"), Ai, "NS §4.1.9")
            {
                Comply = T("Setiap aras: floor-to-ceiling >= 3000 mm; basement >= 2500 mm",
                           "Every level: floor-to-ceiling >= 3000 mm; basement >= 2500 mm"),
                NotComply = T("Mana-mana aras < 3000 mm (atau basement < 2500 mm) — senaraikan aras + nilai sebenar",
                              "Any level < 3000 mm (or basement < 2500 mm) — list the level + the actual value"),
                Manual = Dash,
            },
            new SpecCheck("A1.2", "A1", T("Saiz pintu (NS §5.8.5)",
                          "Door size (NS §5.8.5)"), Ai, "NS §5.8.5")
            {
                Comply = T("Pintu single leaf >= 900 mm; double >= 1500 mm; tinggi clear >= 2100 mm",
                           "Single-leaf door >= 900 mm; double >= 1500 mm; clear height >= 2100 mm"),
                NotComply = T("Mana-mana type pintu bawah nilai minimum — senaraikan `Type | lebar | tinggi`",
                              "Any door type below the minimum — list `Type | width | height`"),
                Manual = Dash,
            },
            new SpecCheck("A1.3", "A1", T("Pintu utama solid core (NS §5.8.1/5.8.3)",
                          "Main door solid core (NS §5.8.1/5.8.3)"), Ai, "NS §5.8.1, §5.8.3")
            {
                Comply = T("Pintu utama: solid core dan ketebalan >= 40 mm (parameter thickness terisi)",
                           "Main door: solid core and thickness >= 40 mm (thickness parameter filled)"),
                NotComply = T("Ketebalan < 40 mm, atau parameter tidak wujud pada pintu utama",
                              "Thickness < 40 mm, or the parameter does not exist on the main door"),
                Manual = T("Pintu utama tidak dapat dikenal pasti dari model — senaraikan calon, manusia sahkan",
                           "The main door cannot be identified from the model — list the candidates, a person confirms"),
            },
            new SpecCheck("A1.4", "A1", T("Flat roof / waterproofing (NS §5.2.3 + §5.14)",
                          "Flat roof / waterproofing (NS §5.2.3 + §5.14)"), Ai, "NS §5.2.3, §5.14")
            {
                Comply = T("Tiada RC flat roof; ATAU ada flat roof DAN elemen waterproofing/double roofing wujud dalam model",
                           "No RC flat roof; OR a flat roof AND a waterproofing / double-roofing element exists in the model"),
                NotComply = T("Ada RC flat roof tanpa sebarang elemen/material waterproofing",
                              "An RC flat roof without any waterproofing element or material"),
                Manual = T("Kewajaran penggunaan flat roof (justifikasi reka bentuk) — manusia",
                           "Whether a flat roof is justified (design justification) — a person"),
            },
            new SpecCheck("A1.5", "A1", T("Pagar hiasan (NS §4.8.3)",
                          "Decorative fence (NS §4.8.3)"), Ai, "NS §4.8.3")
            {
                Comply = T("Tinggi pagar hiasan >= 2400 mm",
                           "Decorative fence height >= 2400 mm"),
                NotComply = T("Tinggi < 2400 mm",
                              "Height < 2400 mm"),
                Manual = T("Tiada elemen pagar dalam model — sahkan skop projek",
                           "No fence element in the model — confirm the project scope"),
            },
            new SpecCheck("A1.6", "A1", T("Klausa NS lain (sustainability, IBS, landskap)",
                          "Other NS clauses (sustainability, IBS, landscape)"), List, "NS (lain-lain)")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAIKAN sahaja klausa NS berkenaan yang tidak dapat disemak dari model. Tiada verdict, tiada kiraan pending.",
                           "LIST only the relevant NS clauses that cannot be checked from the model. No verdict, no pending count."),
            },
            new SpecCheck("A1.7", "A1", T("Skop A1 = keperluan dalam Need Statement sahaja",
                          "A1 scope = requirements in the Need Statement only"), List, "Prinsip 0.3, NS")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("A1 hanya semak keperluan yang TERTULIS dalam Need Statement. Konsep/estetika reka bentuk tidak dinilai (0.3) — nyatakan remark sahaja.",
                           "A1 only checks requirements WRITTEN in the Need Statement. Design concept / aesthetics are not assessed (0.3) — give a remark only."),
            },
            new SpecCheck("A2.1", "A2", T("Nama fail model ikut PWD BIM Guidelines",
                          "Model file name follows the PWD BIM Guidelines"), Ai, "F A.2, T 21:16")
            {
                Comply = T("Nama fail mengandungi semua segmen wajib (>=3 segmen mengikut garis panduan)",
                           "The file name has every required segment (>= 3 segments as the guidelines set)"),
                NotComply = T("Segmen tidak lengkap / format salah — papar pecahan segmen yang dikesan",
                              "Segments incomplete / wrong format — show the segments detected"),
                Manual = Dash,
            },
            new SpecCheck("A2.2", "A2", T("Kod model dikesan dari nama fail",
                          "Model code read from the file name"), Ai, "T 21:22")
            {
                Comply = T("Kod dikesan: W00 (model tunggal) atau WXnn (model pecah) — dibawa ke A3",
                           "Code detected: W00 (single model) or WXnn (split model) — carried to A3"),
                NotComply = T("Kod tidak dapat dibaca dari nama fail",
                              "The code cannot be read from the file name"),
                Manual = Dash,
            },
            new SpecCheck("A3.1", "A3", T("Model tunggal (kod W00)",
                          "Single model (code W00)"), Ai, "T 21:19, 21:22, 21:23")
            {
                Comply = T("Kod = W00 → TERUS COMPLY (model satu seketul, cth dewan)",
                           "Code = W00 → COMPLIES DIRECTLY (a one-piece model, e.g. a hall)"),
                NotComply = Dash,
                Manual = T("Jika model sepatutnya tunggal tetapi ada banyak link, nyatakan sebagai REMARK sahaja (bukan ✗) — menunggu pengesahan JKR",
                           "If the model should be single but has many links, state it as a REMARK only (not ✗) — awaiting JKR's confirmation"),
            },
            new SpecCheck("A3.2", "A3", T("Model pecah (kod WXnn) — link wujud",
                          "Split model (code WXnn) — links exist"), Ai, "T 21:19–21:21")
            {
                Comply = T("Setiap fail pecahan (WX01, WX02…) hadir sebagai link dalam model",
                           "Every part file (WX01, WX02…) is present as a link in the model"),
                NotComply = T("Ada pecahan yang tidak di-link — senaraikan fail yang hilang",
                              "A part is not linked — list the missing files"),
                Manual = Dash,
            },
            new SpecCheck("A3.3", "A3", T("Pecahan model vs senarai fail dalam BPEP",
                          "Model parts vs the file list in the BPEP"), Ai, "T 22:23–22:25")
            {
                Comply = T("Semua fail model yang disenaraikan dalam BPEP dijumpai sebagai link",
                           "Every model file listed in the BPEP is found as a link"),
                NotComply = T("Ada fail dalam BPEP tiada dalam model (atau sebaliknya)",
                              "A file in the BPEP is not in the model (or the other way round)"),
                Manual = T("BPEP tidak tersedia dalam RAG → tidak dapat disemak; nyatakan sebab",
                           "BPEP not available in RAG → cannot be checked; state why"),
            },
            new SpecCheck("A3.4", "A3", T("Model pecah tetapi tiada link langsung",
                          "Split model but no links at all"), List, "T 21:22, 21:24")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("TIDAK DAPAT DISEMAK — senaraikan sahaja dengan remark 'Model perlu di-link dahulu sebelum semakan A3 boleh dijalankan'",
                           "CANNOT BE CHECKED — list only, with the remark 'The model must be linked first before the A3 check can run'"),
            },
            new SpecCheck("A3.5", "A3", T("Cara pecahan model (penerangan)",
                          "How the model is split (description)"), List, "T 21:19–21:20")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAIKAN cara model dipecahkan (cth 'WX01 Tower, WX02 Podium, WX03 Basement'). Penerangan sahaja — tiada verdict, tiada kiraan pending.",
                           "LIST how the model is split (e.g. 'WX01 Tower, WX02 Podium, WX03 Basement'). Description only — no verdict, no pending count."),
            },
            new SpecCheck("A4.1", "A4", T("Kedudukan model pada Project Base Point (Grid A/1)",
                          "Model position at the Project Base Point (Grid A/1)"), Ai, "F A.4")
            {
                Comply = T("Sisihan dari Grid A/1 = 0 mm (dalam toleransi)",
                           "Offset from Grid A/1 = 0 mm (within tolerance)"),
                NotComply = T("Sisihan > toleransi — papar sisihan dalam mm (X/Y/Z)",
                              "Offset > tolerance — show the offset in mm (X/Y/Z)"),
                Manual = Dash,
            },
            new SpecCheck("A5.1", "A5", T("Bilangan grid dan aras",
                          "Number of grids and levels"), List, "F A.5")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAI sahaja: bil. grid + bil. aras (maklumat asas, tiada verdict)",
                           "LIST only: number of grids + number of levels (basic information, no verdict)"),
            },
            new SpecCheck("A5.2", "A5", T("Urutan (sequencing) grid line",
                          "Gridline sequencing"), Ai, "T 21:24–21:26")
            {
                Comply = T("Grid tersusun tanpa lompat: nombor 1,2,3,4… dan huruf A,B,C… (arah kiri→kanan atau terbalik, atas→bawah atau bawah→atas — semua diterima)",
                           "Grids in order with no jumps: numbers 1,2,3,4… and letters A,B,C… (left→right or reversed, top→bottom or bottom→top — all accepted)"),
                NotComply = T("Ada lompatan dalam urutan (cth 1,2,3 → 11) — senaraikan jurang",
                              "A jump in the sequence (e.g. 1,2,3 → 11) — list the gaps"),
                Manual = Dash,
            },
            new SpecCheck("A5.3", "A5", T("Sub-grid antara grid induk",
                          "Sub-grids between main grids"), Ai, "T 21:25")
            {
                Comply = T("Pola sub-grid sah: 1A,1B,1C atau A1,A2,A3 — disenaraikan di bawah grid induk",
                           "A valid sub-grid pattern: 1A,1B,1C or A1,A2,A3 — listed under the main grid"),
                NotComply = T("— (sub-grid TIDAK dikira salah)",
                              "— (sub-grids are NOT counted as wrong)"),
                Manual = Dash,
            },
            new SpecCheck("A6.1", "A6", T("Senarai room setiap aras",
                          "Rooms listed per level"), List, "T 21:26–21:33")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAI: `Aras | Nama room | Nombor | Department | Luas` untuk SETIAP aras (10 aras = 10 aras disemak)",
                           "LIST: `Level | Room name | Number | Department | Area` for EVERY level (10 levels = 10 levels checked)"),
            },
            new SpecCheck("A6.2", "A6", T("Ada slab mesti ada room",
                          "A slab must have a room"), Ai, "T 21:28–21:33")
            {
                Comply = T("Setiap kawasan berslab mempunyai room yang didefinisikan (termasuk lif, tangga, apron)",
                           "Every slab area has a defined room (lifts, stairs and aprons included)"),
                NotComply = T("Ada slab tanpa room — senaraikan `Aras | Lokasi grid | Jenis (lif/tangga/apron)`. INI ✗ UTAMA A6",
                              "A slab without a room — list `Level | Grid location | Type (lift/stair/apron)`. THIS IS THE MAIN ✗ OF A6"),
                Manual = Dash,
            },
            new SpecCheck("A6.3", "A6", T("Parameter Department",
                          "Department parameter"), Human, "T 21:26, 21:34")
            {
                Comply = T("Department terisi (jika jenis projek memerlukan: hospital/pejabat)",
                           "Department filled (if the project type needs it: hospital / office)"),
                NotComply = T("— (JANGAN flag MAJOR semata-mata Department kosong)",
                              "— (DO NOT flag MAJOR just because Department is empty)"),
                Manual = T("Department kosong = MAKLUMAT sahaja. Sama ada jenis projek memerlukan Department = manusia sahkan (sekolah/dewan tiada department)",
                           "An empty Department = INFORMATION only. Whether the project type needs a Department = a person confirms (schools / halls have no department)"),
            },
            new SpecCheck("A7.1", "A7", T("Setiap elemen ada material",
                          "Every element has a material"), Ai, "T 21:42–21:43")
            {
                Comply = T("Semua elemen dalam semua kategori mempunyai material (tidak kosong)",
                           "Every element in every category has a material (not empty)"),
                NotComply = T("Elemen tanpa material — senaraikan `Kategori | Type name | Bil. elemen`",
                              "Elements without a material — list `Category | Type name | No. of elements`"),
                Manual = Dash,
            },
            new SpecCheck("A7.2", "A7", T("Naming material ikut JKR",
                          "Material naming follows JKR"), Human, "T 21:41, 21:46, PB p.35")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("WARNING sahaja, BUKAN ✗: senaraikan material yang guna nilai default Revit atau nama bukan-JKR (cth 'Glass (door)', 'Metal White'). JKR sendiri tidak boleh kata ia salah; manusia nilai. Coding material yang wajib disemak di D-b",
                           "WARNING only, NOT ✗: list the materials that use a Revit default value or a non-JKR name (e.g. 'Glass (door)', 'Metal White'). JKR itself cannot call it wrong; a person judges. The material coding that is mandatory is checked in D-b"),
            },
            new SpecCheck("A7.3", "A7", T("Ketepatan pilihan material",
                          "Material choice is correct"), Human, "T 21:44")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("TIDAK DINILAI di A7 — hanya kewujudan + naming. Ketepatan material disemak di seksyen D",
                           "NOT ASSESSED in A7 — only existence + naming. Material correctness is checked in section D"),
            },
            // ── B ──
            new SpecCheck("B1.1", "B1", T("Field Project Information wajib mengikut LOi projek",
                          "Mandatory Project Information fields for the project LOI"), Ai, "PB p.30–34, T 21:51–21:54")
            {
                Comply = T("Semua field yang bertanda wajib pada LOi projek (PB p.30–34) terisi dengan nilai sebenar",
                           "Every field marked mandatory at the project LOI (PB p.30–34) is filled with a real value"),
                NotComply = T("Field wajib kosong ATAU masih nilai lalai Revit — senaraikan `Parameter | Wajib pada LOi | Nilai | Status`",
                              "A mandatory field is empty OR still a Revit default — list `Parameter | Mandatory at LOI | Value | Status`"),
                Manual = Dash,
            },
            new SpecCheck("B1.2", "B1", T("Disiplin lain (ST / ME / EL)",
                          "Other disciplines (ST / ME / EL)"), Ai, "T 21:55, PB p.120/161/209")
            {
                Comply = Dash,
                NotComply = T("Satu baris 'Tidak patuh' setiap disiplin + senarai field yang belum diisi sahaja",
                              "One 'Not compliant' row per discipline + a list of the unfilled fields only"),
                Manual = Dash,
            },
            new SpecCheck("B1.3", "B1", T("Ringkasan pengisian",
                          "Fill summary"), List, "T 21:54")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAI: 'n/m field diisi (x%)' — maklumat ringkasan",
                           "LIST: 'n/m fields filled (x%)' — summary information"),
            },
            // ── C ──
            new SpecCheck("C1.0.1", "C1.0", T("Baris induk Views (Architectural)",
                          "Parent row Views (Architectural)"), List, "T 21:56")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("TIADA SEMAKAN — baris induk sahaja; semak anak 1.1–1.6",
                           "NO CHECK — parent row only; check the children 1.1–1.6"),
            },
            new SpecCheck("C1.1.1", "C1.1", T("View template JKR AR wujud dalam projek",
                          "JKR AR template views exist in the project"), Ai, "T 21:56–22:00")
            {
                Comply = T("Setiap view dalam template JKR AR wujud dalam projek (dibanding dengan template, bukan dengan projek)",
                           "Every view of the JKR AR template exists in the project (compared with the template, not with the project)"),
                NotComply = T("View template yang dipadam — senaraikan view yang tiada",
                           "A template view was deleted — list the missing views"),
                Manual = T("Senarai view datang dari template JKR AR; tanpa senarai itu tidak dapat disemak — nyatakan sebab",
                           "The view list comes from the JKR AR template; without that list it cannot be checked — state the reason"),
            },
            new SpecCheck("C1.1.2", "C1.1", T("View template ditukar nama",
                          "Template views renamed"), Ai, "T 21:58")
            {
                Comply = T("Semua view template kekal dengan nama asal",
                           "Every template view keeps its original name"),
                NotComply = T("Nama view template ditukar (ditukar nama = SALAH; jarang berlaku)",
                           "A template view was renamed (renamed = WRONG; rare)"),
                Manual = T("Menunggu pengesahan JKR: adakah ditukar nama diterima asalkan view wujud ('cukup ada tu dah')",
                           "Awaiting JKR's confirmation: is a renamed view accepted as long as it exists ('cukup ada tu dah')"),
            },
            new SpecCheck("C1.1.3", "C1.1", T("Bilangan aras mencukupi",
                          "Number of levels is enough"), Human, "T 22:04–22:05")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("AI senaraikan bil. aras dikesan; MANUSIA sahkan cukup atau tidak",
                           "The AI lists the number of levels detected; a PERSON confirms whether it is enough"),
            },
            new SpecCheck("C1.2.1", "C1.2", T("Set view WIP setiap aras",
                          "WIP view set for every level"), List, "T 22:00–22:05")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAI SAHAJA `Jenis view | Aras | Nama view`, disusun ikut aras (floor plan, ceiling plan, section setiap aras hingga bumbung) supaya aras tanpa view kelihatan — JANGAN beri verdict comply",
                           "LIST ONLY `View type | Level | View name`, ordered by level (floor plan, ceiling plan, section for every level up to the roof) so a level without views shows — DO NOT give a comply verdict"),
            },
            new SpecCheck("C1.3.1", "C1.3", T("Set view PBT (floor plan, elevation, section)",
                          "PBT view set (floor plan, elevation, section)"), Ai, "T 22:01, 22:04")
            {
                Comply = T("Ketiga-tiga jenis wajib hadir dalam set PBT",
                           "All three required types are present in the PBT set"),
                NotComply = T("Mana-mana jenis tiada — checklist `floor plan ✓ | elevation ✓ | section ✗`",
                           "Any type missing — checklist `floor plan ✓ | elevation ✓ | section ✗`"),
                Manual = T("Menunggu pengesahan JKR: ✗ automatik, atau AI senaraikan dan manusia putuskan",
                           "Awaiting JKR's confirmation: automatic ✗, or the AI lists and a person decides"),
            },
            new SpecCheck("C1.4.1", "C1.4", T("Set view BOMBA (floor plan, section, drafting view)",
                          "BOMBA view set (floor plan, section, drafting view)"), Ai, "T 22:01, 22:05")
            {
                Comply = T("Ketiga-tiga jenis wajib hadir dalam set BOMBA",
                           "All three required types are present in the BOMBA set"),
                NotComply = T("Mana-mana jenis tiada — checklist",
                           "Any type missing — checklist"),
                Manual = T("Menunggu pengesahan JKR: ✗ automatik, atau AI senaraikan dan manusia putuskan",
                           "Awaiting JKR's confirmation: automatic ✗, or the AI lists and a person decides"),
            },
            new SpecCheck("C1.5.1", "C1.5", T("View Dokumen + status dalam sheet",
                          "Dokumen views + status on sheets"), List, "T 22:02–22:03")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAI `Nama view | Jenis | Aras | Dalam sheet? (biru)`",
                           "LIST `View name | Type | Level | On a sheet? (blue)`"),
            },
            new SpecCheck("C1.5.2", "C1.5", T("3D view setiap aras (set Dokumen)",
                          "3D view for every level (Dokumen set)"), Ai, "T 22:39–22:40")
            {
                Comply = T("Set view Dokumen ada satu 3D view bagi setiap aras",
                           "The Dokumen view set has one 3D view for every level"),
                NotComply = T("Aras tanpa 3D view Dokumen — cth '3D view setiap aras: 3/4 ✗ (Aras 03 tiada)'; nyatakan sebab",
                           "A level without a Dokumen 3D view — e.g. '3D view for every level: 3/4 ✗ (Aras 03 missing)'; state the reason"),
                Manual = Dash,
            },
            new SpecCheck("C1.6.1", "C1.6", T("Jadual ruang (room schedule) wujud",
                          "Room schedule exists"), Ai, "T 21:36–22:08")
            {
                Comply = T("Room schedule / Schedule of Accommodation wujud dalam projek",
                           "A room schedule / Schedule of Accommodation exists in the project"),
                NotComply = T("Tiada jadual ruang → ✗",
                              "No room schedule → ✗"),
                Manual = Dash,
            },
            new SpecCheck("C1.6.2", "C1.6", T("Setiap room hadir dalam jadual",
                          "Every room is in the schedule"), Ai, "N2, T 22:06–22:08")
            {
                Comply = T("Semua room dalam model hadir dalam jadual ruang",
                           "Every room in the model is in the room schedule"),
                NotComply = T("Room dalam model tiada dalam jadual — senaraikan",
                              "A room in the model is not in the schedule — list them"),
                Manual = Dash,
            },
            new SpecCheck("C1.6.3", "C1.6", T("Setiap kolum jadual ruang berisi",
                          "Every room schedule column is filled"), Ai, "PB p.36–37, T 22:06–22:08")
            {
                Comply = T("Setiap kolum wajib (PB p.36 Area / p.37 Room) ada nilai bagi setiap room",
                           "Every mandatory column (PB p.36 Area / p.37 Room) has a value for every room"),
                NotComply = T("Ada kolum kosong — senaraikan `Room | Kolum kosong` (cth 'Bilik Mesyuarat 1 | Kemasan lantai, Kemasan dinding'). Tidak lengkap = JKR reject",
                              "An empty column — list `Room | Empty column` (e.g. 'Bilik Mesyuarat 1 | Floor finish, Wall finish'). Incomplete = JKR rejects"),
                Manual = Dash,
            },
            new SpecCheck("C2.1.1", "C2.1", T("Legend dan status dalam lukisan",
                          "Legends and their status on drawings"), Ai, "T 22:08–22:09")
            {
                Comply = T("Sekurang-kurangnya satu legend wujud → ✓",
                           "At least one legend exists → ✓"),
                NotComply = T("Tiada legend langsung",
                              "No legend at all"),
                Manual = T("SENARAI `Nama legend | Jenis | Dalam lukisan? (biru)` untuk semakan manusia",
                           "LIST `Legend name | Type | On a drawing? (blue)` for a person to review"),
            },
            new SpecCheck("C3.1.1", "C3.1", T("Kategori dimodelkan mesti ada schedule",
                          "Modelled categories must have a schedule"), Ai, "T 22:10–22:13")
            {
                Comply = T("Setiap kategori AR (senarai PB p.36–103) yang DIMODELKAN mempunyai schedule",
                           "Every AR category (PB p.36–103 list) that is MODELLED has a schedule"),
                NotComply = T("Dimodelkan tetapi tiada schedule — senaraikan (cth 'Railing: 5 elemen, tiada schedule')",
                              "Modelled but no schedule — list them (e.g. 'Railing: 5 elements, no schedule')"),
                Manual = Dash,
            },
            new SpecCheck("C3.1.2", "C3.1", T("Kategori tidak dimodelkan",
                          "Categories not modelled"), List, "T 22:14–22:16")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("Tandakan 'n/a' — BUKAN kekurangan. Kategori bukan-AR (line, dimension) tidak disenaraikan langsung",
                           "Mark 'n/a' — NOT a shortcoming. Non-AR categories (lines, dimensions) are not listed at all"),
            },
            new SpecCheck("C4.1.1", "C4.1", T("Senarai semua sheet",
                          "List of all sheets"), List, "T 22:17–22:18")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENARAI `No. sheet | Nama | Bil. view | Jenis kandungan` — manusia semak (sheet kadang digabung)",
                           "LIST `Sheet no. | Name | No. of views | Content type` — a person reviews (sheets are sometimes combined)"),
            },
            new SpecCheck("C4.1.2", "C4.1", T("Checklist merentas semua sheet",
                          "Checklist across all sheets"), Ai, "T 22:18–22:19, N3")
            {
                Comply = T("Kesemua hadir merentas set sheet: floor plan, ceiling plan, elevation, section, detail pintu, detail tingkap",
                           "All present across the sheet set: floor plan, ceiling plan, elevation, section, door detail, window detail"),
                NotComply = T("Mana-mana jenis tiada langsung dalam semua sheet",
                              "Any type missing from every sheet"),
                Manual = Dash,
            },
            new SpecCheck("C4.1.3", "C4.1", T("Sheet kosong",
                          "Empty sheets"), Ai, "F C.4.1")
            {
                Comply = T("Setiap sheet mengandungi sekurang-kurangnya satu view",
                           "Every sheet holds at least one view"),
                NotComply = T("Sheet tanpa view → ✗",
                              "A sheet without a view → ✗"),
                Manual = Dash,
            },
            new SpecCheck("C4.2.1", "C4.2", T("Title block JKR digunakan",
                          "JKR title block used"), Ai, "F C.4.2")
            {
                Comply = T("Family title block yang digunakan ialah title block JKR",
                           "The title block family used is the JKR title block"),
                NotComply = T("Title block bukan JKR / tiada",
                              "Title block not JKR / none"),
                Manual = Dash,
            },
            new SpecCheck("C4.2.2", "C4.2", T("Maklumat title block terisi",
                          "Title block information filled"), Ai, "PB p.103, T 22:20–22:21")
            {
                Comply = T("Semua terisi: tajuk projek, tajuk lukisan, dimodelkan oleh, timestamp",
                           "All filled: project title, drawing title, modelled by, timestamp"),
                NotComply = T("Mana-mana sel kosong → ✗ pada sel berkenaan",
                              "Any empty cell → ✗ on that cell"),
                Manual = Dash,
            },
            new SpecCheck("C4.2.3", "C4.2", T("Timestamp softcopy vs hardcopy sama",
                          "Softcopy vs hardcopy timestamp match"), Human, "NS §12.4.5, T 22:22")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("AI hanya sahkan timestamp softcopy. Padanan dengan HARDCOPY = SEMAK MANUAL (AI tiada akses)",
                           "The AI only confirms the softcopy timestamp. Matching the HARDCOPY = MANUAL CHECK (the AI has no access)"),
            },
            new SpecCheck("C5.1.1", "C5.1", T("Link disiplin ST / ME / EL",
                          "Discipline links ST / ME / EL"), Ai, "T 22:21–22:22")
            {
                Comply = T("Ketiga-tiga disiplin hadir sebagai link dan berstatus Loaded",
                           "All three disciplines are present as links with status Loaded"),
                NotComply = T("Disiplin wajib tiada link ATAU status Unloaded → ✗ KRITIKAL",
                              "A required discipline has no link OR status Unloaded → ✗ CRITICAL"),
                Manual = Dash,
            },
            new SpecCheck("C5.1.2", "C5.1", T("Nama fail link vs BPEP",
                          "Link file names vs the BPEP"), Ai, "T 22:23–22:25")
            {
                Comply = T("Nama fail link padan dengan senarai fail dalam BPEP",
                           "Link file names match the file list in the BPEP"),
                NotComply = T("Nama tidak padan / fail BPEP tiada dalam model",
                              "Names do not match / a BPEP file is not in the model"),
                Manual = T("BPEP tidak tersedia → tidak dapat disemak",
                           "BPEP not available → cannot be checked"),
            },
            new SpecCheck("C5.1.3", "C5.1", T("Link versi terkini",
                          "Links are the latest version"), Human, "T 22:22–22:23")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SENTIASA SEMAK MANUAL — AI tiada akses CDE/sync. Remark: 'kena semak versi terkini secara manual'",
                           "ALWAYS A MANUAL CHECK — the AI has no CDE / sync access. Remark: 'the latest version must be checked by hand'"),
            },
            // ── D ──
            new SpecCheck("Da.1", "D-a", T("Type name ikut coding JKR",
                          "Type name follows JKR coding"), Ai, "T 22:27–22:28")
            {
                Comply = T("Type name padan dengan pola coding JKR (cth `(PT2p600a) 900 x 2325 a`)",
                           "The type name matches the JKR coding pattern (e.g. `(PT2p600a) 900 x 2325 a`)"),
                NotComply = T("Nama generik ('Door 1', 'Basic Wall', 'door bla bla') → ✗ TERUS. Papar `Type name | Bil. | Cadangan nama`",
                              "A generic name ('Door 1', 'Basic Wall', 'door bla bla') → ✗ DIRECTLY. Show `Type name | Count | Suggested name`"),
                Manual = Dash,
            },
            new SpecCheck("Db.1", "D-b", T("Parameter wajib ikut Buku Parameter × LOD",
                          "Mandatory parameters per Buku Parameter × LOD"), Ai, "PB per kategori, T 22:28")
            {
                Comply = T("Semua parameter wajib pada LOi projek bagi kategori itu terisi (PB: Door p.51, Window p.99, Wall p.95, Floor p.55, Ceiling p.42, Roof p.83, Stair p.91, Railing p.80, Ramp p.76, Curtain p.48, Casework p.38, Plumbing p.72, Generic p.67, Column p.45)",
                           "Every parameter mandatory at the project LOI for that category is filled (PB: Door p.51, Window p.99, Wall p.95, Floor p.55, Ceiling p.42, Roof p.83, Stair p.91, Railing p.80, Ramp p.76, Curtain p.48, Casework p.38, Plumbing p.72, Generic p.67, Column p.45)"),
                NotComply = T("Parameter wajib kosong — ringkasan 'Door: 9/12 parameter LOi 300 terisi'",
                              "A mandatory parameter is empty — summary 'Door: 9/12 LOI 300 parameters filled'"),
                Manual = Dash,
            },
            new SpecCheck("Db.2", "D-b", T("Material pada elemen",
                          "Material on elements"), Ai, "T 22:28–22:29")
            {
                Comply = T("Material terisi DAN mengikut coding JKR",
                           "Material filled AND follows JKR coding"),
                NotComply = T("Material kosong atau tidak ikut coding JKR",
                              "Material empty or not JKR-coded"),
                Manual = Dash,
            },
            new SpecCheck("Dc.1", "D-c", T("Nilai dalam nama tally dengan parameter geometri",
                          "Values in the name tally with the geometry parameters"), Ai, "T 22:33–22:34")
            {
                Comply = T("Nombor dalam type name (dianggap mm) sama dengan nilai parameter sebenar (cth `Wall 146` → Width = 146)",
                           "The number in the type name (taken as mm) equals the actual parameter value (e.g. `Wall 146` → Width = 146)"),
                NotComply = T("Tidak padan — senaraikan `Type name | Nilai dalam nama | Parameter | Nilai sebenar` (cth 'Wall 146 | 146 | Width | 150 ✗')",
                              "No match — list `Type name | Value in name | Parameter | Actual value` (e.g. 'Wall 146 | 146 | Width | 150 ✗')"),
                Manual = Dash,
            },
            new SpecCheck("Dd.1", "D-d", T("Nama ↔ kategori Revit",
                          "Name ↔ Revit category"), Ai, "T 22:35")
            {
                Comply = T("Kategori Revit padan dengan kategori yang dijangka dari nama (floor dalam kategori Floor, tingkap bukan dalam kategori Door)",
                           "The Revit category matches the category the name implies (a floor in the Floor category, a window not in the Door category)"),
                NotComply = T("Tidak padan — paparkan yang tidak padan SAHAJA",
                              "No match — show ONLY the mismatches"),
                Manual = Dash,
            },
            new SpecCheck("Dd.2", "D-d", T("Teknik pemodelan (cara letak permukaan dll)",
                          "Modelling technique (how surfaces are placed etc.)"), Human, "T 22:35–22:36")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("SEMAK MANUAL — di luar skop semakan automatik",
                           "MANUAL CHECK — outside the scope of the automatic check"),
            },
            new SpecCheck("De.1", "D-e", T("Room (F D.8)",
                          "Room (F D.8)"), List, "F D.8")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("Tiada ElementType — gantikan dengan semakan room di A6 / C1.6",
                           "No ElementType — replaced by the room checks in A6 / C1.6"),
            },
            new SpecCheck("De.2", "D-e", T("Columns / Structural Column 0 elemen",
                          "Columns / Structural Column with 0 elements"), Human, "F D.18, D.21")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("0 elemen → 'sahkan skop' (MANUSIA). JANGAN flag MAJOR automatik",
                           "0 elements → 'confirm scope' (A PERSON). DO NOT flag MAJOR automatically"),
            },
            new SpecCheck("De.3", "D-e", T("Kategori tidak dimodelkan",
                          "Categories not modelled"), List, "C 3.1")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = T("Tandakan 'tidak berkenaan' (konsisten dengan C 3.1)",
                           "Mark 'not applicable' (consistent with C 3.1)"),
            },
            // ── E ──
            new SpecCheck("E.1", "E", T("View template betul mengikut set",
                          "View template right for its set"), Ai, "T 22:37–22:39")
            {
                Comply = T("Setiap view guna template setnya sendiri: BOMBA→template Bomba, PBT→template PBT, Dokumen→template Dokumen",
                           "Every view uses its own set's template: BOMBA→Bomba template, PBT→PBT template, Dokumen→Dokumen template"),
                NotComply = T("Tiada view template LANGSUNG, ATAU template dari set yang SALAH — papar `Set | Nama view | Template dipakai | Template dijangka | Padan?`",
                           "No view template AT ALL, OR a template from the WRONG set — show `Set | View name | Template used | Template expected | Match?`"),
                Manual = T("View WIP: tiada peraturan template dari mesyuarat — disenaraikan sahaja, menunggu pengesahan JKR",
                           "WIP views: no template rule from the meeting — listed only, awaiting JKR's confirmation"),
            },
        };

        public static SpecCheck Find(string id) => Checks.FirstOrDefault(c => c.Id == id);
        public static IEnumerable<SpecItem> ItemsOf(string section) => Items.Where(i => i.Section == section);
        public static IEnumerable<SpecCheck> ChecksOf(string item) => Checks.Where(c => c.Item == item);
        public static string SectionOf(string item) => Items.FirstOrDefault(i => i.No == item)?.Section ?? "";
    }
}
