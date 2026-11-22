// Originally generated from the yellow Model Audit prototype; now hand-maintained for the Clear Sheet design.
// Do not regenerate: it would bring back removed strings (Generate, consent, Undo, 'Fixed · re-scan pending').

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
                Title = Text.Same("Single door below minimum width"),
                Desc = Text.Same("Single doors must be at least 900 mm wide per the Need Statement."),
                El = "Doors [204511] PT2p600a", Req = "≥ 900 mm", Act = Text.Same("850 mm"),
                Prop = null, Cite = "NS §5.8.5 · Need Statement AR 2025",
                Fix = FixKind.None, Hash = "576aaab64d14",
                Reason = null,
            },
            new PanelItem
            {
                Id = "2", Row = "A1", Sec = "A", Sev = "Med",
                V = Verify.Human,
                Title = Text.Same("Flat roof waterproofing"),
                Desc = Text.Same("Flat roofs are allowed only with a waterproofing layer."),
                El = "Roofs [118204] Flat Roof 01", Req = "Waterproofing layer", Act = Text.Same("—"),
                Prop = null, Cite = "NS §5.2.3, §5.14",
                Fix = FixKind.None, Hash = "a91e02c7f310",
                Reason = Text.Same("Waterproofing is not modelled as an identifiable layer. The AI cannot determine whether it exists."),
            },
            new PanelItem
            {
                Id = "3", Row = "A3", Sec = "A", Sev = "Med",
                V = Verify.Human,
                Title = Text.Same("Model split explanation"),
                Desc = Text.Same("Model is split by tower/podium/basement. All splits were found as links."),
                El = "Model code: WX01 · WX02 · WX03", Req = "Manageable split + links", Act = Text.Same("WX01 Tower, WX02 Podium, WX03 Basement"),
                Prop = null, Cite = "Borang A3 · BPEP",
                Fix = FixKind.None, Hash = "3c0b8e41d2aa",
                Reason = Text.Same("The AI describes how the model is split; whether the split is appropriate is a human call."),
            },
            new PanelItem
            {
                Id = "4", Row = "A5", Sec = "A", Sev = "Med",
                V = Verify.Ai,
                Title = Text.Same("Grid sequence has a gap"),
                Desc = Text.Same("Numbered grids jump from 3 to 11. Grids must be sequential with no gaps."),
                El = "Grids [311902] 11", Req = "1, 2, 3, 4 …", Act = Text.Same("1, 2, 3 → 11"),
                Prop = "1, 2, 3 → 4", Cite = "Borang A5 · Garis Panduan BIM JKR",
                Fix = FixKind.Fix, Hash = "0d7f1a2b9e44",
                Reason = null,
            },
            new PanelItem
            {
                Id = "5", Row = "A6", Sec = "A", Sev = "High",
                V = Verify.Ai,
                Title = Text.Same("Slab with no room"),
                Desc = Text.Same("A floor exists here but no room is placed. Lift spaces must be registered as rooms."),
                El = "Level 02 · Grid C/4 · Lift", Req = "A slab → needs a room", Act = Text.Same("no room"),
                Prop = null, Cite = "Borang A6 · PB p.37 Room",
                Fix = FixKind.None, Hash = "be2201f9c3d8",
                Reason = null,
            },
            new PanelItem
            {
                Id = "6", Row = "A7", Sec = "A", Sev = "Low",
                V = Verify.Ai,
                Title = Text.Same("Material not using JKR naming"),
                Desc = Text.Same("A default Revit material is used on a glass door."),
                El = "Materials · Doors (14 elements)", Req = "JKR_<type>_<specification>", Act = Text.Same("Glass"),
                Prop = "JKR_Kaca_Jernih_6mm", Cite = "Borang A7 · PB p.35 Material Identity",
                Fix = FixKind.Fix, Hash = "7e44c0a1b2f9",
                Reason = null,
            },
            new PanelItem
            {
                Id = "7", Row = "B1", Sec = "B", Sev = "Med",
                V = Verify.Ai,
                Title = Text.Same("Mandatory Project Information empty"),
                Desc = Text.Same("This parameter is mandatory from LOi 200. Enter the structural consultant name."),
                El = "Project Information · Perunding_Struktur_jkr_sit", Req = "Filled (LOi 200–500)", Act = Text.Same("(empty)"),
                Prop = null, Cite = "PB p.30 Project Information",
                Fix = FixKind.None, Hash = "c18d3e7a0f52",
                Reason = null,
            },
            new PanelItem
            {
                Id = "8", Row = "C1.1", Sec = "C", Sev = "Med",
                V = Verify.Ai,
                Title = Text.Same("Missing 3D view for Level 03"),
                Desc = Text.Same("Every level needs a 3D view per the JKR AR template."),
                El = "Views · Level 03", Req = "3D view for every level", Act = Text.Same("none"),
                Prop = "3D - Aras 03", Cite = "Borang C1.1 · JKR AR Template",
                Fix = FixKind.Fix, Hash = "4a9b7c21e0d3",
                Reason = null,
            },
            new PanelItem
            {
                Id = "9", Row = "C1.2", Sec = "C", Sev = "Low",
                V = Verify.Human,
                Title = Text.Same("WIP view set"),
                Desc = Text.Same("6 WIP views listed: floor and ceiling plans for Levels 01–03."),
                El = "Views · WIP (6)", Req = "Floor plan + ceiling plan for every level", Act = Text.Same("WIP_FP_L01…L03, WIP_CP_L01…L03"),
                Prop = null, Cite = "Borang C1.2",
                Fix = FixKind.None, Hash = "9f12d0b7a6c1",
                Reason = Text.Same("The form asks for a list only. Whether the WIP set is sufficient is a human call."),
            },
            new PanelItem
            {
                Id = "10", Row = "C1.6", Sec = "C", Sev = "High",
                V = Verify.Ai,
                Title = Text.Same("Room schedule not found"),
                Desc = Text.Same("The Schedule of Accommodation must be generated from the model. JKR rejects submissions without one."),
                El = "Schedules · Room", Req = "Room schedule exists & is complete", Act = Text.Same("none"),
                Prop = "Room Schedule (PB p.37)", Cite = "Borang C1.6 · NS §3.1 · PB p.36–37",
                Fix = FixKind.None, Hash = "e07a9c3b1d58",
                Reason = null,
            },
            new PanelItem
            {
                Id = "11", Row = "C3.1", Sec = "C", Sev = "Med",
                V = Verify.Ai,
                Title = Text.Same("Railings modelled without a schedule"),
                Desc = Text.Same("Every modelled category must appear in a schedule."),
                El = "Railings (5 elements)", Req = "A schedule exists", Act = Text.Same("no schedule"),
                Prop = "JKR_Jadual_Railing", Cite = "Borang C3.1 · PB p.80",
                Fix = FixKind.Fix, Hash = "11b6f4e2c90a",
                Reason = null,
            },
            new PanelItem
            {
                Id = "12", Row = "C4.1", Sec = "C", Sev = "Low",
                V = Verify.Ai,
                Title = Text.Same("Empty sheet"),
                Desc = Text.Same("This sheet contains no views."),
                El = "Sheets · A-105 Window Details", Req = "≥ 1 view", Act = Text.Same("0 views"),
                Prop = null, Cite = "Borang C4.1",
                Fix = FixKind.None, Hash = "5d3a8e0f7b26",
                Reason = null,
            },
            new PanelItem
            {
                Id = "13", Row = "C4.2", Sec = "C", Sev = "Med",
                V = Verify.Human,
                Title = Text.Same("Hardcopy timestamp matches softcopy"),
                Desc = Text.Same("JKR title block and project title are filled. Softcopy timestamp: 2026-11-08."),
                El = "Sheets (24) · Title block JKR_A1", Req = "Softcopy timestamp = hardcopy", Act = Text.Same("2026-11-08 / —"),
                Prop = null, Cite = "Borang C4.2 · NS §12.4.5",
                Fix = FixKind.None, Hash = "f6c2b19d04e7",
                Reason = Text.Same("The AI cannot read the hardcopy."),
            },
            new PanelItem
            {
                Id = "14", Row = "C5.1", Sec = "C", Sev = "High",
                V = Verify.Ai,
                Title = Text.Same("EL link file missing — CRITICAL"),
                Desc = Text.Same("The architectural model must link Structural, Mechanical and Electrical models."),
                El = "Revit Links · EL", Req = "ST ✓ · ME ✓ · EL", Act = Text.Same("EL: none"),
                Prop = null, Cite = "Borang C5.1 · BPEP",
                Fix = FixKind.None, Hash = "2b8e5f1a3c07",
                Reason = null,
            },
            new PanelItem
            {
                Id = "15", Row = "C5.1", Sec = "C", Sev = "Med",
                V = Verify.Human,
                Title = Text.Same("Link files are latest version"),
                Desc = Text.Same("ST and ME links are loaded. Version status cannot be confirmed."),
                El = "Revit Links · ST, ME", Req = "Latest version", Act = Text.Same("—"),
                Prop = null, Cite = "Borang C5.1",
                Fix = FixKind.None, Hash = "8c71a0e4d2b9",
                Reason = Text.Same("No CDE access. Check for the latest version manually."),
            },
            new PanelItem
            {
                Id = "16", Row = "D", Sec = "D", Sev = "Med",
                V = Verify.Ai,
                Title = Text.Same("Generic door type name"),
                Desc = Text.Same("Type names must follow JKR coding. Generic names fail outright."),
                El = "Doors · 12 elements", Req = "(PTxpNNNa) W x H a", Act = Text.Same("Door 1"),
                Prop = "(PT2p600a) 900 x 2325 a", Cite = "Borang D · Coding JKR",
                Fix = FixKind.Fix, Hash = "d4e9b2a7c113",
                Reason = null,
            },
            new PanelItem
            {
                Id = "17", Row = "D", Sec = "D", Sev = "Med",
                V = Verify.Ai,
                Title = Text.Same("Wall name does not match Width"),
                Desc = Text.Same("The value in the name (146) differs from the Width parameter (150)."),
                El = "Walls · JKR_Brick_Wall_146", Req = "Width = 146", Act = Text.Same("Width = 150"),
                Prop = "JKR_Dinding_Bata_150", Cite = "Borang D · PB p.95 Wall",
                Fix = FixKind.Fix, Hash = "a3f07c58e2d1",
                Reason = null,
            },
            new PanelItem
            {
                Id = "18", Row = "D", Sec = "D", Sev = "High",
                V = Verify.Ai,
                Title = Text.Same("Window modelled in Door category"),
                Desc = Text.Same("The name indicates a window but the Revit category is Door."),
                El = "Doors [220871] TK_Nako_600", Req = "Category: Windows", Act = Text.Same("Category: Doors"),
                Prop = null, Cite = "Borang D · PB p.99 Window",
                Fix = FixKind.None, Hash = "60b2d9f1e4a8",
                Reason = null,
            },
            new PanelItem
            {
                Id = "19", Row = "E", Sec = "E", Sev = "Med",
                V = Verify.Ai,
                Title = Text.Same("BOMBA view uses PBT template"),
                Desc = Text.Same("Views in the BOMBA set must use the BOMBA view template."),
                El = "Views · BOMBA_FP_L01", Req = "BOMBA_Plan", Act = Text.Same("PBT_Plan"),
                Prop = "BOMBA_Plan", Cite = "Borang E · JKR AR Template",
                Fix = FixKind.Fix, Hash = "b75c3e09a1f2",
                Reason = null,
            },
        };
    }
}
