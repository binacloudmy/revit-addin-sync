using System.Collections.Generic;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>Stub issues mirroring design handoff data.jsx — used until backend wiring lands.</summary>
    public static class StubData
    {
        public static string Filename => "jkrAR24_5a_(BEde1A_p14-001)…";

        public static List<IssueVm> Build() => new List<IssueVm>
        {
            new IssueVm {
                Id = "I-001", Title = "File name does not use 'jkr' prefix",
                Category = "Project Naming", Priority = IssuePriority.High, Status = IssueStatus.Open,
                Description = "File 'Copy of jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a' does not start with 'jkr'.",
                Required = "jkr{Discipline}{Code}_{Phase}_({ProjectID})_{Zone}_{Level}_{Status}_{Date}",
                Actual   = "Copy of jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a",
                Example  = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a",
                AutoFixable = true,
                HowToFix = "Rename the Revit file to start with 'jkr' followed by the discipline code.",
                Spec = new SpecRef { Doc = "doc04", Clause = "4.2.1", Page = 38, Quote = "All Revit project files shall be named using the prefix \"jkr\" followed by the discipline code and project parameters separated by underscores." },
                Element = new ElementRef { Id = "—", Name = "Revit Project File" },
            },
            new IssueVm {
                Id = "I-002", Title = "Missing type parameter: Bidang_Kejuruteraan_jkr_stt",
                Category = "Component Parameter", Priority = IssuePriority.High, Status = IssueStatus.Open,
                Description = "Type parameter 'Bidang_Kejuruteraan_jkr_stt' required at LOI 300 for Wall.",
                Required = "Bidang_Kejuruteraan_jkr_stt", Actual = "(not found)",
                AutoFixable = true,
                HowToFix = "Add shared parameter 'Bidang_Kejuruteraan_jkr_stt' to Wall category.",
                Spec = new SpecRef { Doc = "doc09", Clause = "LOI-300 §2.4.1", Page = 14, Quote = "At LOI 300 (Detail Design), all Wall elements shall include type parameter \"Bidang_Kejuruteraan_jkr_stt\" populated with the responsible engineering discipline." },
                Element = new ElementRef { Id = "498231", Name = "Wall : Basic Wall : Generic - 200mm" },
            },
            new IssueVm {
                Id = "I-003", Title = "Project Base Point coordinates not set",
                Category = "Project Base Point", Priority = IssuePriority.High, Status = IssueStatus.Open,
                Description = "Project Base Point is at model origin (0,0,0). Survey coordinates not applied.",
                Required = "Survey coordinates per JUPEM reference",
                Actual   = "E: 0.000  N: 0.000  Elev: 0.000",
                AutoFixable = false,
                Steps = new List<string> {
                    "Go to the Manage tab in the Revit ribbon.",
                    "Click Coordinates → Specify Coordinates at Point.",
                    "Click the model origin (Project Base Point symbol in plan view).",
                    "Enter the N/E/Elevation values from the JUPEM survey sheet.",
                    "Click OK. Verify the Project Base Point now shows the survey coordinates."
                },
                HowToFix = "Open Manage → Coordinates → Specify Coordinates at Point.",
                Spec = new SpecRef { Doc = "doc07", Clause = "3.1.4", Page = 21, Quote = "Project Base Point shall be set to the shared coordinate system referenced to JUPEM survey data prior to modelling." },
                Element = new ElementRef { Id = "—", Name = "Project Base Point" },
            },
            new IssueVm {
                Id = "I-004", Title = "Grid naming does not follow convention",
                Category = "Grids", Priority = IssuePriority.Medium, Status = IssueStatus.Open,
                Description = "Grid 'Grid 7' should use letter+number convention (e.g. 'A-1', 'A-2').",
                Required = "Alphanumeric grid names: A,B,C… for horizontal; 1,2,3… for vertical",
                Actual   = "Grid 7",
                AutoFixable = true,
                HowToFix = "Rename grids in sequence. Letters horizontal, numbers vertical.",
                Spec = new SpecRef { Doc = "doc05", Clause = "5.3.2", Page = 47, Quote = "Grid lines shall be labelled with capital letters (horizontal) and numerals (vertical) in sequence from the lower-left corner of the building footprint." },
                Element = new ElementRef { Id = "312087", Name = "Grid : 6.5mm : Grid 7" },
            },
            new IssueVm {
                Id = "I-005", Title = "Level name missing elevation suffix",
                Category = "Levels", Priority = IssuePriority.Medium, Status = IssueStatus.Open,
                Description = "Level 'Level 2' should include elevation in name, e.g. 'L02 +4.500'.",
                Required = "L{NN} +{elevation_m}", Actual = "Level 2",
                AutoFixable = true,
                HowToFix = "Rename to 'L02 +4.500' using the level's actual elevation value.",
                Spec = new SpecRef { Doc = "doc05", Clause = "5.4.1", Page = 49, Quote = "Level names shall follow the format L## +#.### where ## is the level number and #.### is the elevation in metres from the reference datum." },
                Element = new ElementRef { Id = "312156", Name = "Level : 8mm Head : Level 2" },
            },
            new IssueVm {
                Id = "I-006", Title = "Project Information: Client field empty",
                Category = "Project Information", Priority = IssuePriority.Medium, Status = IssueStatus.Open,
                Description = "Manage → Project Information → 'Client Name' is empty.",
                Required = "Client Name populated", Actual = "(empty)",
                AutoFixable = false,
                Steps = new List<string> {
                    "Open the Manage tab in the Revit ribbon.",
                    "Click Project Information.",
                    "Locate the Client Name parameter.",
                    "Type the client name exactly as it appears in the project brief.",
                    "Click OK to save."
                },
                HowToFix = "Fill in Client Name under Manage → Project Information.",
                Spec = new SpecRef { Doc = "doc01", Clause = "2.2.3", Page = 15, Quote = "Project Information parameters (Client Name, Project Name, Project Number, Address) shall be populated at project inception." },
                Element = new ElementRef { Id = "—", Name = "Project Information" },
            },
            new IssueVm {
                Id = "I-007", Title = "Component name does not follow naming rule",
                Category = "Component Naming", Priority = IssuePriority.Medium, Status = IssueStatus.Open,
                Description = "Family type 'Door-Single-Flush' should be prefixed with discipline (e.g. 'AR-Door-Single-Flush').",
                Required = "{Discipline}-{Type}-{Variant}", Actual = "Door-Single-Flush",
                AutoFixable = true,
                HowToFix = "Rename type to AR-Door-Single-Flush.",
                Spec = new SpecRef { Doc = "doc05", Clause = "6.1.2", Page = 58, Quote = "Component type names shall be prefixed with the two-letter discipline code (AR, ST, ME, EL, PL) followed by the component type." },
                Element = new ElementRef { Id = "498765", Name = "Door : Single-Flush : 900x2100mm" },
            },
            new IssueVm {
                Id = "I-008", Title = "LOD 400 parameter missing: Manufacturer",
                Category = "LOD 400/500 parameter", Priority = IssuePriority.Low, Status = IssueStatus.Open,
                Description = "LOD 400 requires 'Manufacturer' parameter on all mechanical equipment.",
                Required = "Manufacturer (text, populated)", Actual = "(empty)",
                AutoFixable = false,
                Steps = new List<string> {
                    "Select the mechanical equipment in the view.",
                    "In Properties panel, scroll to Identity Data.",
                    "Fill in the Manufacturer field using the approved supplier name.",
                    "Repeat for all equipment of the same type."
                },
                HowToFix = "Populate 'Manufacturer' parameter per supplier data.",
                Spec = new SpecRef { Doc = "doc06", Clause = "LOI-400 §4.2", Page = 22, Quote = "At LOI 400 (Construction), mechanical equipment shall include manufacturer, model number, and serial reference." },
                Element = new ElementRef { Id = "512339", Name = "Mechanical Equipment : AHU-01" },
            },
            new IssueVm {
                Id = "I-009", Title = "LOD 500 parameter missing: AssetTag",
                Category = "LOD 400/500 parameter", Priority = IssuePriority.Low, Status = IssueStatus.Open,
                Description = "LOD 500 requires 'AssetTag' parameter for FM handover.",
                Required = "AssetTag (text)", Actual = "(empty)",
                AutoFixable = false,
                Steps = new List<string> {
                    "Export current fixtures to Excel (Schedule → Export).",
                    "Match each fixture to its CAFM AssetTag from the facility register.",
                    "Import the filled AssetTag values back via Revit → Modify → Properties.",
                    "Verify each fixture has a unique, non-empty AssetTag."
                },
                HowToFix = "Populate AssetTag per CAFM asset register.",
                Spec = new SpecRef { Doc = "doc06", Clause = "LOI-500 §5.1", Page = 28, Quote = "At LOI 500 (As-Built), every maintainable asset shall carry a unique AssetTag matching the facility management CAFM register." },
                Element = new ElementRef { Id = "512401", Name = "Plumbing Fixture : WC-01" },
            },
            new IssueVm {
                Id = "I-010", Title = "Unused view has non-standard name",
                Category = "Project Naming", Priority = IssuePriority.Low, Status = IssueStatus.Open,
                Description = "View 'Perspektif Hadapan_Rendered' uses mixed-language naming.",
                Required = "English naming in {ViewType}_{Description} format",
                Actual   = "Perspektif Hadapan_Rendered",
                AutoFixable = true,
                HowToFix = "Rename to '3D_Front_Rendered' or similar English format.",
                Spec = new SpecRef { Doc = "doc04", Clause = "4.3.1", Page = 42, Quote = "All view names shall be in English and follow the format {ViewType}_{Description} using underscores as separators." },
                Element = new ElementRef { Id = "201008", Name = "3D View : Perspektif Hadapan_Rendered" },
            },
        };
    }
}
