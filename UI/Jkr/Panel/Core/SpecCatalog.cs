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

    /// <summary>A Borang item ("A2", "C1.6", "D-b"). The title is the form's own
    /// English wording, the same in BM and EN, as on the printed Borang.</summary>
    public sealed class SpecItem
    {
        public SpecItem(string section, string no, string title) { Section = section; No = no; Title = title; }
        public string Section { get; }
        public string No { get; }
        public string Title { get; }
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
        /// <summary>BM: the spec's "Apa yang disemak"; EN: a short rendering of it.</summary>
        public Text Title { get; }
        public SpecCellClass Class { get; }
        /// <summary>The spec's "Rujukan" (NS clause, Buku Parameter page, Borang row, meeting time).</summary>
        public string Reference { get; }
        public string Section => SpecCatalog.SectionOf(Item);
        /// <summary>The spec's "COMPLY (✓) bila" (BM verbatim; EN translation).</summary>
        public Text Comply { get; set; } = Text.Same("");
        /// <summary>The spec's "TIDAK COMPLY (✗) bila".</summary>
        public Text NotComply { get; set; } = Text.Same("");
        /// <summary>The spec's "SEMAK MANUAL / nota".</summary>
        public Text Manual { get; set; } = Text.Same("");
    }

    /// <summary>
    /// The 61 checks of the JKR Model Audit spec (JKR-Model-Audit-Spec.xlsx, sheet
    /// "Peraturan comply"), grouped as the spec groups them: Borang BIM010
    /// section (A–E) → item → check. Totals as the spec states them: 61 checks —
    /// 40 AI-verifiable, 7 human-decides, 14 list only. WPF- and Revit-free.
    /// </summary>
    public static class SpecCatalog
    {
        private const SpecCellClass Ai = SpecCellClass.Ai;
        private const SpecCellClass Human = SpecCellClass.Human;
        private const SpecCellClass List = SpecCellClass.List;

        private static Text T(string ms, string en) => new Text(ms, en);

        public static readonly IReadOnlyList<SpecItem> Items = new[]
        {
            new SpecItem("A", "A1", "Architectural Model developed in accordance to specified architectural design"),
            new SpecItem("A", "A2", "Model files named according to PWD BIM Guidelines"),
            new SpecItem("A", "A3", "Model structured according to rules that are easily managed (block / tower / podium)"),
            new SpecItem("A", "A4", "Model positioned to Project Base Point (Grid A/1)"),
            new SpecItem("A", "A5", "Model organised using architectural gridlines and levels"),
            new SpecItem("A", "A6", "Rooms are categorised by departments"),
            new SpecItem("A", "A7", "All materials included in model elements"),
            new SpecItem("B", "B1", "Project Information should be updated"),
            new SpecItem("C", "C1.0", "Views (Architectural)"),
            new SpecItem("C", "C1.1", "Views created based on PWD architectural template"),
            new SpecItem("C", "C1.2", "WIP views"),
            new SpecItem("C", "C1.3", "PBT views"),
            new SpecItem("C", "C1.4", "BOMBA views"),
            new SpecItem("C", "C1.5", "Dokumen views"),
            new SpecItem("C", "C1.6", "Spatial & Zoning Analysis generated from Area Plan"),
            new SpecItem("C", "C2.1", "Legends"),
            new SpecItem("C", "C3.1", "Schedules / Quantities"),
            new SpecItem("C", "C4.1", "Sheets"),
            new SpecItem("C", "C4.2", "JKR Title Block with furnished information"),
            new SpecItem("C", "C5.1", "Link Files"),
            new SpecItem("D", "D-a", "Naming follows JKR coding"),
            new SpecItem("D", "D-b", "Information / parameters per Buku Parameter by LOD"),
            new SpecItem("D", "D-c", "Naming tallies with parameters"),
            new SpecItem("D", "D-d", "Integrity: name ↔ category"),
            new SpecItem("D", "D-e", "Special rows"),
            new SpecItem("E", "E", "View templates"),
        };

        public static readonly IReadOnlyList<SpecCheck> Checks = new[]
        {
            // ── A ──
            new SpecCheck("A1.1", "A1", T("Ketinggian siling ≥ 3000 mm (basement ≥ 2500 mm)", "Floor-to-ceiling ≥ 3000 mm (basement ≥ 2500 mm)"), Ai, "NS §4.1.9"),
            new SpecCheck("A1.2", "A1", T("Saiz pintu: single ≥ 900, double ≥ 1500, tinggi clear ≥ 2100 mm", "Doors: single ≥ 900, double ≥ 1500, clear height ≥ 2100 mm"), Ai, "NS §5.8.5"),
            new SpecCheck("A1.3", "A1", T("Pintu utama solid core, tebal ≥ 40 mm", "Main door solid core, ≥ 40 mm thick"), Ai, "NS §5.8.1, §5.8.3"),
            new SpecCheck("A1.4", "A1", T("Tiada RC flat roof, atau ada waterproofing", "No RC flat roof, or flat roof with waterproofing"), Ai, "NS §5.2.3, §5.14"),
            new SpecCheck("A1.5", "A1", T("Pagar hiasan ≥ 2400 mm", "Decorative fence ≥ 2400 mm"), Ai, "NS §4.8.3"),
            new SpecCheck("A1.6", "A1", T("Klausa NS lain (sustainability, IBS, landskap) disenaraikan", "Other NS clauses (sustainability, IBS, landscape) listed"), List, "NS"),
            new SpecCheck("A1.7", "A1", T("Need Statement sahaja — konsep reka bentuk tidak dinilai", "Need Statement only — design concept not assessed"), List, "Prinsip 0.3, NS"),
            new SpecCheck("A2.1", "A2", T("Nama fail model ikut PWD BIM Guidelines (semua segmen wajib)", "File name has every required segment"), Ai, "F A.2"),
            new SpecCheck("A2.2", "A2", T("Kod model (W00 / WXnn) dikesan dari nama fail", "Model code (W00 / WXnn) read from the file name"), Ai, "T 21:22"),
            new SpecCheck("A3.1", "A3", T("Model tunggal (W00) — terus comply", "Single model (W00) complies"), Ai, "T 21:19, 21:22"),
            new SpecCheck("A3.2", "A3", T("Model pecah (WXnn) — setiap pecahan di-link", "Split model (WXnn): every part is linked"), Ai, "T 21:19–21:21"),
            new SpecCheck("A3.3", "A3", T("Pecahan model padan senarai fail BPEP", "Split parts match the BPEP file list"), Ai, "T 22:23–22:25"),
            new SpecCheck("A3.4", "A3", T("Model pecah tanpa link — disenaraikan", "Split model with no links — listed"), List, "T 21:22, 21:24"),
            new SpecCheck("A3.5", "A3", T("Cara pecahan model (penerangan)", "How the model is split (description)"), List, "T 21:19–21:20"),
            new SpecCheck("A4.1", "A4", T("Kedudukan pada Project Base Point (Grid A/1)", "Offset from Grid A/1 within tolerance"), Ai, "F A.4"),
            new SpecCheck("A5.1", "A5", T("Bilangan grid dan aras", "Grid and level count"), List, "F A.5"),
            new SpecCheck("A5.2", "A5", T("Urutan grid line tersusun, tiada lompat", "Gridlines in sequence, no jumps"), Ai, "T 21:24–21:26"),
            new SpecCheck("A5.3", "A5", T("Sub-grid ikut pola 1A / A1", "Sub-grids follow the 1A / A1 pattern"), Ai, "T 21:25"),
            new SpecCheck("A6.1", "A6", T("Senarai room setiap aras", "Rooms listed per level"), List, "T 21:26–21:33"),
            new SpecCheck("A6.2", "A6", T("Ada slab mesti ada room (lif, tangga, apron)", "Every slab area has a room (lift, stair, apron)"), Ai, "T 21:28–21:33"),
            new SpecCheck("A6.3", "A6", T("Parameter Department (jika jenis projek perlu)", "Department filled where the project type needs it"), Human, "T 21:26, 21:34"),
            new SpecCheck("A7.1", "A7", T("Setiap elemen ada material", "Every element has a material"), Ai, "T 21:42–21:43"),
            new SpecCheck("A7.2", "A7", T("Naming material ikut JKR", "Material names follow JKR naming"), Ai, "T 21:40–21:46, PB p.35"),
            new SpecCheck("A7.3", "A7", T("Ketepatan pilihan material", "Material choice is correct"), Human, "T 21:44"),
            // ── B ──
            new SpecCheck("B1.1", "B1", T("Field Project Information wajib ikut LOi projek", "Mandatory Project Information fields filled for the project LOD"), Ai, "PB p.30–34"),
            new SpecCheck("B1.2", "B1", T("Disiplin lain (ST / ME / EL) — field belum diisi", "Other disciplines (ST / ME / EL): unfilled fields"), Ai, "PB p.120 / 161 / 209"),
            new SpecCheck("B1.3", "B1", T("Ringkasan pengisian (n / m field)", "Filled-field summary (n / m)"), List, "T 21:54"),
            // ── C ──
            new SpecCheck("C1.0.1", "C1.0", T("Baris induk — disemak melalui 1.1–1.6", "Parent row — checked through 1.1–1.6"), List, "T 21:56"),
            new SpecCheck("C1.1.1", "C1.1", T("View wajib template JKR AR × setiap aras", "Template views exist for every level"), Ai, "T 21:57–22:01"),
            new SpecCheck("C1.1.2", "C1.1", T("View template tidak dipadam / ditukar nama", "View templates present under their original names"), Ai, "T 21:57–21:59"),
            new SpecCheck("C1.1.3", "C1.1", T("3D view setiap aras", "One 3D view per level"), Ai, "N2, T 22:39–22:40"),
            new SpecCheck("C1.1.4", "C1.1", T("Bilangan aras mencukupi", "Number of levels is enough"), Human, "T 22:05"),
            new SpecCheck("C1.2.1", "C1.2", T("Set view WIP disenaraikan", "WIP view set listed"), List, "T 22:04–22:05"),
            new SpecCheck("C1.3.1", "C1.3", T("Set view PBT: floor plan, elevation, section", "PBT set has floor plan, elevation and section"), Ai, "T 22:01, 22:04"),
            new SpecCheck("C1.4.1", "C1.4", T("Set view BOMBA: floor plan, section, drafting view", "BOMBA set has floor plan, section and drafting view"), Ai, "T 22:01, 22:05"),
            new SpecCheck("C1.5.1", "C1.5", T("View Dokumen + status dalam sheet", "Dokumen views listed with sheet status"), List, "T 22:03, 22:40"),
            new SpecCheck("C1.6.1", "C1.6", T("Jadual ruang (room schedule) wujud", "Room schedule exists"), Ai, "T 21:36–22:08"),
            new SpecCheck("C1.6.2", "C1.6", T("Setiap room hadir dalam jadual", "Every room is in the schedule"), Ai, "N2, T 22:06–22:08"),
            new SpecCheck("C1.6.3", "C1.6", T("Setiap kolum jadual ruang berisi", "Every schedule column is filled"), Ai, "PB p.36–37"),
            new SpecCheck("C2.1.1", "C2.1", T("Sekurang-kurangnya satu legend wujud", "At least one legend exists"), Ai, "T 22:08–22:09"),
            new SpecCheck("C3.1.1", "C3.1", T("Kategori dimodelkan mesti ada schedule", "Every modelled AR category has a schedule"), Ai, "T 22:10–22:13"),
            new SpecCheck("C3.1.2", "C3.1", T("Kategori tidak dimodelkan — n/a", "Categories not modelled marked n/a"), List, "T 22:14–22:16"),
            new SpecCheck("C4.1.1", "C4.1", T("Senarai semua sheet", "All sheets listed"), List, "T 22:17–22:18"),
            new SpecCheck("C4.1.2", "C4.1", T("Sheet merangkumi plan, siling, elevation, section, detail pintu & tingkap", "Sheets cover plan, ceiling, elevation, section, door and window details"), Ai, "T 22:18–22:19, N3"),
            new SpecCheck("C4.1.3", "C4.1", T("Tiada sheet kosong", "No empty sheets"), Ai, "F C.4.1"),
            new SpecCheck("C4.2.1", "C4.2", T("Title block JKR digunakan", "JKR title block used"), Ai, "F C.4.2"),
            new SpecCheck("C4.2.2", "C4.2", T("Maklumat title block terisi (tajuk, dimodelkan oleh, timestamp)", "Title block fields filled (titles, modeller, timestamp)"), Ai, "PB p.103"),
            new SpecCheck("C4.2.3", "C4.2", T("Timestamp softcopy sama dengan hardcopy", "Softcopy and hardcopy timestamps match"), Human, "NS §12.4.5"),
            new SpecCheck("C5.1.1", "C5.1", T("Link disiplin ST / ME / EL ada dan Loaded", "ST, ME and EL linked and loaded"), Ai, "T 22:21–22:22"),
            new SpecCheck("C5.1.2", "C5.1", T("Nama fail link padan BPEP", "Link file names match the BPEP"), Ai, "T 22:23–22:25"),
            new SpecCheck("C5.1.3", "C5.1", T("Link versi terkini", "Links are the latest version"), Human, "T 22:22–22:23"),
            // ── D ──
            new SpecCheck("Da.1", "D-a", T("Type name ikut coding JKR (nama generik = ✗)", "Type names follow JKR coding (generic names fail)"), Ai, "T 22:27–22:28"),
            new SpecCheck("Db.1", "D-b", T("Parameter wajib ikut Buku Parameter × LOD", "Mandatory parameters filled for the project LOD"), Ai, "PB"),
            new SpecCheck("Db.2", "D-b", T("Material pada elemen terisi dan ikut coding JKR", "Element materials filled and JKR-coded"), Ai, "T 22:28–22:29"),
            new SpecCheck("Dc.1", "D-c", T("Nilai dalam nama tally dengan parameter geometri", "Numbers in the type name match the geometry (mm)"), Ai, "T 22:33–22:34"),
            new SpecCheck("Dd.1", "D-d", T("Nama ↔ kategori Revit", "Type name matches the Revit category"), Ai, "T 22:35"),
            new SpecCheck("Dd.2", "D-d", T("Teknik pemodelan", "Modelling technique"), Human, "T 22:35–22:36"),
            new SpecCheck("De.1", "D-e", T("Room — rujuk A6 / C1.6", "Room row — covered by A6 and C1.6"), List, "F D.8"),
            new SpecCheck("De.2", "D-e", T("Columns / Structural Column 0 elemen — sahkan skop", "Columns with 0 elements — confirm scope"), Human, "F D.18, D.21"),
            new SpecCheck("De.3", "D-e", T("Kategori tidak dimodelkan — tidak berkenaan", "Categories not modelled — not applicable"), List, "C 3.1"),
            // ── E ──
            new SpecCheck("E.1", "E", T("View template betul mengikut set", "Views use the template of their set"), Ai, "T 22:37–22:39"),
        };

        public static SpecCheck Find(string id) => Checks.FirstOrDefault(c => c.Id == id);
        public static IEnumerable<SpecItem> ItemsOf(string section) => Items.Where(i => i.Section == section);
        public static IEnumerable<SpecCheck> ChecksOf(string item) => Checks.Where(c => c.Item == item);
        public static string SectionOf(string item) => Items.FirstOrDefault(i => i.No == item)?.Section ?? "";
    }
}
