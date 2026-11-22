using System;
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
    /// "Tajuk"; English (AI-513), A–C as the printed form words them.</summary>
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
        /// <summary>The spec's "Apa yang disemak", in English.</summary>
        public Text Title { get; }
        public SpecCellClass Class { get; }
        /// <summary>The spec's "Rujukan" (NS clause, Buku Parameter page, Borang row, meeting time).</summary>
        public string Reference { get; }
        public string Section => SpecCatalog.SectionOf(Item);
        /// <summary>The spec's "COMPLY (✓) bila" (English). "—" = the spec has none.</summary>
        public Text Comply { get; set; } = Text.Same("");
        /// <summary>The spec's "TIDAK COMPLY (✗) bila" (English).</summary>
        public Text NotComply { get; set; } = Text.Same("");
        /// <summary>The spec's "SEMAK MANUAL / nota" (English).</summary>
        public Text Manual { get; set; } = Text.Same("");
        /// <summary>The spec's severity for a ✗ on this check: "critical" (the spec writes
        /// KRITIKAL), "major" (the default; the spec writes nothing) or "minor". Same words as
        /// the checker's severity, so IssueMapper.PriorityOf turns it into the board tier
        /// (critical → High, major → Medium, minor → Low).</summary>
        public string Severity { get; set; } = "major";
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

        private static Text Same(string s) => Text.Same(s);
        /// <summary>The spec's empty cell, kept verbatim.</summary>
        public const string EmptyCell = "—";
        private static readonly Text Dash = Text.Same(EmptyCell);

        /// <summary>True when a criterion cell is the spec's "—" (or blank).</summary>
        public static bool IsEmpty(Text cell) => cell == null || string.IsNullOrWhiteSpace(cell.En) || cell.En.Trim() == EmptyCell;

        /// <summary>Borang A-E items, "Tajuk" (the sheet has no E row: E keeps
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
            new SpecItem("D", "D-a", Same("Naming follows JKR coding")),
            new SpecItem("D", "D-b", Same("Information / parameters per the Buku Parameter, by LOD")),
            new SpecItem("D", "D-c", Same("Naming must tally with the parameters (integrity / geometry quality)")),
            new SpecItem("D", "D-d", Same("Integrity: name ↔ category")),
            new SpecItem("D", "D-e", Same("Special rows")),
            new SpecItem("E", "E", Same("Properties and Visibility Graphic: View Templates")),
        };

        /// <summary>"Peraturan comply", row by row, every cell in English.</summary>
        public static readonly IReadOnlyList<SpecCheck> Checks = new[]
        {
            // ── A ──
            new SpecCheck("A1.1", "A1", Same("Ceiling height (NS §4.1.9)"), Ai, "NS §4.1.9")
            {
                Comply = Same("Every level: floor-to-ceiling >= 3000 mm; basement >= 2500 mm"),
                NotComply = Same("Any level < 3000 mm (or basement < 2500 mm) — list the level + the actual value"),
                Manual = Same("A value that only reaches the minimum when rounded is listed for a person — JKR gives no rounding rule"),
            },
            new SpecCheck("A1.2", "A1", Same("Door size (NS §5.8.5)"), Ai, "NS §5.8.5")
            {
                Comply = Same("Single-leaf door >= 900 mm; double >= 1500 mm; clear height >= 2100 mm"),
                NotComply = Same("Any door type below the minimum — list `Type | width | height`"),
                Manual = Same("Clear opening and double leaf: JKR gives no method to turn the nominal size into a clear opening, or to tell a double leaf from its name — listed, a person decides. A value that only reaches the minimum when rounded is listed for a person — JKR gives no rounding rule"),
            },
            new SpecCheck("A1.3", "A1", Same("Main door solid core (NS §5.8.1/5.8.3)"), Ai, "NS §5.8.1, §5.8.3")
            {
                Comply = Same("Main door: solid core and thickness >= 40 mm (thickness parameter filled)"),
                NotComply = Same("Thickness < 40 mm, or the parameter does not exist on the main door"),
                Manual = Same("The main door cannot be identified from the model — list the candidates, a person confirms. A value that only reaches the minimum when rounded is listed for a person — JKR gives no rounding rule"),
            },
            new SpecCheck("A1.4", "A1", Same("Flat roof / waterproofing (NS §5.2.3 + §5.14)"), Ai, "NS §5.2.3, §5.14")
            {
                Comply = Same("No RC flat roof; OR a flat roof AND a waterproofing / double-roofing element exists in the model"),
                NotComply = Same("An RC roof without a waterproofing layer or material — LISTED for a person (JKR gives no flat-roof definition), not an automatic ✗"),
                Manual = Same("What counts as a flat roof, and whether it is justified (design justification) — a person"),
            },
            new SpecCheck("A1.5", "A1", Same("Decorative fence (NS §4.8.3)"), Ai, "NS §4.8.3")
            {
                Comply = Same("Decorative fence height >= 2400 mm"),
                NotComply = Same("Height < 2400 mm"),
                Manual = Same("No fence element in the model — confirm the project scope. A value that only reaches the minimum when rounded is listed for a person — JKR gives no rounding rule"),
            },
            new SpecCheck("A1.6", "A1", Same("Other NS clauses (sustainability, IBS, landscape)"), List, "NS (other)")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST only the relevant NS clauses that cannot be checked from the model. No verdict, no pending count."),
            },
            new SpecCheck("A1.7", "A1", Same("A1 scope = requirements in the Need Statement only"), List, "Principle 0.3, NS")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("A1 only checks requirements WRITTEN in the Need Statement. Design concept / aesthetics are not assessed (0.3) — give a remark only."),
            },
            new SpecCheck("A2.1", "A2", Same("Model file name follows the PWD BIM Guidelines"), Ai, "F A.2, T 21:16")
            {
                Comply = Same("The file name has every required segment (>= 3 segments as the guidelines set)"),
                NotComply = Same("Segments incomplete / wrong format — show the segments detected"),
                Manual = Dash,
            },
            new SpecCheck("A2.2", "A2", Same("Model code read from the file name"), Ai, "T 21:22")
            {
                Comply = Same("Code detected: W00 (single model) or WXnn (split model) — carried to A3"),
                NotComply = Same("The code cannot be read from the file name"),
                Manual = Dash,
            },
            new SpecCheck("A3.1", "A3", Same("Single model (code W00)"), Ai, "T 21:19, 21:22, 21:23")
            {
                Comply = Same("Code = W00 → COMPLIES DIRECTLY (a one-piece model, e.g. a hall)"),
                NotComply = Dash,
                Manual = Dash,
            },
            new SpecCheck("A3.2", "A3", Same("Split model (code WXnn) — links exist"), Ai, "T 21:19–21:21")
            {
                Comply = Same("Every part file (WX01, WX02…) is present as a link in the model"),
                NotComply = Same("A part is not linked — list the missing files"),
                Manual = Dash,
            },
            new SpecCheck("A3.3", "A3", Same("Model parts vs the file list in the BPEP"), Ai, "T 22:23–22:25")
            {
                Comply = Same("Every model file listed in the BPEP is found as a link"),
                NotComply = Same("A file in the BPEP is not in the model (or the other way round)"),
                Manual = Same("BPEP not available in RAG → cannot be checked; state why"),
            },
            new SpecCheck("A3.4", "A3", Same("Split model but no links at all"), List, "T 21:22, 21:24")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("CANNOT BE CHECKED — list only, with the remark 'The model must be linked first before the A3 check can run'"),
            },
            new SpecCheck("A3.5", "A3", Same("How the model is split (description)"), List, "T 21:19–21:20")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST how the model is split (e.g. 'WX01 Tower, WX02 Podium, WX03 Basement'). Description only — no verdict, no pending count."),
            },
            new SpecCheck("A4.1", "A4", Same("Model position at the Project Base Point (Grid A/1)"), Ai, "F A.4")
            {
                Comply = Same("Project Base Point exactly at Grid A/1 (0 mm)"),
                NotComply = Same("Offset is not 0 mm — show the offset in mm (X/Y); JKR gives no offset limit, a person decides"),
                Manual = Same("Project-prefixed grids (e.g. PT-A / PT1) are not read as Grid A/1 — listed, a person decides"),
            },
            new SpecCheck("A5.1", "A5", Same("Number of grids and levels"), List, "F A.5")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST only: number of grids + number of levels (basic information, no verdict)"),
            },
            new SpecCheck("A5.2", "A5", Same("Gridline sequencing"), Ai, "T 21:24–21:26")
            {
                Comply = Same("Grids in order with no jumps: numbers 1,2,3,4… and letters A,B,C… (left→right or reversed, top→bottom or bottom→top — all accepted)"),
                NotComply = Same("A jump in the sequence (e.g. 1,2,3 → 11) — list the gaps"),
                Manual = Dash,
            },
            new SpecCheck("A5.3", "A5", Same("Sub-grids between main grids"), Ai, "T 21:25")
            {
                Comply = Same("A valid sub-grid pattern: 1A,1B,1C or A1,A2,A3 — listed under the main grid"),
                NotComply = Same("— (sub-grids are NOT counted as wrong)"),
                Manual = Dash,
            },
            new SpecCheck("A6.1", "A6", Same("Rooms listed per level"), List, "T 21:26–21:33")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST: `Level | Room name | Number | Department | Area` for EVERY level (10 levels = 10 levels checked)"),
            },
            new SpecCheck("A6.2", "A6", Same("A slab must have a room"), Ai, "T 21:28–21:33")
            {
                Comply = Same("Every slab area has a defined room (lifts, stairs and aprons included)"),
                NotComply = Same("A slab without a room — list `Level | Grid location | Type (lift/stair/apron)`. A person decides whether it is an exception (lift, stair, apron)"),
                Manual = Dash,
            },
            new SpecCheck("A6.3", "A6", Same("Department parameter"), Human, "T 21:26, 21:34")
            {
                Comply = Same("Department filled (if the project type needs it: hospital / office)"),
                NotComply = Same("— (DO NOT flag MAJOR just because Department is empty)"),
                Manual = Same("An empty Department = INFORMATION only. Whether the project type needs a Department = a person confirms (schools / halls have no department)"),
            },
            new SpecCheck("A7.1", "A7", Same("Every element has a material"), Ai, "T 21:42–21:43")
            {
                Comply = Same("Every element in every category has a material (not empty)"),
                NotComply = Same("Elements without a material — list `Category | Type name | No. of elements`"),
                Manual = Dash,
            },
            new SpecCheck("A7.2", "A7", Same("Material naming follows JKR"), Human, "T 21:41, 21:46, PB p.35")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("WARNING only, NOT ✗: list the materials that use a Revit default value or a non-JKR name (e.g. 'Glass (door)', 'Metal White'). JKR itself cannot call it wrong; a person judges. The material coding that is mandatory is checked in D-b"),
            },
            new SpecCheck("A7.3", "A7", Same("Material choice is correct"), Human, "T 21:44")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("NOT ASSESSED in A7 — only existence + naming. Material correctness is checked in section D"),
            },
            // ── B ──
            new SpecCheck("B1.1", "B1", Same("Mandatory Project Information fields for the project LOI"), Ai, "PB p.30–34, T 21:51–21:54")
            {
                Comply = Same("Every field marked mandatory at the project LOI (PB p.30–34) is filled with a real value"),
                NotComply = Same("A mandatory field is empty OR still a Revit default — list `Parameter | Mandatory at LOI | Value | Status`"),
                Manual = Dash,
            },
            new SpecCheck("B1.2", "B1", Same("Other disciplines (ST / ME / EL)"), Ai, "T 21:55, PB p.120/161/209")
            {
                Comply = Dash,
                NotComply = Same("One 'Not compliant' row per discipline + a list of the unfilled fields only"),
                Manual = Dash,
            },
            new SpecCheck("B1.3", "B1", Same("Fill summary"), List, "T 21:54")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST: 'n/m fields filled (x%)' — summary information"),
            },
            // ── C ──
            new SpecCheck("C1.0.1", "C1.0", Same("Parent row Views (Architectural)"), List, "T 21:56")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("NO CHECK — parent row only; check the children 1.1–1.6"),
            },
            new SpecCheck("C1.1.1", "C1.1", Same("JKR AR template views exist in the project"), Ai, "T 21:56–22:00")
            {
                Comply = Same("Every view of the JKR AR template exists in the project (compared with the template, not with the project)"),
                NotComply = Same("A template view was deleted — list the missing views"),
                Manual = Same("The view list comes from the JKR AR template; without that list it cannot be checked — state the reason"),
            },
            new SpecCheck("C1.1.2", "C1.1", Same("Template views renamed"), Ai, "T 21:58")
            {
                Comply = Same("Every template view keeps its original name"),
                NotComply = Same("A template view was renamed (renamed = WRONG; rare)"),
                Manual = Same("Awaiting JKR's confirmation: is a renamed view accepted as long as it exists ('existing is enough')"),
            },
            new SpecCheck("C1.1.3", "C1.1", Same("Number of levels is enough"), Human, "T 22:04–22:05")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("The AI lists the number of levels detected; a PERSON confirms whether it is enough"),
            },
            new SpecCheck("C1.2.1", "C1.2", Same("WIP view set for every level"), List, "T 22:00–22:05")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST ONLY `View type | Level | View name`, ordered by level (floor plan, ceiling plan, section for every level up to the roof) so a level without views shows — DO NOT give a comply verdict"),
            },
            new SpecCheck("C1.3.1", "C1.3", Same("PBT view set (floor plan, elevation, section)"), Ai, "T 22:01, 22:04")
            {
                Comply = Same("All three required types are present in the PBT set"),
                NotComply = Same("Any type missing — checklist `floor plan ✓ | elevation ✓ | section ✗`"),
                Manual = Same("Awaiting JKR's confirmation: automatic ✗, or the AI lists and a person decides"),
            },
            new SpecCheck("C1.4.1", "C1.4", Same("BOMBA view set (floor plan, section, drafting view)"), Ai, "T 22:01, 22:05")
            {
                Comply = Same("All three required types are present in the BOMBA set"),
                NotComply = Same("Any type missing — checklist"),
                Manual = Same("Awaiting JKR's confirmation: automatic ✗, or the AI lists and a person decides"),
            },
            new SpecCheck("C1.5.1", "C1.5", Same("Dokumen views + status on sheets"), List, "T 22:02–22:03")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST `View name | Type | Level | On a sheet? (blue)`"),
            },
            new SpecCheck("C1.5.2", "C1.5", Same("3D view for every level (Dokumen set)"), Ai, "T 22:39–22:40")
            {
                Comply = Same("The Dokumen view set has one 3D view for every level"),
                NotComply = Same("A level without a Dokumen 3D view — e.g. '3D view for every level: 3/4 ✗ (Level 03 missing)'; state the reason"),
                Manual = Dash,
            },
            new SpecCheck("C1.6.1", "C1.6", Same("Room schedule exists"), Ai, "T 21:36–22:08")
            {
                Comply = Same("A room schedule / Schedule of Accommodation exists in the project"),
                NotComply = Same("No room schedule → ✗"),
                Manual = Dash,
            },
            new SpecCheck("C1.6.2", "C1.6", Same("Every room is in the schedule"), Ai, "N2, T 22:06–22:08")
            {
                Comply = Same("Every room in the model is in the room schedule"),
                NotComply = Same("A room in the model is not in the schedule — list them"),
                Manual = Dash,
            },
            new SpecCheck("C1.6.3", "C1.6", Same("Every room schedule column is filled"), Ai, "PB p.36–37, T 22:06–22:08")
            {
                Comply = Same("Every mandatory column (PB p.36 Area / p.37 Room) has a value for every room"),
                NotComply = Same("An empty column — list `Room | Empty column` (e.g. 'Meeting Room 1 | Floor finish, Wall finish'). Incomplete = JKR rejects"),
                Manual = Dash,
            },
            new SpecCheck("C2.1.1", "C2.1", Same("Legends and their status on drawings"), Ai, "T 22:08–22:09")
            {
                Comply = Same("At least one legend exists → ✓"),
                NotComply = Same("No legend at all"),
                Manual = Same("LIST `Legend name | Type | On a drawing? (blue)` for a person to review"),
            },
            new SpecCheck("C3.1.1", "C3.1", Same("Modelled categories must have a schedule"), Ai, "T 22:10–22:13")
            {
                Comply = Same("Every AR category (PB p.36–103 list) that is MODELLED has a schedule"),
                NotComply = Same("Modelled but no schedule — list them (e.g. 'Railing: 5 elements, no schedule')"),
                Manual = Dash,
            },
            new SpecCheck("C3.1.2", "C3.1", Same("Categories not modelled"), List, "T 22:14–22:16")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("Mark 'n/a' — NOT a shortcoming. Non-AR categories (lines, dimensions) are not listed at all"),
            },
            new SpecCheck("C4.1.1", "C4.1", Same("List of all sheets"), List, "T 22:17–22:18")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("LIST `Sheet no. | Name | No. of views | Content type` — a person reviews (sheets are sometimes combined)"),
            },
            new SpecCheck("C4.1.2", "C4.1", Same("Checklist across all sheets"), Ai, "T 22:18–22:19, N3")
            {
                Comply = Same("All present across the sheet set: floor plan, ceiling plan, elevation, section, door detail, window detail"),
                NotComply = Same("Any type missing from every sheet"),
                Manual = Same("A sheet named for door / window details with no Detail view: no JKR document says it counts — listed, a person decides"),
            },
            new SpecCheck("C4.1.3", "C4.1", Same("Empty sheets"), Ai, "F C.4.1")
            {
                Comply = Same("Every sheet holds at least one view"),
                NotComply = Same("A sheet without a view → ✗"),
                Manual = Dash,
            },
            new SpecCheck("C4.2.1", "C4.2", Same("JKR title block used"), Ai, "F C.4.2")
            {
                Comply = Same("The title block family used is the JKR title block"),
                NotComply = Same("Title block not JKR / none"),
                Manual = Dash,
            },
            new SpecCheck("C4.2.2", "C4.2", Same("Title block information filled"), Ai, "PB p.103, T 22:20–22:21")
            {
                Comply = Same("All filled: project title, drawing title, modelled by, timestamp"),
                NotComply = Same("Any empty cell → ✗ on that cell"),
                Manual = Dash,
            },
            new SpecCheck("C4.2.3", "C4.2", Same("Softcopy vs hardcopy timestamp match"), Human, "NS §12.4.5, T 22:22")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("The AI only confirms the softcopy timestamp. Matching the HARDCOPY = MANUAL CHECK (the AI has no access)"),
            },
            new SpecCheck("C5.1.1", "C5.1", Same("Discipline links ST / ME / EL"), Ai, "T 22:21–22:22")
            {
                Comply = Same("All three disciplines are present as links with status Loaded"),
                NotComply = Same("A required discipline has no link OR status Unloaded → ✗ CRITICAL"),
                Manual = Dash,
                Severity = "critical",
            },
            new SpecCheck("C5.1.2", "C5.1", Same("Link file names vs the BPEP"), Ai, "T 22:23–22:25")
            {
                Comply = Same("Link file names match the file list in the BPEP"),
                NotComply = Same("Names do not match / a BPEP file is not in the model"),
                Manual = Same("BPEP not available → cannot be checked"),
            },
            new SpecCheck("C5.1.3", "C5.1", Same("Links are the latest version"), Human, "T 22:22–22:23")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("ALWAYS A MANUAL CHECK — the AI has no CDE / sync access. Remark: 'the latest version must be checked by hand'"),
            },
            // ── D ──
            new SpecCheck("Da.1", "D-a", Same("Type name follows JKR coding"), Ai, "T 22:27–22:28")
            {
                Comply = Same("The type name matches the JKR coding pattern (e.g. `(PT2p600a) 900 x 2325 a`)"),
                NotComply = Same("A generic name ('Door 1', 'Basic Wall', 'door bla bla') → ✗ DIRECTLY. Show `Type name | Count | Suggested name`"),
                Manual = Dash,
            },
            new SpecCheck("Db.1", "D-b", Same("Mandatory parameters per Buku Parameter × LOD"), Ai, "PB per category, T 22:28")
            {
                Comply = Same("Every parameter mandatory at the project LOI for that category is filled (PB: Door p.51, Window p.99, Wall p.95, Floor p.55, Ceiling p.42, Roof p.83, Stair p.91, Railing p.80, Ramp p.76, Curtain p.48, Casework p.38, Plumbing p.72, Generic p.67, Column p.45)"),
                NotComply = Same("A mandatory parameter is empty — summary 'Door: 9/12 LOI 300 parameters filled'"),
                Manual = Dash,
            },
            new SpecCheck("Db.2", "D-b", Same("Material on elements"), Ai, "T 22:28–22:29")
            {
                Comply = Same("Material filled AND follows JKR coding"),
                NotComply = Same("Material empty or not JKR-coded"),
                Manual = Dash,
            },
            new SpecCheck("Dc.1", "D-c", Same("Values in the name tally with the geometry parameters"), Ai, "T 22:33–22:34")
            {
                Comply = Same("The number in the type name (taken as mm) equals the actual parameter value (e.g. `Wall 146` → Width = 146)"),
                NotComply = Same("No match — list `Type name | Value in name | Parameter | Actual value` (e.g. 'Wall 146 | 146 | Width | 150 ✗')"),
                Manual = Dash,
            },
            new SpecCheck("Dd.1", "D-d", Same("Name ↔ Revit category"), Ai, "T 22:35")
            {
                Comply = Same("The Revit category matches the category the name implies (a floor in the Floor category, a window not in the Door category)"),
                NotComply = Same("No match — show ONLY the mismatches"),
                Manual = Dash,
            },
            new SpecCheck("Dd.2", "D-d", Same("Modelling technique (how surfaces are placed etc.)"), Human, "T 22:35–22:36")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("MANUAL CHECK — outside the scope of the automatic check"),
            },
            new SpecCheck("De.1", "D-e", Same("Room (F D.8)"), List, "F D.8")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("No ElementType — replaced by the room checks in A6 / C1.6"),
            },
            new SpecCheck("De.2", "D-e", Same("Columns / Structural Column with 0 elements"), Human, "F D.18, D.21")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("0 elements → 'confirm scope' (A PERSON). DO NOT flag MAJOR automatically"),
            },
            new SpecCheck("De.3", "D-e", Same("Categories not modelled"), List, "C 3.1")
            {
                Comply = Dash,
                NotComply = Dash,
                Manual = Same("Mark 'not applicable' (consistent with C 3.1)"),
            },
            // ── E ──
            new SpecCheck("E.1", "E", Same("View template right for its set"), Ai, "T 22:37–22:39")
            {
                Comply = Same("Every view uses its own set's template: BOMBA→Bomba template, PBT→PBT template, Dokumen→Dokumen template"),
                NotComply = Same("No view template AT ALL, OR a template from the WRONG set — show `Set | View name | Template used | Template expected | Match?`"),
                Manual = Same("WIP views: no template rule from the meeting — listed only, awaiting JKR's confirmation"),
            },
        };

        public static SpecCheck Find(string id) => Checks.FirstOrDefault(c => c.Id == id);
        public static IEnumerable<SpecItem> ItemsOf(string section) => Items.Where(i => i.Section == section);
        public static IEnumerable<SpecCheck> ChecksOf(string item) => Checks.Where(c => c.Item == item);
        public static string SectionOf(string item) => Items.FirstOrDefault(i => i.No == item)?.Section ?? "";

        private static readonly System.Text.RegularExpressions.Regex TranscriptTime =
            new System.Text.RegularExpressions.Regex(@"^(?:T\s+)?\d{1,2}:\d{2}(?:\s*[–-]\s*\d{1,2}:\d{2})?$");

        /// <summary>
        /// A check's reference as users see it: without the spec meeting's transcript times
        /// ("T 22:27–22:28", "T 21:26, 21:34"), which mean nothing outside the spec's own
        /// source. "F A.2, T 21:16" → "F A.2"; "T 22:18–22:19, N3" → "N3". The catalog
        /// (<see cref="SpecCheck.Reference"/>) keeps them.
        /// </summary>
        public static string ShownReference(string reference)
        {
            var parts = (reference ?? "").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var kept = new List<string>();
            var inTranscript = false;
            foreach (var p in parts)
            {
                // "T 21:19" opens a run of times; a bare "21:22" after it belongs to it.
                if (p.StartsWith("T ", StringComparison.Ordinal) && TranscriptTime.IsMatch(p)) { inTranscript = true; continue; }
                if (inTranscript && TranscriptTime.IsMatch(p)) continue;
                inTranscript = false;
                kept.Add(p);
            }
            return string.Join(", ", kept);
        }
    }
}
