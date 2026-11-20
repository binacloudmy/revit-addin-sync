using System.Collections.Generic;

namespace RevitWebAppSync.Services.JkrSpec
{
    // ─────────────────────────────────────────────────────────────────────────
    // The model as the JKR Model Audit spec checks need it: plain data, read
    // once on Revit's thread by SpecModelReader (the only Revit-dependent file),
    // then checked by the pure evaluators in SpecChecks*.cs. No Revit types here,
    // so every rule is unit-tested with hand-built snapshots.
    //
    // Units: lengths in millimetres, areas in m², angles in degrees.
    // Every list may be empty and every string may be "" (never null after
    // SpecModel.Normalize) — evaluators must not assume a field was read.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class SpecModel
    {
        /// <summary>Model file name with extension ("jkrAR24_5a_(BEde1A_p14-001)_A1_w-01.rvt"); "" when unsaved.</summary>
        public string FileName = "";
        /// <summary>Scan discipline (AR / CD / EL / ME / ST).</summary>
        public string Discipline = "AR";
        /// <summary>Project LOD the run was started with (100–500).</summary>
        public int Lod;

        public List<SpecLevel> Levels = new List<SpecLevel>();
        public List<SpecGrid> Grids = new List<SpecGrid>();
        /// <summary>Project Base Point position on plan (internal coordinates, mm, the same
        /// frame as the grid end points); null when not read.</summary>
        public SpecPoint ProjectBasePoint;
        public List<SpecRoom> Rooms = new List<SpecRoom>();
        public List<SpecFloor> Floors = new List<SpecFloor>();
        public List<SpecCeiling> Ceilings = new List<SpecCeiling>();
        public List<SpecRoof> Roofs = new List<SpecRoof>();
        /// <summary>Element types with at least one placed instance, all model categories.</summary>
        public List<SpecType> Types = new List<SpecType>();
        /// <summary>Every material in the document (used or not).</summary>
        public List<SpecMaterial> Materials = new List<SpecMaterial>();
        /// <summary>Non-template views (plans, elevations, sections, 3D, drafting, legends, area plans).</summary>
        public List<SpecView> Views = new List<SpecView>();
        /// <summary>Names of the view templates in the document.</summary>
        public List<string> ViewTemplates = new List<string>();
        public List<SpecSchedule> Schedules = new List<SpecSchedule>();
        public List<SpecSheet> Sheets = new List<SpecSheet>();
        public List<SpecLink> Links = new List<SpecLink>();
        /// <summary>Project Information parameters: name → value as shown ("" when empty).</summary>
        public Dictionary<string, string> ProjectInfo = new Dictionary<string, string>();
        /// <summary>Revit category name → number of placed model elements (e.g. "Doors" → 24).</summary>
        public Dictionary<string, int> CategoryCounts = new Dictionary<string, int>();
        /// <summary>Things the reader could not read ("Sheets: …exception…"): shown, never a silent pass.</summary>
        public List<string> ReadErrors = new List<string>();

        /// <summary>Replace nulls left by a reader or a JSON fixture with empty values.</summary>
        public SpecModel Normalize()
        {
            FileName = FileName ?? "";
            Discipline = string.IsNullOrWhiteSpace(Discipline) ? "AR" : Discipline.Trim().ToUpperInvariant();
            Levels = Levels ?? new List<SpecLevel>();
            Grids = Grids ?? new List<SpecGrid>();
            Rooms = Rooms ?? new List<SpecRoom>();
            Floors = Floors ?? new List<SpecFloor>();
            Ceilings = Ceilings ?? new List<SpecCeiling>();
            Roofs = Roofs ?? new List<SpecRoof>();
            Types = Types ?? new List<SpecType>();
            Materials = Materials ?? new List<SpecMaterial>();
            Views = Views ?? new List<SpecView>();
            ViewTemplates = ViewTemplates ?? new List<string>();
            Schedules = Schedules ?? new List<SpecSchedule>();
            Sheets = Sheets ?? new List<SpecSheet>();
            Links = Links ?? new List<SpecLink>();
            ProjectInfo = ProjectInfo ?? new Dictionary<string, string>();
            CategoryCounts = CategoryCounts ?? new Dictionary<string, int>();
            ReadErrors = ReadErrors ?? new List<string>();
            Levels.RemoveAll(x => x == null); Grids.RemoveAll(x => x == null); Rooms.RemoveAll(x => x == null);
            Floors.RemoveAll(x => x == null); Ceilings.RemoveAll(x => x == null); Roofs.RemoveAll(x => x == null);
            Types.RemoveAll(x => x == null); Materials.RemoveAll(x => x == null); Views.RemoveAll(x => x == null);
            Schedules.RemoveAll(x => x == null); Sheets.RemoveAll(x => x == null); Links.RemoveAll(x => x == null);
            ViewTemplates.RemoveAll(x => x == null);
            foreach (var x in Levels) x.Name = x.Name ?? "";
            foreach (var x in Grids) x.Name = x.Name ?? "";
            foreach (var x in Rooms) { x.Name = x.Name ?? ""; x.Number = x.Number ?? ""; x.Department = x.Department ?? ""; x.LevelName = x.LevelName ?? ""; x.Params = x.Params ?? new Dictionary<string, string>(); x.Loops = CleanLoops(x.Loops); }
            foreach (var x in Floors) { x.TypeName = x.TypeName ?? ""; x.LevelName = x.LevelName ?? ""; x.Loops = CleanLoops(x.Loops); }
            foreach (var x in Ceilings) { x.TypeName = x.TypeName ?? ""; x.LevelName = x.LevelName ?? ""; x.Loops = CleanLoops(x.Loops); }
            foreach (var x in Roofs) { x.TypeName = x.TypeName ?? ""; x.LevelName = x.LevelName ?? ""; x.Materials = x.Materials ?? new List<string>(); x.LayerFunctions = x.LayerFunctions ?? new List<string>(); }
            foreach (var x in Types) { x.Category = x.Category ?? ""; x.FamilyName = x.FamilyName ?? ""; x.TypeName = x.TypeName ?? ""; x.InstanceIds = x.InstanceIds ?? new List<long>(); x.Numbers = x.Numbers ?? new Dictionary<string, double>(); x.Texts = x.Texts ?? new Dictionary<string, string>(); x.Materials = x.Materials ?? new List<string>(); x.NoMaterialInstanceIds = x.NoMaterialInstanceIds ?? new List<long>(); }
            foreach (var x in Materials) { x.Name = x.Name ?? ""; x.Class = x.Class ?? ""; }
            foreach (var x in Views) { x.Name = x.Name ?? ""; x.Kind = x.Kind ?? ""; x.LevelName = x.LevelName ?? ""; x.TemplateName = x.TemplateName ?? ""; x.SheetNumber = x.SheetNumber ?? ""; }
            foreach (var x in Schedules) { x.Name = x.Name ?? ""; x.Category = x.Category ?? ""; x.Fields = x.Fields ?? new List<string>(); x.RowElementIds = x.RowElementIds ?? new List<long>(); }
            foreach (var x in Sheets) { x.Number = x.Number ?? ""; x.Name = x.Name ?? ""; x.TitleBlock = x.TitleBlock ?? ""; x.Params = x.Params ?? new Dictionary<string, string>(); x.ViewKinds = x.ViewKinds ?? new List<string>(); x.ViewNames = x.ViewNames ?? new List<string>(); }
            foreach (var x in Links) { x.Name = x.Name ?? ""; x.Status = x.Status ?? ""; }
            return this;
        }

        /// <summary>Plan loops without nulls and without loops of fewer than 3 points.</summary>
        private static List<List<SpecPoint>> CleanLoops(List<List<SpecPoint>> loops)
        {
            var list = loops ?? new List<List<SpecPoint>>();
            list.RemoveAll(l => l == null);
            foreach (var l in list) l.RemoveAll(p => p == null || double.IsNaN(p.X) || double.IsNaN(p.Y));
            list.RemoveAll(l => l.Count < 3);
            return list;
        }
    }

    public sealed class SpecLevel
    {
        public long Id;
        public string Name = "";
        /// <summary>Elevation above the project base point, mm.</summary>
        public double Elevation;
        /// <summary>Building story (Revit "Building Story" checked).</summary>
        public bool IsStory = true;
    }

    public sealed class SpecGrid
    {
        public long Id;
        public string Name = "";
        /// <summary>Straight grid direction: true = runs mostly along Y (a "number" axis line placed along X).</summary>
        public bool IsVertical;
        /// <summary>Position across the grid direction, mm (X for vertical grids, Y for horizontal).</summary>
        public double Offset;
        /// <summary>A straight grid whose end points were read (X0, Y0 → X1, Y1, plan mm,
        /// internal coordinates). False for arcs, multi-segment grids and old snapshots.</summary>
        public bool HasLine;
        public double X0, Y0, X1, Y1;
    }

    /// <summary>A plan point, mm.</summary>
    public sealed class SpecPoint
    {
        public double X, Y;
    }

    public sealed class SpecRoom
    {
        public long Id;
        public string Name = "";
        public string Number = "";
        public string Department = "";
        public string LevelName = "";
        /// <summary>m²; 0 = not placed / not enclosed.</summary>
        public double Area;
        /// <summary>The room has a location point (placed). With Area 0 it is placed but not
        /// enclosed or redundant; false with Area 0 = not placed. Null = not read (old snapshot).</summary>
        public bool? Placed;
        /// <summary>Location point, mm.</summary>
        public double X, Y;
        /// <summary>Every room parameter with a value (name → shown value; "" when empty), for schedule-column checks.</summary>
        public Dictionary<string, string> Params = new Dictionary<string, string>();
        /// <summary>The room's boundary in plan (finish faces), mm: outer loop and holes, each a
        /// closed polygon (last point not repeated). Empty = not read (old snapshot, not enclosed).</summary>
        public List<List<SpecPoint>> Loops = new List<List<SpecPoint>>();
        /// <summary>Where the room's volume starts: its level's elevation + base offset, mm
        /// (project frame, as SpecLevel.Elevation). Null = not read.</summary>
        public double? BaseElevation;
        /// <summary>Unbounded height of the room's volume above <see cref="BaseElevation"/>, mm. Null = not read.</summary>
        public double? Height;
    }

    public sealed class SpecFloor
    {
        public long Id;
        public string TypeName = "";
        public string LevelName = "";
        /// <summary>Plan bounding box, mm.</summary>
        public double MinX, MinY, MaxX, MaxY;
        /// <summary>m².</summary>
        public double Area;
        public bool IsStructural;
        /// <summary>Elevation of the floor's top face, mm (project frame, as SpecLevel.Elevation);
        /// the highest point when the top is sloped. Null = not read.</summary>
        public double? TopElevation;
        /// <summary>The top face's edge loops in plan, mm: outer loops and holes, each a closed
        /// polygon (last point not repeated); inside = odd number of loops around a point.
        /// Empty = not read (the plan bounding box is the only footprint then).</summary>
        public List<List<SpecPoint>> Loops = new List<List<SpecPoint>>();
    }

    public sealed class SpecCeiling
    {
        public long Id;
        public string TypeName = "";
        public string LevelName = "";
        /// <summary>Height offset from its level, mm: where its underside sits above the level.</summary>
        public double HeightOffset;
        /// <summary>Elevation of the ceiling's underside, mm (project frame, as SpecLevel.Elevation);
        /// the lowest point when it is sloped. Null = not read (level + HeightOffset is used then).</summary>
        public double? BottomElevation;
        /// <summary>The underside's edge loops in plan, mm, same form as <see cref="SpecFloor.Loops"/>.
        /// Empty = not read: the ceiling is then measured from its level, not from the floor under it.</summary>
        public List<List<SpecPoint>> Loops = new List<List<SpecPoint>>();
    }

    public sealed class SpecRoof
    {
        public long Id;
        public string TypeName = "";
        public string LevelName = "";
        /// <summary>Largest slope, degrees (0 = flat).</summary>
        public double SlopeDegrees;
        /// <summary>Names of the materials in its layers.</summary>
        public List<string> Materials = new List<string>();
        /// <summary>Layer functions ("Structure", "Membrane", "Finish1" …).</summary>
        public List<string> LayerFunctions = new List<string>();
    }

    public sealed class SpecType
    {
        public long TypeId;
        /// <summary>Revit category name ("Doors", "Walls", "Structural Columns").</summary>
        public string Category = "";
        public string FamilyName = "";
        public string TypeName = "";
        public int InstanceCount;
        /// <summary>Placed instance ids (capped by the reader).</summary>
        public List<long> InstanceIds = new List<long>();
        /// <summary>Numeric type/instance values in mm where lengths: "Width", "Height", "Thickness", "Rough Width" …</summary>
        public Dictionary<string, double> Numbers = new Dictionary<string, double>();
        /// <summary>Text parameters (JKR shared parameters included): name → value.</summary>
        public Dictionary<string, string> Texts = new Dictionary<string, string>();
        /// <summary>Material names on the type (layers or material parameters); empty = no material.</summary>
        public List<string> Materials = new List<string>();
        /// <summary>Placed instances whose own GetMaterialIds(false) is empty (capped like InstanceIds).
        /// Empty = none found, or not read (older snapshots). A7.1 uses it when the type has a material
        /// only through a Material-typed parameter (AI-419).</summary>
        public List<long> NoMaterialInstanceIds = new List<long>();
        /// <summary>Count of such instances among those read (see NoMaterialInstancesSampled).</summary>
        public int NoMaterialInstanceCount;
        /// <summary>True when the type's material comes ONLY from a Material-typed type parameter (no
        /// compound layers, no instance parameter, no geometry material on the first instance). Only then
        /// is the per-instance material gap judged (AI-419).</summary>
        public bool MaterialFromTypeParamOnly;
        /// <summary>Round 1 (Director decision): the instance-level A7.1 check applies ONLY to these
        /// categories (Generic Models, the AI-419 case). Every other category keeps the type-level rule.
        /// Widen here in Round 2.</summary>
        public static readonly string[] InstanceMaterialCheckCategories = { "Generic Models" };
        /// <summary>True when the instance-level material check applies to this category.</summary>
        public static bool InstanceMaterialCheckApplies(string category)
            => category != null && System.Array.IndexOf(InstanceMaterialCheckCategories, category.Trim()) >= 0;
        /// <summary>True when only the first InstanceIdCap instances were read, so the count is "at least".</summary>
        public bool NoMaterialInstancesSampled;
    }

    public sealed class SpecMaterial
    {
        public long Id;
        public string Name = "";
        /// <summary>Revit material class ("Glass", "Metal" …).</summary>
        public string Class = "";
        /// <summary>Number of placed elements using it (0 = unused).</summary>
        public int UsedBy;
    }

    public sealed class SpecView
    {
        public long Id;
        public string Name = "";
        /// <summary>FloorPlan | CeilingPlan | Elevation | Section | ThreeD | DraftingView | Detail | Legend | AreaPlan | Other.</summary>
        public string Kind = "";
        /// <summary>Associated level ("" for elevations, sections, drafting views).</summary>
        public string LevelName = "";
        /// <summary>Applied view template ("" = none).</summary>
        public string TemplateName = "";
        /// <summary>Placed on a sheet (shown blue in the project browser).</summary>
        public bool OnSheet;
        public string SheetNumber = "";
        /// <summary>The view's folders in the current Project Browser organisation, outermost
        /// first, joined by " / " ("07d_Dokumen / 01_Lukisan Utama / 3D Views"); "???" for a
        /// folder whose value is blank; "" = not organised into folders, or not read.</summary>
        public string FolderPath = "";
        /// <summary>The parameter each folder of <see cref="FolderPath"/> groups by, same order
        /// ("View_jkr_pit / Jenis_Lukisan_jkr_pit / Family"); a part is "" when unknown.</summary>
        public string FolderBy = "";
        /// <summary>The view's JKR set parameter View_jkr_pit ("07d_Dokumen"); "" = none or blank.</summary>
        public string SetParam = "";
    }

    public sealed class SpecSchedule
    {
        public long Id;
        public string Name = "";
        /// <summary>Scheduled category ("Rooms", "Doors" …; "" for multi-category / sheet lists).</summary>
        public string Category = "";
        /// <summary>Visible field names (parameter names).</summary>
        public List<string> Fields = new List<string>();
        /// <summary>Element ids the schedule lists.</summary>
        public List<long> RowElementIds = new List<long>();
        /// <summary>Categories of the elements it lists, spec names ("Ramps", "Walls"), each once;
        /// what a multi-category schedule or a material takeoff actually covers.</summary>
        public List<string> RowCategories = new List<string>();
        /// <summary>A material takeoff (single- or multi-category).</summary>
        public bool IsMaterialTakeoff;
        public bool IsTemplate;
    }

    public sealed class SpecSheet
    {
        public long Id;
        public string Number = "";
        public string Name = "";
        /// <summary>Title block family : type on the sheet ("" = none).</summary>
        public string TitleBlock = "";
        /// <summary>Sheet / title-block parameters: name → value ("Drawn By", "Sheet Issue Date", "Project Name" …).</summary>
        public Dictionary<string, string> Params = new Dictionary<string, string>();
        /// <summary>Kinds of the views placed on it (SpecView.Kind values).</summary>
        public List<string> ViewKinds = new List<string>();
        public List<string> ViewNames = new List<string>();
    }

    public sealed class SpecLink
    {
        /// <summary>Linked file name ("jkrST24_….rvt").</summary>
        public string Name = "";
        /// <summary>Loaded | Unloaded | NotFound.</summary>
        public string Status = "";
        public bool IsLoaded => string.Equals(Status, "Loaded", System.StringComparison.OrdinalIgnoreCase);
    }
}
