// Built-in BIM010 Model Audit Form (PINDAAN 01, 16 JULAI 2021).
//
// The JKR pane audits against this form without an attached PDF: the rows are
// the verified parser output of the form (Tests/data/bim010_expected_rows.tsv,
// checked page by page against bim010_model_audit_form.pdf), and AuditTools
// runs the same fill_audit checkers over them. Keys follow the prototype
// ("A1", "C1.1", "E1.2"); Section D rows are one per family category
// ("D10" = Doors). "x.0" rows are section headings and are never scored.
// System only — no Revit — so the row list and keys are unit-tested.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Audit
{
    /// <summary>One printed row of the BIM010 form.</summary>
    public sealed class Bim010Row
    {
        public string Section { get; }
        /// <summary>As printed: "1", "1.1", "4.2", "1.0".</summary>
        public string RowRef { get; }
        public string Description { get; }

        /// <summary>Prototype key: section + row ref, e.g. "A1", "C1.1", "D10".</summary>
        public string Key => Section + RowRef;

        /// <summary>"x.0" rows ("1.0 Views (Architectural)") are headings: no
        /// criterion, never scored (same rule as AuditMatching.IsSectionHeader).</summary>
        public bool IsHeading => RowRef.EndsWith(".0", StringComparison.Ordinal);

        public Bim010Row(string section, string rowRef, string description)
        {
            Section = section;
            RowRef = rowRef;
            Description = description;
        }
    }

    public static class Bim010Form
    {
        public const string FormName = "BIM010 Model Audit Form (PINDAAN 01, 16 JULAI 2021)";

        public static readonly string[] Sections = { "A", "B", "C", "D", "E" };

        /// <summary>Section titles as printed on the form.</summary>
        public static readonly IReadOnlyDictionary<string, string> SectionTitles = new Dictionary<string, string>
        {
            ["A"] = "MODEL INTEGRITY AND QUALITY",
            ["B"] = "PROJECT INFORMATION",
            ["C"] = "PROJECT BROWSER ORGANIZATION",
            ["D"] = "ARCHITECTURAL FAMILIES",
            ["E"] = "PROPERTIES AND VISIBILITY GRAPHIC",
        };

        /// <summary>Short section names for the pane (prototype SECN), BM and EN.</summary>
        public static readonly IReadOnlyDictionary<string, (string Ms, string En)> SectionNames =
            new Dictionary<string, (string, string)>
            {
                ["A"] = ("Integriti & kualiti model", "Model integrity & quality"),
                ["B"] = ("Maklumat projek", "Project information"),
                ["C"] = ("Project Browser", "Project Browser"),
                ["D"] = ("Families arkitek", "Architectural families"),
                ["E"] = ("View template", "View templates"),
            };

        /// <summary>The 50 rows of the form, in printed order.</summary>
        public static readonly IReadOnlyList<Bim010Row> Rows = new List<Bim010Row>
        {
            new Bim010Row("A", "1", "Architectural Model developed in accordance to specified architectural design."),
            new Bim010Row("A", "2", "Architectural Model files are named according to the PWD’s BIM Guidelines and Standards"),
            new Bim010Row("A", "3", "Architectural Model is structured according to rules that are easily managed. For example, architectural model is managed according to block, tower and podium."),
            new Bim010Row("A", "4", "Architectural Model to be positioned appropriately to the Project Base Point preferably Grid A and 1."),
            new Bim010Row("A", "5", "Architectural Model should be properly organised and using architectural gridlines and levels."),
            new Bim010Row("A", "6", "Rooms are catagorised by departments."),
            new Bim010Row("A", "7", "All materials are included in model elements."),
            new Bim010Row("B", "1", "Project Information should be updated."),
            new Bim010Row("C", "1.0", "Views (Architectural)"),
            new Bim010Row("C", "1.1", "Views (Architectural) created based from PWD’s architectural template."),
            new Bim010Row("C", "1.2", "WIP views are used for design development and design study purposes."),
            new Bim010Row("C", "1.3", "PBT views are used for PBT submission purposes."),
            new Bim010Row("C", "1.4", "BOMBA views are used for BOMBA submission purposes."),
            new Bim010Row("C", "1.5", "Dokumen views are used for contract documentation, contract implementation and final completion."),
            new Bim010Row("C", "1.6", "Spatial and Zoning Analysis are generated from Area Plan."),
            new Bim010Row("C", "2.0", "Legends"),
            new Bim010Row("C", "2.1", "Contents of Legends are managed as follows: a) Legends for Components b) General Notes"),
            new Bim010Row("C", "3.0", "Schedules/Quantities"),
            new Bim010Row("C", "3.1", "Schedules/Quantities shall be generated according to: a) Schedule of Accomodation b) Building Component Schedule c) Material Takeoff Schedule"),
            new Bim010Row("C", "4.0", "Sheets"),
            new Bim010Row("C", "4.1", "Contents of Sheets are architectural drawings that are managed according to the rules for architecture as follows: a) Title Block b) Drawing Title c) List of Drawings d) General Notes e) Plans f) Elevations g) Sections h) Details i) Others: _________________"),
            new Bim010Row("C", "4.2", "All architectural drawings should use JKR Title Block with furnished information."),
            new Bim010Row("C", "5.0", "Link Files"),
            new Bim010Row("C", "5.1", "Files for the models linked with Architectural Model are correct and up-to-date"),
            new Bim010Row("D", "1", "Floors"),
            new Bim010Row("D", "2", "Walls"),
            new Bim010Row("D", "3", "Ceilings"),
            new Bim010Row("D", "4", "Roofs"),
            new Bim010Row("D", "5", "Stairs"),
            new Bim010Row("D", "6", "Railings"),
            new Bim010Row("D", "7", "Ramps"),
            new Bim010Row("D", "8", "Room"),
            new Bim010Row("D", "9", "Curtain Systems"),
            new Bim010Row("D", "10", "Doors"),
            new Bim010Row("D", "11", "Windows"),
            new Bim010Row("D", "12", "Caseworks"),
            new Bim010Row("D", "13", "Furniture"),
            new Bim010Row("D", "14", "Plumbing Fixtures"),
            new Bim010Row("D", "15", "Furniture Systems"),
            new Bim010Row("D", "16", "Specialty Equipment"),
            new Bim010Row("D", "17", "Generic Models"),
            new Bim010Row("D", "18", "Columns"),
            new Bim010Row("D", "19", "Parking"),
            new Bim010Row("D", "20", "Pipes"),
            new Bim010Row("D", "21", "Structural Column"),
            new Bim010Row("D", "22", "Toposurface"),
            new Bim010Row("D", "23", "Mass"),
            new Bim010Row("E", "1.0", "View Template are Correctly Applied"),
            new Bim010Row("E", "1.1", "BOMBA Submission"),
            new Bim010Row("E", "1.2", "PBT Submission"),
        };

        private static readonly Dictionary<string, Bim010Row> _byKey =
            Rows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        /// <summary>Rows that carry a criterion (headings excluded).</summary>
        public static IEnumerable<Bim010Row> ScoredRows => Rows.Where(r => !r.IsHeading);

        /// <summary>The Section D family categories, in printed order (D1..D23).</summary>
        public static IReadOnlyList<string> DCategories =>
            Rows.Where(r => r.Section == "D").Select(r => r.Description).ToList();

        public static Bim010Row Find(string key) =>
            key != null && _byKey.TryGetValue(key.Trim(), out var r) ? r : null;

        /// <summary>Key for a form row as fill_audit parsed it (section + printed ref).</summary>
        public static string KeyOf(string section, string rowRef) => (section ?? "").Trim() + (rowRef ?? "").Trim();

        /// <summary>Section letter of a key ("C1.1" → "C"); "" when unknown.</summary>
        public static string SectionOf(string key) =>
            string.IsNullOrEmpty(key) ? "" : Find(key)?.Section ?? "";

        /// <summary>Section D key for a family category label as printed ("Doors" → "D10").</summary>
        public static string DKeyFor(string formCategory) =>
            Rows.FirstOrDefault(r => r.Section == "D"
                && string.Equals(r.Description, formCategory, StringComparison.OrdinalIgnoreCase))?.Key;

        /// <summary>The rows as fill_audit input (no PDF): same shape the form
        /// parser produces, so the same checkers match them.</summary>
        public static List<AuditFormRow> ToFormRows() => Rows.Select(r => new AuditFormRow
        {
            Section = r.Section,
            SectionTitle = SectionTitles[r.Section],
            RowRef = r.RowRef,
            Description = r.Description,
        }).ToList();
    }
}
