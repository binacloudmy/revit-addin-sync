// GENERATED from "Model Audit Prototype.dc.html" (const ISSUES, BASE_PASS,
// FILE) — the prototype fixture, field for field. Regenerate rather than edit.

using System.Collections.Generic;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    public static class PanelFixture
    {
        public const string File = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a.rvt";

        /// <summary>Passing AI cells per section that are not issues (prototype BASE_PASS).</summary>
        public static IReadOnlyDictionary<string, int> BasePass => new Dictionary<string, int>
        {
            ["A"] = 41,
            ["B"] = 26,
            ["C"] = 58,
            ["D"] = 112,
            ["E"] = 18,
        };

        /// <summary>The 19 prototype issues, status open and not passed.</summary>
        public static List<PanelItem> Issues() => new List<PanelItem>
        {
            new PanelItem
            {
                Id = "1", Row = "A1", Sec = "A", Sev = "High",
                V = Verify.Ai,
                Title = new Text("Lebar pintu single di bawah minimum", "Single door below minimum width"),
                Desc = new Text("Pintu single mesti sekurang-kurangnya 900 mm lebar mengikut Need Statement.", "Single doors must be at least 900 mm wide per the Need Statement."),
                El = "Doors [204511] PT2p600a", Req = "≥ 900 mm", Act = Text.Same("850 mm"),
                Prop = null, Cite = "NS §5.8.5 · Need Statement AR 2025",
                Fix = FixKind.None, Hash = "576aaab64d14",
                Reason = null,
            },
            new PanelItem
            {
                Id = "2", Row = "A1", Sec = "A", Sev = "Med",
                V = Verify.Human,
                Title = new Text("Kalis air bumbung rata", "Flat roof waterproofing"),
                Desc = new Text("Bumbung rata dibenarkan dengan syarat ada lapisan kalis air.", "Flat roofs are allowed only with a waterproofing layer."),
                El = "Roofs [118204] Bumbung Rata 01", Req = "Lapisan kalis air", Act = Text.Same("—"),
                Prop = null, Cite = "NS §5.2.3, §5.14",
                Fix = FixKind.None, Hash = "a91e02c7f310",
                Reason = new Text("Lapisan kalis air tidak dimodelkan sebagai lapisan yang boleh dikenal pasti. AI tidak dapat menentukan sama ada ia wujud.", "Waterproofing is not modelled as an identifiable layer. The AI cannot determine whether it exists."),
            },
            new PanelItem
            {
                Id = "3", Row = "A3", Sec = "A", Sev = "Med",
                V = Verify.Human,
                Title = new Text("Cara pecahan model", "Model split explanation"),
                Desc = new Text("Model dipecahkan mengikut tower/podium/basement. Semua pecahan dijumpai sebagai link.", "Model is split by tower/podium/basement. All splits were found as links."),
                El = "Kod model: WX01 · WX02 · WX03", Req = "Pecahan mudah diurus + link", Act = Text.Same("WX01 Tower, WX02 Podium, WX03 Basement"),
                Prop = null, Cite = "Borang A3 · BPEP",
                Fix = FixKind.None, Hash = "3c0b8e41d2aa",
                Reason = new Text("AI menerangkan cara model dipecahkan; kesesuaian pecahan disahkan oleh manusia.", "The AI describes how the model is split; whether the split is appropriate is a human call."),
            },
            new PanelItem
            {
                Id = "4", Row = "A5", Sec = "A", Sev = "Med",
                V = Verify.Ai,
                Title = new Text("Urutan grid tidak tersusun", "Grid sequence has a gap"),
                Desc = new Text("Grid nombor melompat dari 3 ke 11. Grid mesti tersusun tanpa lompatan.", "Numbered grids jump from 3 to 11. Grids must be sequential with no gaps."),
                El = "Grids [311902] 11", Req = "1, 2, 3, 4 …", Act = Text.Same("1, 2, 3 → 11"),
                Prop = "1, 2, 3 → 4", Cite = "Borang A5 · Garis Panduan BIM JKR",
                Fix = FixKind.Fix, Hash = "0d7f1a2b9e44",
                Reason = null,
            },
            new PanelItem
            {
                Id = "5", Row = "A6", Sec = "A", Sev = "High",
                V = Verify.Ai,
                Title = new Text("Slab tanpa room", "Slab with no room"),
                Desc = new Text("Ada lantai pada lokasi ini tetapi tiada room didaftarkan. Ruang lif mesti didaftar sebagai room.", "A floor exists here but no room is placed. Lift spaces must be registered as rooms."),
                El = "Aras 02 · Grid C/4 · Lif", Req = "Ada slab → mesti ada room", Act = new Text("tiada room", "no room"),
                Prop = null, Cite = "Borang A6 · PB p.37 Room",
                Fix = FixKind.None, Hash = "be2201f9c3d8",
                Reason = null,
            },
            new PanelItem
            {
                Id = "6", Row = "A7", Sec = "A", Sev = "Low",
                V = Verify.Ai,
                Title = new Text("Material tidak ikut naming JKR", "Material not using JKR naming"),
                Desc = new Text("Material default Revit digunakan pada pintu kaca.", "A default Revit material is used on a glass door."),
                El = "Materials · Doors (14 elemen)", Req = "JKR_<jenis>_<spesifikasi>", Act = Text.Same("Glass"),
                Prop = "JKR_Kaca_Jernih_6mm", Cite = "Borang A7 · PB p.35 Material Identity",
                Fix = FixKind.Fix, Hash = "7e44c0a1b2f9",
                Reason = null,
            },
            new PanelItem
            {
                Id = "7", Row = "B1", Sec = "B", Sev = "Med",
                V = Verify.Ai,
                Title = new Text("Project Information wajib kosong", "Mandatory Project Information empty"),
                Desc = new Text("Parameter ini wajib diisi dari LOi 200. Masukkan nama perunding struktur.", "This parameter is mandatory from LOi 200. Enter the structural consultant name."),
                El = "Project Information · Perunding_Struktur_jkr_sit", Req = "Terisi (LOi 200–500)", Act = new Text("(kosong)", "(empty)"),
                Prop = null, Cite = "PB p.30 Project Information",
                Fix = FixKind.None, Hash = "c18d3e7a0f52",
                Reason = null,
            },
            new PanelItem
            {
                Id = "8", Row = "C1.1", Sec = "C", Sev = "Med",
                V = Verify.Ai,
                Title = new Text("3D view Aras 03 tiada", "Missing 3D view for Level 03"),
                Desc = new Text("Setiap aras mesti ada 3D view mengikut template JKR AR.", "Every level needs a 3D view per the JKR AR template."),
                El = "Views · Aras 03", Req = "3D view setiap aras", Act = new Text("tiada", "none"),
                Prop = "3D - Aras 03", Cite = "Borang C1.1 · JKR AR Template",
                Fix = FixKind.Fix, Hash = "4a9b7c21e0d3",
                Reason = null,
            },
            new PanelItem
            {
                Id = "9", Row = "C1.2", Sec = "C", Sev = "Low",
                V = Verify.Human,
                Title = new Text("Set view WIP", "WIP view set"),
                Desc = new Text("6 view WIP disenaraikan: floor plan dan ceiling plan bagi Aras 01–03.", "6 WIP views listed: floor and ceiling plans for Levels 01–03."),
                El = "Views · WIP (6)", Req = "Floor plan + ceiling plan setiap aras", Act = Text.Same("WIP_FP_L01…L03, WIP_CP_L01…L03"),
                Prop = null, Cite = "Borang C1.2",
                Fix = FixKind.None, Hash = "9f12d0b7a6c1",
                Reason = new Text("Borang meminta senarai sahaja. Kecukupan set WIP disahkan oleh manusia.", "The form asks for a list only. Whether the WIP set is sufficient is a human call."),
            },
            new PanelItem
            {
                Id = "10", Row = "C1.6", Sec = "C", Sev = "High",
                V = Verify.Ai,
                Title = new Text("Jadual ruang tidak dijumpai", "Room schedule not found"),
                Desc = new Text("Schedule of Accommodation mesti dijana dari model. JKR menolak serahan tanpa jadual ruang.", "The Schedule of Accommodation must be generated from the model. JKR rejects submissions without one."),
                El = "Schedules · Room", Req = "Jadual ruang wujud & lengkap", Act = new Text("tiada", "none"),
                Prop = "Jadual Ruang (PB p.37)", Cite = "Borang C1.6 · NS §3.1 · PB p.36–37",
                Fix = FixKind.Generate, Hash = "e07a9c3b1d58",
                Reason = null,
            },
            new PanelItem
            {
                Id = "11", Row = "C3.1", Sec = "C", Sev = "Med",
                V = Verify.Ai,
                Title = new Text("Railing dimodelkan tanpa schedule", "Railings modelled without a schedule"),
                Desc = new Text("Setiap kategori yang dimodelkan mesti wujud dalam schedule.", "Every modelled category must appear in a schedule."),
                El = "Railings (5 elemen)", Req = "Schedule wujud", Act = new Text("tiada schedule", "no schedule"),
                Prop = "JKR_Jadual_Railing", Cite = "Borang C3.1 · PB p.80",
                Fix = FixKind.Fix, Hash = "11b6f4e2c90a",
                Reason = null,
            },
            new PanelItem
            {
                Id = "12", Row = "C4.1", Sec = "C", Sev = "Low",
                V = Verify.Ai,
                Title = new Text("Sheet kosong", "Empty sheet"),
                Desc = new Text("Sheet ini tidak mengandungi sebarang view.", "This sheet contains no views."),
                El = "Sheets · A-105 Butiran Tingkap", Req = "≥ 1 view", Act = Text.Same("0 view"),
                Prop = null, Cite = "Borang C4.1",
                Fix = FixKind.None, Hash = "5d3a8e0f7b26",
                Reason = null,
            },
            new PanelItem
            {
                Id = "13", Row = "C4.2", Sec = "C", Sev = "Med",
                V = Verify.Human,
                Title = new Text("Timestamp hardcopy sama dengan softcopy", "Hardcopy timestamp matches softcopy"),
                Desc = new Text("Title block JKR dan tajuk projek terisi. Timestamp softcopy: 2026-11-08.", "JKR title block and project title are filled. Softcopy timestamp: 2026-11-08."),
                El = "Sheets (24) · Title block JKR_A1", Req = "Timestamp softcopy = hardcopy", Act = Text.Same("2026-11-08 / —"),
                Prop = null, Cite = "Borang C4.2 · NS §12.4.5",
                Fix = FixKind.None, Hash = "f6c2b19d04e7",
                Reason = new Text("Hardcopy tidak boleh dibaca oleh AI.", "The AI cannot read the hardcopy."),
            },
            new PanelItem
            {
                Id = "14", Row = "C5.1", Sec = "C", Sev = "High",
                V = Verify.Ai,
                Title = new Text("Fail link EL tiada — KRITIKAL", "EL link file missing — CRITICAL"),
                Desc = new Text("Model arkitek mesti di-link dengan model Struktur, Mekanikal dan Elektrik.", "The architectural model must link Structural, Mechanical and Electrical models."),
                El = "Revit Links · EL", Req = "ST ✓ · ME ✓ · EL", Act = new Text("EL: tiada", "EL: none"),
                Prop = null, Cite = "Borang C5.1 · BPEP",
                Fix = FixKind.None, Hash = "2b8e5f1a3c07",
                Reason = null,
            },
            new PanelItem
            {
                Id = "15", Row = "C5.1", Sec = "C", Sev = "Med",
                V = Verify.Human,
                Title = new Text("Versi terkini fail link", "Link files are latest version"),
                Desc = new Text("Link ST dan ME dimuatkan. Status versi tidak dapat disahkan.", "ST and ME links are loaded. Version status cannot be confirmed."),
                El = "Revit Links · ST, ME", Req = "Versi terkini", Act = Text.Same("—"),
                Prop = null, Cite = "Borang C5.1",
                Fix = FixKind.None, Hash = "8c71a0e4d2b9",
                Reason = new Text("Tiada akses ke CDE. Semak versi terkini secara manual.", "No CDE access. Check for the latest version manually."),
            },
            new PanelItem
            {
                Id = "16", Row = "D", Sec = "D", Sev = "Med",
                V = Verify.Ai,
                Title = new Text("Nama type pintu generik", "Generic door type name"),
                Desc = new Text("Nama type mesti ikut coding JKR. Nama generik terus tidak comply.", "Type names must follow JKR coding. Generic names fail outright."),
                El = "Doors · 12 elemen", Req = "(PTxpNNNa) W x H a", Act = Text.Same("Door 1"),
                Prop = "(PT2p600a) 900 x 2325 a", Cite = "Borang D · Coding JKR",
                Fix = FixKind.Fix, Hash = "d4e9b2a7c113",
                Reason = null,
            },
            new PanelItem
            {
                Id = "17", Row = "D", Sec = "D", Sev = "Med",
                V = Verify.Ai,
                Title = new Text("Nama dinding tidak padan dengan Width", "Wall name does not match Width"),
                Desc = new Text("Nilai dalam nama (146) berbeza dengan parameter Width (150).", "The value in the name (146) differs from the Width parameter (150)."),
                El = "Walls · JKR_Dinding_Bata_146", Req = "Width = 146", Act = Text.Same("Width = 150"),
                Prop = "JKR_Dinding_Bata_150", Cite = "Borang D · PB p.95 Wall",
                Fix = FixKind.Fix, Hash = "a3f07c58e2d1",
                Reason = null,
            },
            new PanelItem
            {
                Id = "18", Row = "D", Sec = "D", Sev = "High",
                V = Verify.Ai,
                Title = new Text("Tingkap dimodel dalam kategori Door", "Window modelled in Door category"),
                Desc = new Text("Nama menunjukkan tingkap tetapi kategori Revit ialah Door.", "The name indicates a window but the Revit category is Door."),
                El = "Doors [220871] TK_Nako_600", Req = "Kategori: Windows", Act = Text.Same("Kategori: Doors"),
                Prop = null, Cite = "Borang D · PB p.99 Window",
                Fix = FixKind.None, Hash = "60b2d9f1e4a8",
                Reason = null,
            },
            new PanelItem
            {
                Id = "19", Row = "E", Sec = "E", Sev = "Med",
                V = Verify.Ai,
                Title = new Text("View BOMBA guna template PBT", "BOMBA view uses PBT template"),
                Desc = new Text("View dalam set BOMBA mesti guna view template BOMBA.", "Views in the BOMBA set must use the BOMBA view template."),
                El = "Views · BOMBA_FP_L01", Req = "BOMBA_Plan", Act = Text.Same("PBT_Plan"),
                Prop = "BOMBA_Plan", Cite = "Borang E · JKR AR Template",
                Fix = FixKind.Fix, Hash = "b75c3e09a1f2",
                Reason = null,
            },
        };
    }
}
