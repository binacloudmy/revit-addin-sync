using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Canonical command catalog — a direct port of design_handoff_revit_copilot/prototype/data.jsx.
    /// 5 vetted (Tier 1) tools with form schemas + 9 AI (Tier 2) commands with plan/code/result.
    /// Pure data (no Revit dependency) so it can be unit-tested via Tests/Tests.csproj.
    /// </summary>
    public static class CopilotCatalog
    {
        private static string S(IDictionary<string, object> v, string k)
            => v != null && v.TryGetValue(k, out var o) && o != null ? o.ToString() : "";

        // ─── Tier 1 — Vetted ─────────────────────────────────────────────────
        public static readonly List<ToolDef> Vetted = new List<ToolDef>
        {
            new ToolDef {
                Id = "open-view", BackendName = "open_view", Tier = 1,
                Title = "Open a view",
                Desc = "Jump to any view by name — floor plan, 3D, section, callout",
                Icon = "cube", TileBg = "#dbeafe", TileFg = "#1d4ed8", Category = "nav",
                Fields = new List<FieldDef> {
                    new FieldDef { Id = "type", Label = "View type", Kind = CpFieldKind.Seg,
                        Options = new[] { "Floor Plan", "3D", "Section", "Elevation", "Drafting" }, Default = "Floor Plan" },
                    new FieldDef { Id = "view", Label = "View name", Kind = CpFieldKind.Select,
                        Options = new[] { "Level 1", "Level 2", "Roof", "Site" }, Default = "Level 1",
                        Hint = "Autocompletes from your project browser." },
                },
                RunLabel = v => $"Open · {S(v, "view")}",
                PlanText = v => $"Switch active view to \"{S(v, "view")}\".",
                Result = v => new ResultModel { Kind = CpResultKind.Plain, Headline = $"Opened {S(v, "view")}", Sub = "Switched the active view." },
            },
            new ToolDef {
                Id = "rename", BackendName = "rename_elements", Tier = 1,
                Title = "Rename elements",
                Desc = "Find and replace across a category — names only",
                Icon = "filter", TileBg = "#fef3c7", TileFg = "#a16207", Category = "clean",
                Fields = new List<FieldDef> {
                    new FieldDef { Id = "category", Label = "Category", Kind = CpFieldKind.Select,
                        Options = new[] { "Walls", "Doors", "Windows", "Floors", "Views" }, Default = "Views" },
                    new FieldDef { Id = "find", Label = "Find", Kind = CpFieldKind.Text, Default = "Level" },
                    new FieldDef { Id = "replace", Label = "Replace with", Kind = CpFieldKind.Text, Default = "L" },
                    new FieldDef { Id = "scope", Label = "Scope", Kind = CpFieldKind.Seg,
                        Options = new[] { "Whole model", "Selection" }, Default = "Whole model" },
                },
                RunLabel = v => $"Rename · \"{S(v, "find")}\" → \"{S(v, "replace")}\"",
                PlanText = v => $"Find \"{S(v, "find")}\" and replace with \"{S(v, "replace")}\" in {S(v, "category")} names.",
                Result = v => new ResultModel { Kind = CpResultKind.List, Headline = "6 elements renamed",
                    Diffs = new List<DiffItem> {
                        new DiffItem("Level 1", "L1"), new DiffItem("Level 2", "L2"),
                        new DiffItem("Level 3", "L3"), new DiffItem("Level Roof", "L Roof"),
                        new DiffItem("Level Site", "L Site"), new DiffItem("Level Mech", "L Mech"),
                    } },
            },
            new ToolDef {
                Id = "set-param", BackendName = "set_parameter", Tier = 1,
                Title = "Set a parameter",
                Desc = "Batch-set a parameter across a category — skips read-only",
                Icon = "wall", TileBg = "#dcfce7", TileFg = "#15803d", Category = "modify",
                Fields = new List<FieldDef> {
                    new FieldDef { Id = "category", Label = "Category", Kind = CpFieldKind.Select,
                        Options = new[] { "Walls", "Doors", "Windows", "Floors", "Rooms" }, Default = "Walls" },
                    new FieldDef { Id = "param", Label = "Parameter", Kind = CpFieldKind.Select,
                        Options = new[] { "Fire Rating", "Comments", "Mark", "Phase" }, Default = "Fire Rating" },
                    new FieldDef { Id = "value", Label = "New value", Kind = CpFieldKind.Text, Default = "FRR-60" },
                    new FieldDef { Id = "scope", Label = "Scope", Kind = CpFieldKind.Seg,
                        Options = new[] { "Active view", "Whole model", "Selection" }, Default = "Whole model" },
                },
                RunLabel = v => $"Set {S(v, "param")} on all {S(v, "category")}",
                PlanText = v => $"Set \"{S(v, "param")}\" to \"{S(v, "value")}\" on every instance of {S(v, "category")}. Read-only instances will be skipped.",
                Result = v => new ResultModel { Kind = CpResultKind.Plain,
                    Headline = $"Updated 12 {S(v, "category").ToLowerInvariant()}",
                    Sub = $"Set to {S(v, "value")} · 3 skipped (read-only Type parameter)" },
            },
            new ToolDef {
                Id = "select", BackendName = "select_elements", Tier = 1,
                Title = "Select elements",
                Desc = "Select every instance in a category",
                Icon = "selection", TileBg = "#ede9fe", TileFg = "#6d28d9", Category = "select",
                Fields = new List<FieldDef> {
                    new FieldDef { Id = "category", Label = "Category", Kind = CpFieldKind.Select,
                        Options = new[] { "Walls", "Doors", "Windows", "Floors", "Rooms", "Furniture" }, Default = "Doors" },
                    new FieldDef { Id = "level", Label = "Limit to level", Kind = CpFieldKind.Select,
                        Options = new[] { "Any", "Level 1", "Level 2", "Roof" }, Default = "Any" },
                },
                RunLabel = v => $"Select all {S(v, "category")}",
                PlanText = v => {
                    var lvl = S(v, "level");
                    return $"Select every {S(v, "category")} instance{(lvl == "Any" ? "" : " on " + lvl)}.";
                },
                Result = v => new ResultModel { Kind = CpResultKind.Plain,
                    Headline = $"47 {S(v, "category").ToLowerInvariant()} selected", Sub = "Zoomed to selection bounds." },
            },
            new ToolDef {
                Id = "export-sched", BackendName = "export_schedule", Tier = 1,
                Title = "Export a schedule",
                Desc = "Revit schedule → Excel or CSV",
                Icon = "table", TileBg = "#e0f2fe", TileFg = "#0369a1", Category = "sheets",
                Fields = new List<FieldDef> {
                    new FieldDef { Id = "schedule", Label = "Schedule", Kind = CpFieldKind.Select,
                        Options = new[] { "Door Schedule", "Window Schedule", "Room Schedule", "Wall Quantities" }, Default = "Door Schedule" },
                    new FieldDef { Id = "format", Label = "Format", Kind = CpFieldKind.Seg,
                        Options = new[] { "xlsx", "csv" }, Default = "xlsx" },
                    new FieldDef { Id = "open", Label = "After export", Kind = CpFieldKind.Seg,
                        Options = new[] { "Open file", "Show in folder", "Do nothing" }, Default = "Open file" },
                },
                RunLabel = v => $"Export · {S(v, "schedule")} (.{S(v, "format")})",
                PlanText = v => $"Export \"{S(v, "schedule")}\" to .{S(v, "format")} in the active project folder.",
                Result = v => new ResultModel { Kind = CpResultKind.File,
                    Headline = $"{S(v, "schedule")}.{S(v, "format")}", Sub = "47 rows · 1.2 MB", Path = @"C:\Projects\jkrAR24\Exports\" },
            },
            new ToolDef {
                Id = "tag", BackendName = "tag_elements", Tier = 1,
                Title = "Tag elements",
                Desc = "Place tags on a category in the active view — deterministic",
                Icon = "layers", TileBg = "#e0e7ff", TileFg = "#4338ca", Category = "annotate",
                Fields = new List<FieldDef> {
                    new FieldDef { Id = "category", Label = "Category", Kind = CpFieldKind.Select,
                        Options = new[] { "Walls", "Doors", "Windows", "Floors", "Furniture" }, Default = "Walls" },
                    new FieldDef { Id = "mode", Label = "Tags", Kind = CpFieldKind.Seg,
                        Options = new[] { "One per type", "Every instance" }, Default = "One per type",
                        Hint = "One per type tags the longest instance of each type (walls skip < 1 m); every instance tags all." },
                },
                RunLabel = v => $"Tag {S(v, "category")}",
                PlanText = v => $"Tag {S(v, "category")} in the active view — {S(v, "mode").ToLowerInvariant()}.",
                Result = v => new ResultModel { Kind = CpResultKind.Plain,
                    Headline = "Tagged", Sub = "Placed tags in the active view." },
            },
        };

        // ─── Tier 2 — AI commands ────────────────────────────────────────────
        public static readonly List<ToolDef> Ai = new List<ToolDef>
        {
            new ToolDef {
                Id = "rename-level-prefix", BackendName = "rename_elements", Tier = 2,
                Title = "Rename levels with L-prefix",
                Desc = "Find \"Level X\" and rename to \"LX\" — view names update too",
                Icon = "filter", TileBg = "#fef3c7", TileFg = "#a16207", Category = "clean",
                Plan = new List<string> {
                    "Find every Level whose name starts with \"Level \"",
                    "Rename to use the \"L\" prefix (Level 1 → L1, Level 2 → L2, …)",
                    "View names that reference the level update automatically",
                },
                Code = @"using var tx = new Transaction(doc, ""Rename levels"");
tx.Start();
var levels = new FilteredElementCollector(doc)
  .OfClass(typeof(Level)).Cast<Level>();

foreach (var level in levels) {
  if (level.Name.StartsWith(""Level "")) {
    level.Name = ""L"" + level.Name.Substring(6);
  }
}
tx.Commit();",
                Result = v => new ResultModel { Kind = CpResultKind.List, Headline = "4 levels renamed",
                    Diffs = new List<DiffItem> {
                        new DiffItem("Level 1", "L1"), new DiffItem("Level 2", "L2"),
                        new DiffItem("Level 3", "L3"), new DiffItem("Level Roof", "LRoof"),
                    } },
            },
            new ToolDef {
                Id = "count-doors", BackendName = "code", Tier = 2,
                Title = "Count doors by level",
                Desc = "Group doors by level and return totals",
                Icon = "door", TileBg = "#fef3c7", TileFg = "#a16207", Category = "query",
                Plan = new List<string> {
                    "Collect every door instance in the active model",
                    "Group by the door's Level Id",
                    "Return a count per level — read-only, no model changes",
                },
                Code = @"var doors = new FilteredElementCollector(doc)
  .OfCategory(BuiltInCategory.OST_Doors)
  .WhereElementIsNotElementType();

return doors
  .GroupBy(d => doc.GetElement(d.LevelId)?.Name)
  .Select(g => new {
    Level = g.Key, Count = g.Count()
  })
  .OrderBy(x => x.Level);",
                Result = v => new ResultModel { Kind = CpResultKind.Count, Headline = "47", Unit = "doors", Sub = "across 3 levels",
                    Bars = new List<BarItem> {
                        new BarItem("Level 1 — Ground", 18, "#1d4ed8"),
                        new BarItem("Level 2 — First", 21, "#7c3aed"),
                        new BarItem("Roof", 8, "#0891b2"),
                    } },
            },
            new ToolDef {
                Id = "walls-missing-frr", BackendName = "code", Tier = 2,
                Title = "Walls missing fire rating",
                Desc = "QA check — finds walls with no FRR parameter",
                Icon = "warning", TileBg = "#fee2e2", TileFg = "#b91c1c", Category = "compliance",
                Plan = new List<string> {
                    "Iterate all wall instances in the model",
                    "Check the \"Fire Rating\" type parameter",
                    "Return any wall where the value is empty or null",
                },
                Code = @"var walls = new FilteredElementCollector(doc)
  .OfCategory(BuiltInCategory.OST_Walls)
  .WhereElementIsNotElementType();

return walls.Where(w => {
  var p = w.LookupParameter(""Fire Rating"");
  return p == null || string.IsNullOrEmpty(p.AsString());
});",
                Result = v => new ResultModel { Kind = CpResultKind.Issues, Headline = "7", Unit = "walls flagged",
                    Items = new List<IssueItem> {
                        new IssueItem("Wall-014", "Level 1 · Corridor partition · 12,840 mm"),
                        new IssueItem("Wall-022", "Level 1 · Egress wall · 8,200 mm"),
                        new IssueItem("Wall-031", "Level 2 · Stair core · 5,600 mm"),
                    } },
            },
            new ToolDef {
                Id = "tag-walls", BackendName = "code", Tier = 2,
                Title = "Tag all walls in active view",
                Desc = "Place wall tags on every wall visible in the current view",
                Icon = "layers", TileBg = "#e0e7ff", TileFg = "#4338ca", Category = "annotate",
                Plan = new List<string> {
                    "Find all walls visible in the active view",
                    "Place a Wall Tag at the midpoint of each",
                    "Skip any wall that already has a tag",
                },
                Code = @"var view = uidoc.ActiveView;
var walls = new FilteredElementCollector(doc, view.Id)
  .OfCategory(BuiltInCategory.OST_Walls)
  .WhereElementIsNotElementType();

using var tx = new Transaction(doc, ""Tag walls"");
tx.Start();
foreach (var w in walls) {
  var loc = ((LocationCurve)w.Location).Curve;
  var mid = loc.Evaluate(0.5, true);
  IndependentTag.Create(doc, tagType.Id, view.Id,
    new Reference(w), false, TagOrientation.Horizontal, mid);
}
tx.Commit();",
                Result = v => new ResultModel { Kind = CpResultKind.Plain, Headline = "24 walls tagged", Sub = "Active view · 2 already had tags (skipped)" },
            },
            new ToolDef {
                Id = "set-frr-corridor", BackendName = "code", Tier = 2,
                Title = "Set FRR-60 on corridor doors",
                Desc = "Batch modify with conditional predicate",
                Icon = "door", TileBg = "#fef3c7", TileFg = "#a16207", Category = "modify",
                Plan = new List<string> {
                    "Find every door whose host wall is on a corridor (Room Name contains \"Corridor\")",
                    "Set their Fire Rating parameter to \"FRR-60\"",
                    "Wrap in a transaction — fully undoable",
                },
                Code = @"using var tx = new Transaction(doc, ""Set FRR-60"");
tx.Start();
var corridors = doors.Where(d => {
  var room = d.FromRoom ?? d.ToRoom;
  return room?.Name?.Contains(""Corridor"") == true;
});
foreach (var d in corridors) {
  d.LookupParameter(""Fire Rating"")?.Set(""FRR-60"");
}
tx.Commit();",
                Result = v => new ResultModel { Kind = CpResultKind.Plain, Headline = "14 doors updated", Sub = "Fire Rating → FRR-60 · 2 skipped (no host room)" },
            },
            new ToolDef {
                Id = "place-views-sheet", BackendName = "code", Tier = 2,
                Title = "Place 4 views on sheet A101",
                Desc = "Auto-layout views on a new title block",
                Icon = "table", TileBg = "#dbeafe", TileFg = "#1d4ed8", Category = "sheets",
                Plan = new List<string> {
                    "Create or find sheet \"A101\"",
                    "Place 4 selected views in a 2×2 grid",
                    "Apply default title block \"A1 Landscape\"",
                },
                Code = @"var sheet = ViewSheet.Create(doc, titleBlockType.Id);
sheet.SheetNumber = ""A101"";
sheet.Name = ""General Arrangement"";

var positions = new[] {
  new XYZ(0.5, 0.5, 0), new XYZ(1.2, 0.5, 0),
  new XYZ(0.5, 0.3, 0), new XYZ(1.2, 0.3, 0),
};
for (int i = 0; i < views.Count; i++) {
  Viewport.Create(doc, sheet.Id, views[i].Id, positions[i]);
}",
                Result = v => new ResultModel { Kind = CpResultKind.Plain, Headline = "Sheet A101 created", Sub = "4 viewports placed · A1 Landscape title block" },
            },
            new ToolDef {
                Id = "import-doors-excel", BackendName = "code", Tier = 2,
                Title = "Import door types from Excel",
                Desc = "Read .xlsx, create or update Door types",
                Icon = "chart", TileBg = "#dcfce7", TileFg = "#15803d", Category = "excel",
                Plan = new List<string> {
                    "Read \"doors.xlsx\" from the project folder",
                    "For each row, find or create a Door type matching the Name column",
                    "Set Width, Height, and Fire Rating from the row",
                },
                Code = @"var rows = Excel.Read(""doors.xlsx"", ""Sheet1"");
using var tx = new Transaction(doc, ""Import doors"");
tx.Start();
foreach (var r in rows) {
  var type = FindOrCreateDoorType(doc, r[""Name""]);
  type.LookupParameter(""Width"")?.Set(MmToFt(r[""Width""]));
  type.LookupParameter(""Height"")?.Set(MmToFt(r[""Height""]));
  type.LookupParameter(""Fire Rating"")?.Set(r[""FRR""]);
}
tx.Commit();",
                Result = v => new ResultModel { Kind = CpResultKind.Plain, Headline = "8 door types", Sub = "5 created · 3 updated from doors.xlsx" },
            },
            new ToolDef {
                Id = "purge-unused", BackendName = "code", Tier = 2,
                Title = "Purge unused families",
                Desc = "Find and remove never-instanced families",
                Icon = "cube", TileBg = "#f1f5f9", TileFg = "#475569", Category = "clean",
                Plan = new List<string> {
                    "Scan every loaded family",
                    "Find ones with zero instances anywhere in the model",
                    "Show preview list — only purges after you confirm",
                },
                Code = @"var loadedFamilies = new FilteredElementCollector(doc)
  .OfClass(typeof(Family))
  .Cast<Family>();

var unused = loadedFamilies.Where(f => {
  var instances = new FilteredElementCollector(doc)
    .OfClass(typeof(FamilyInstance))
    .OfCategoryId(f.FamilyCategory.Id)
    .Any(i => ((FamilyInstance)i).Symbol.Family.Id == f.Id);
  return !instances;
});",
                Result = v => new ResultModel { Kind = CpResultKind.List, Headline = "23 unused families",
                    Diffs = new List<DiffItem> {
                        new DiffItem("jkrAR18_drw_DD_900x2100", "0 instances"),
                        new DiffItem("M_Window-Casement", "0 instances"),
                        new DiffItem("M_Chair-Office", "0 instances"),
                    } },
            },
            new ToolDef {
                Id = "ubbl-rooms", BackendName = "code", Tier = 2,
                Title = "UBBL minimum room areas",
                Desc = "Compliance check against Malaysian UBBL standards",
                Icon = "warning", TileBg = "#fef3c7", TileFg = "#a16207", Category = "compliance",
                Plan = new List<string> {
                    "For each room, check Area against UBBL Schedule R minimums",
                    "(Bedroom ≥ 6.5m², Bathroom ≥ 1.8m², Kitchen ≥ 4.5m²)",
                    "Flag any room below threshold",
                },
                Code = @"var mins = new Dictionary<string, double> {
  { ""Bedroom"", 6.5 }, { ""Bathroom"", 1.8 },
  { ""Kitchen"", 4.5 }, { ""Living"", 10.0 },
};
return rooms.Where(r => {
  var name = r.Name.Split('_').First();
  if (!mins.TryGetValue(name, out var min)) return false;
  return r.Area * 0.092903 < min; // sqft → m²
});",
                Result = v => new ResultModel { Kind = CpResultKind.Issues, Headline = "3", Unit = "rooms below UBBL minimum",
                    Items = new List<IssueItem> {
                        new IssueItem("Bedroom 2", "Level 1 · 6.2 m² (min 6.5 m²)"),
                        new IssueItem("Bathroom 1", "Level 2 · 1.6 m² (min 1.8 m²)"),
                        new IssueItem("Kitchen", "Level 1 · 4.1 m² (min 4.5 m²)"),
                    } },
            },
            // Neutral catalog entry used when the chat falls through to
            // bina-ai's /generate (Inspector-preflighted free-form
            // codegen). Plan + Code are filled in at runtime from the
            // RouteResult; the doors-flavoured QueryInterpreter default
            // never wins for an unrouted free-form prompt anymore.
            new ToolDef {
                Id = "ai-generated", BackendName = "code", Tier = 2,
                Title = "AI-generated command",
                Desc = "Free-form codegen with Inspector preflight against your live model",
                Icon = "sparkles", TileBg = "#ede9fe", TileFg = "#6d28d9", Category = "query",
                Plan = new List<string> {
                    "Inspector picks the right read-only tools to ground the request",
                    "Generates Revit C# referencing your real levels / types / selection",
                    "Wraps in a Transaction — fully undoable",
                },
                Code = "",  // runtime-filled from /generate response
                Result = v => new ResultModel { Kind = CpResultKind.Plain, Headline = "AI command ready", Sub = "Review the plan and Run when ready." },
            },
        };

        public static readonly List<CategoryDef> Categories = new List<CategoryDef>
        {
            new CategoryDef("all", "All", 14),
            new CategoryDef("query", "Query", 1),
            new CategoryDef("modify", "Modify", 2),
            new CategoryDef("select", "Select", 1),
            new CategoryDef("sheets", "Sheets & views", 2),
            new CategoryDef("annotate", "Annotate", 1),
            new CategoryDef("nav", "Navigation", 1),
            new CategoryDef("excel", "Excel I/O", 1),
            new CategoryDef("clean", "Cleanup", 3),
            new CategoryDef("compliance", "Compliance", 2),
        };

        public static IEnumerable<ToolDef> All => Vetted.Concat(Ai);

        public static ToolDef Find(string id) => All.FirstOrDefault(t => t.Id == id);

        /// <summary>Resolve a tool by its backend name (e.g. "rename_elements"),
        /// used by the hybrid vetted-dispatch contract. Prefers vetted (Tier-1).</summary>
        public static ToolDef FindByBackendName(string backendName)
            => string.IsNullOrEmpty(backendName) ? null
               : Vetted.FirstOrDefault(t => t.BackendName == backendName)
                 ?? All.FirstOrDefault(t => t.BackendName == backendName);

        public static bool IsVetted(string id) => Vetted.Any(t => t.Id == id);
    }
}
