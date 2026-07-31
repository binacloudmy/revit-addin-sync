using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Command type — drives the badge tag + icon-tile colour.</summary>
    public enum ToolBadge { Deterministic, AiAssisted, Report }

    /// <summary>One slash-command tool. Mirrors the design file's cmds() entries
    /// (docs/design/copilot-panel-slate.dc.html). These will later map to backend
    /// commands, so the catalogue lives here, not scattered in XAML.</summary>
    public sealed class SlashTool
    {
        public string Id;
        public string Name;
        public string Subtitle;
        public string Category;   // General · Architecture · Structure · MEP
        public ToolBadge Badge;
        public string IconKey;    // ti-*
        public string Keywords;   // search terms

        /// <summary>True = handled entirely in the addin (no backend command).
        /// The chat send path short-circuits these — see ChatSendSlashCommand.</summary>
        public bool Local;

        /// <summary>bina-ai P1 command id sent to the backend as `command_id`
        /// (authoritative dispatch). Set from the id map in the static ctor;
        /// BackendId falls back to Id when they already match.</summary>
        public string CommandId;
        public string BackendId => string.IsNullOrEmpty(CommandId) ? Id : CommandId;
    }

    public static class ToolCatalog
    {
        // Category order in the palette (Quick access is prepended dynamically).
        public static readonly string[] Categories = { "Actions", "General", "Architecture", "Structure", "MEP" };

        // 20 tools verbatim from the design file's slash catalogue + the 9 quick
        // commands from the 2026-07 Langfuse prod prompt mining
        // (docs/analysis/2026-07-30-slash-command-mining-prod-july.md in bina-ai).
        public static readonly IReadOnlyList<SlashTool> All = new List<SlashTool>
        {
            // ── Actions: generic verb commands ──
            new SlashTool { Id="create",    Category="Actions", Name="Create",             Subtitle="Create elements from description",              Badge=ToolBadge.AiAssisted,    IconKey="ti-plus",            Keywords="create make add new build buat bina tambah letak" },
            new SlashTool { Id="delete",    Category="Actions", Name="Delete",             Subtitle="Delete by selection / filter — preview first",  Badge=ToolBadge.Deterministic, IconKey="ti-trash",           Keywords="delete remove erase clear padam buang hapus" },
            new SlashTool { Id="change",    Category="Actions", Name="Change",             Subtitle="Swap type / material / parameter",              Badge=ToolBadge.Deterministic, IconKey="ti-exchange",        Keywords="change swap replace type material parameter tukar ganti" },
            new SlashTool { Id="rename",    Category="Actions", Name="Rename",             Subtitle="Bulk rename by pattern",                        Badge=ToolBadge.Deterministic, IconKey="ti-pencil",          Keywords="rename name pattern batch namakan nama semula" },
            new SlashTool { Id="open-view", Category="Actions", Name="Open View",          Subtitle="Open view / sheet by name",                     Badge=ToolBadge.Deterministic, IconKey="ti-eye",             Keywords="open view sheet goto jump navigate buka papar", Local=true },
            new SlashTool { Id="count",     Category="Actions", Name="Count / List",       Subtitle="Count & list model contents + loaded families", Badge=ToolBadge.Report,        IconKey="ti-sum",             Keywords="count list how many total inventory berapa kira senarai family loaded" },
            // ── Task tools from the same mining round ──
            new SlashTool { Id="clone",     Category="General", Name="Clone Sheet",        Subtitle="Duplicate template sheet per room / level",     Badge=ToolBadge.Deterministic, IconKey="ti-copy",            Keywords="clone duplicate sheet template copy salin" },
            new SlashTool { Id="place",     Category="General", Name="Place at Selection", Subtitle="Place family relative to selection",            Badge=ToolBadge.AiAssisted,    IconKey="ti-map-pin",         Keywords="place put position spacing selection letak jarak bawah tepi" },
            new SlashTool { Id="audit",     Category="General", Name="Name Audit",         Subtitle="JKR name audit → fix via /rename",              Badge=ToolBadge.Report,        IconKey="ti-clipboard-check", Keywords="audit naming standard jkr compliance check semak nama" },
            new SlashTool { Id="level-vis",    Category="General",       Name="Level Visualiser",          Subtitle="Compare levels host vs linked → .csv",         Badge=ToolBadge.Report,        IconKey="ti-git-compare",     Keywords="level elevation compare align link csv qa" },
            new SlashTool { Id="level-filter", Category="General",       Name="Level View Filter",         Subtitle="Filter / highlight elements by level",         Badge=ToolBadge.Deterministic, IconKey="ti-filter",          Keywords="level filter view highlight isolate element" },
            new SlashTool { Id="level-build",  Category="General",       Name="Level Builder",             Subtitle="Batch-generate levels by name / spacing",      Badge=ToolBadge.Deterministic, IconKey="ti-stack-2",         Keywords="level create generate build floor storey spacing batch" },
            new SlashTool { Id="batch-link",   Category="General",       Name="Batch Link",                Subtitle="Load / reload / relink models in bulk",        Badge=ToolBadge.Deterministic, IconKey="ti-link",            Keywords="link batch reload relink model consultant reference load" },
            new SlashTool { Id="cad-family",   Category="General",       Name="CAD Block to Family",       Subtitle="CAD block → native Revit family",              Badge=ToolBadge.AiAssisted,    IconKey="ti-transform",       Keywords="cad block family symbol convert import native map" },
            new SlashTool { Id="skata",        Category="General",       Name="SKATA Code",                Subtitle="Apply / audit project coding → .csv",          Badge=ToolBadge.Report,        IconKey="ti-hash",            Keywords="skata code standard audit classification parameter compliance csv" },
            new SlashTool { Id="lightvent",    Category="Architecture",  Name="Light / Ventilation Report", Subtitle="Room daylight + airflow check → schedule + .csv", Badge=ToolBadge.Report,      IconKey="ti-sun",             Keywords="light ventilation daylight window opening room compliance report schedule csv" },
            new SlashTool { Id="door-sched",   Category="Architecture",  Name="Graphical Door Schedule",   Subtitle="Auto graphical door schedule + sheet",         Badge=ToolBadge.Deterministic, IconKey="ti-door",            Keywords="door schedule graphical documentation sheet view elevation" },
            new SlashTool { Id="win-sched",    Category="Architecture",  Name="Graphical Window Schedule", Subtitle="Auto graphical window schedule + sheet",       Badge=ToolBadge.Deterministic, IconKey="ti-window",          Keywords="window schedule graphical documentation sheet view elevation" },
            new SlashTool { Id="room-views",   Category="Architecture",  Name="Room View / Plan / 3D",     Subtitle="Auto room views, plans & 3D per room",         Badge=ToolBadge.Deterministic, IconKey="ti-box-multiple",    Keywords="room view plan 3d documentation generate template naming" },
            new SlashTool { Id="walls-cad",    Category="Architecture",  Name="Walls from CAD",            Subtitle="CAD lines → native parametric walls",          Badge=ToolBadge.Deterministic, IconKey="ti-wall",            Keywords="wall cad line convert native parametric layout dwg" },
            new SlashTool { Id="walls-slab",   Category="Architecture",  Name="Walls from Slab Edge",      Subtitle="Slab edge → perimeter walls (incl. sloped)",   Badge=ToolBadge.Deterministic, IconKey="ti-border-corners",  Keywords="wall slab edge perimeter boundary sloped uneven parapet" },
            new SlashTool { Id="sloped-floor", Category="Architecture",  Name="Sloped Floor from Floor",   Subtitle="Flat floor → sloped floor / ramp",             Badge=ToolBadge.Deterministic, IconKey="ti-stairs",          Keywords="floor slope sloped ramp gradient elevation fall drainage" },
            new SlashTool { Id="floor-align",  Category="Architecture",  Name="Floor Edge Align",          Subtitle="Align & clean floor boundary edges",           Badge=ToolBadge.Deterministic, IconKey="ti-frame",           Keywords="floor edge align boundary sketch clean tidy adjacent" },
            new SlashTool { Id="col-cad",      Category="Structure",     Name="Column from CAD",           Subtitle="CAD → native structural columns",              Badge=ToolBadge.Deterministic, IconKey="ti-building",         Keywords="column cad structural grid convert native family tiang" },
            new SlashTool { Id="beam-cad",     Category="Structure",     Name="Beam from CAD",             Subtitle="CAD → structural framing + legend",            Badge=ToolBadge.AiAssisted,    IconKey="ti-ruler-2",         Keywords="beam cad structural framing legend size text layer rasuk mapping" },
            new SlashTool { Id="split-floor",  Category="Structure",     Name="Split Floor by Beams",      Subtitle="Split slab into panels by beam layout",        Badge=ToolBadge.Deterministic, IconKey="ti-layout-grid",     Keywords="split floor slab beam panel divide structural pour zone" },
            new SlashTool { Id="ff-net",       Category="MEP",           Name="FF Network from CAD",       Subtitle="Fire-protection CAD → pipes + sprinklers + legend", Badge=ToolBadge.Deterministic, IconKey="ti-flame",       Keywords="ff fire fighting sprinkler pipe network cad mep legend wet riser" },
            new SlashTool { Id="ff-pick",      Category="MEP",           Name="FF from Picked CAD",        Subtitle="Convert selected FF branch only",              Badge=ToolBadge.Deterministic, IconKey="ti-hand-finger",     Keywords="ff fire pick selected branch partial sprinkler pipe cad section" },
            new SlashTool { Id="light-cad",    Category="MEP",           Name="Lighting from CAD",         Subtitle="CAD blocks → native lighting fixtures",        Badge=ToolBadge.AiAssisted,    IconKey="ti-bulb",            Keywords="lighting light fixture cad block lamp mep convert family lampu" },
        };

        // addin-local Id → bina-ai P1 command id (app/commands/*.md). Only the
        // ids that differ are listed; the rest already match the backend.
        private static readonly Dictionary<string, string> _backendIds = new Dictionary<string, string>
        {
            ["level-vis"] = "level-visualiser",
            ["level-filter"] = "level-view-filter",
            ["level-build"] = "level-builder",
            ["cad-family"] = "cad-block-to-family",
            ["skata"] = "skata-code",
            ["lightvent"] = "light-vent-report",
            ["door-sched"] = "door-schedule",
            ["win-sched"] = "window-schedule",
            ["walls-cad"] = "walls-from-cad",
            ["walls-slab"] = "walls-from-slab-edge",
            ["floor-align"] = "floor-edge-align",
            ["col-cad"] = "column-from-cad",
            ["beam-cad"] = "beam-from-cad",
            ["split-floor"] = "split-floor-by-beams",
            ["ff-net"] = "ff-network-from-cad",
            ["ff-pick"] = "ff-from-picked-cad",
            ["light-cad"] = "lighting-from-cad",
            // batch-link, room-views, sloped-floor already match the backend
            ["create"] = "quick-create",
            ["delete"] = "quick-delete",
            ["change"] = "quick-change",
            ["rename"] = "quick-rename",
            ["count"] = "model-count",
            ["clone"] = "clone-sheet",
            ["place"] = "place-family",
            ["audit"] = "name-audit",
            // open-view is Local — never sent to the backend; BackendId falls back to Id.
        };

        static ToolCatalog()
        {
            foreach (var t in All)
                t.CommandId = _backendIds.TryGetValue(t.Id, out var bid) ? bid : t.Id;
        }

        public static SlashTool ById(string id) => All.FirstOrDefault(t => t.Id == id);

        public static string BadgeLabel(ToolBadge b) =>
            b == ToolBadge.Deterministic ? "deterministic" : b == ToolBadge.AiAssisted ? "ai-assisted" : "report";

        /// <summary>Theme resource key for the solid type colour (icon tile, tag text, legend dot).</summary>
        public static string BadgeKey(ToolBadge b) =>
            b == ToolBadge.Deterministic ? "Cp.Tool.Det" : b == ToolBadge.AiAssisted ? "Cp.Tool.Ai" : "Cp.Tool.Rep";

        /// <summary>Theme resource key for the badge tag's tinted background.</summary>
        public static string BadgeBgKey(ToolBadge b) =>
            b == ToolBadge.Deterministic ? "Cp.Tool.DetBg" : b == ToolBadge.AiAssisted ? "Cp.Tool.AiBg" : "Cp.Tool.RepBg";

        public static string SectionIconKey(string category)
        {
            switch (category)
            {
                case "Quick access": return "ti-clock-bolt";
                case "Actions": return "ti-command";
                case "General": return "ti-adjustments";
                case "Architecture": return "ti-building-arch";
                case "Structure": return "ti-building-skyscraper";
                case "MEP": return "ti-plug";
                default: return "ti-adjustments";
            }
        }

        // ── Icon geometry (Tabler paths from the design file, converted to WPF) ──
        // All are stroke-drawn (StrokeThickness ~1.9, round caps) except ti-star-filled,
        // which is filled. circle/rect/line SVG primitives were converted to path data.
        private static readonly Dictionary<string, string> IconData = new Dictionary<string, string>
        {
            ["ti-git-compare"]         = "M15,18 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M3,6 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M13,6 h3 a2,2 0 0 1 2,2 v7 M11,18 H8 a2,2 0 0 1 -2,-2 V9",
            ["ti-filter"]              = "M22,3 H2 l8,9.46 V19 l4,2 v-8.54 L22,3 z",
            ["ti-stack-2"]             = "M12.83,2.18 a2,2 0 0 0 -1.66,0 L2.6,6.08 a1,1 0 0 0 0,1.83 l8.58,3.91 a2,2 0 0 0 1.66,0 l8.58,-3.9 a1,1 0 0 0 0,-1.83 Z M22,17.65 l-9.17,4.16 a2,2 0 0 1 -1.66,0 L2,17.65 M22,12.65 l-9.17,4.16 a2,2 0 0 1 -1.66,0 L2,12.65",
            ["ti-link"]                = "M10,13 a5,5 0 0 0 7.54,0.54 l3,-3 a5,5 0 0 0 -7.07,-7.07 l-1.72,1.71 M14,11 a5,5 0 0 0 -7.54,-0.54 l-3,3 a5,5 0 0 0 7.07,7.07 l1.71,-1.71",
            ["ti-transform"]           = "M17,2 l4,4 -4,4 M3,11 v-1 a4,4 0 0 1 4,-4 h14 M7,22 l-4,-4 4,-4 M21,13 v1 a4,4 0 0 1 -4,4 H3",
            ["ti-hash"]                = "M4,9 L20,9 M4,15 L20,15 M10,3 L8,21 M16,3 L14,21",
            ["ti-sun"]                 = "M8,12 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M12,2 v2 M12,20 v2 M4.93,4.93 l1.41,1.41 M17.66,17.66 l1.41,1.41 M2,12 h2 M20,12 h2 M6.34,17.66 l-1.41,1.41 M19.07,4.93 l-1.41,1.41",
            ["ti-door"]                = "M18,20 V6 a2,2 0 0 0 -2,-2 H8 a2,2 0 0 0 -2,2 v14 M2,20 h20 M14,12 v0.01",
            ["ti-window"]              = "M5,3 h14 a2,2 0 0 1 2,2 v14 a2,2 0 0 1 -2,2 h-14 a2,2 0 0 1 -2,-2 v-14 a2,2 0 0 1 2,-2 z M3,12 h18 M12,3 v18",
            ["ti-box-multiple"]        = "M10,8 h8 a2,2 0 0 1 2,2 v8 a2,2 0 0 1 -2,2 h-8 a2,2 0 0 1 -2,-2 v-8 a2,2 0 0 1 2,-2 z M4,16 V4 a2,2 0 0 1 2,-2 h10",
            ["ti-wall"]                = "M4,4 h16 a1,1 0 0 1 1,1 v14 a1,1 0 0 1 -1,1 h-16 a1,1 0 0 1 -1,-1 v-14 a1,1 0 0 1 1,-1 z M3,9 h18 M3,14 h18 M9,4 v5 M15,9 v5 M9,14 v6 M15,14 v6",
            ["ti-border-corners"]      = "M4,8 V4 h4 M20,8 V4 h-4 M4,16 v4 h4 M20,16 v4 h-4",
            ["ti-stairs"]              = "M3,20 v-4 h4 v-4 h4 v-4 h4 v-4 h5",
            ["ti-frame"]               = "M22,6 L2,6 M22,18 L2,18 M6,2 L6,22 M18,2 L18,22",
            ["ti-building"]            = "M6,2 h12 a2,2 0 0 1 2,2 v16 a2,2 0 0 1 -2,2 h-12 a2,2 0 0 1 -2,-2 v-16 a2,2 0 0 1 2,-2 z M9,22 v-4 h6 v4 M8,6 h0.01 M16,6 h0.01 M8,10 h0.01 M16,10 h0.01 M8,14 h0.01 M16,14 h0.01",
            ["ti-ruler-2"]             = "M21.3,15.3 a2.4,2.4 0 0 1 0,3.4 l-2.6,2.6 a2.4,2.4 0 0 1 -3.4,0 L2.7,8.7 a2.41,2.41 0 0 1 0,-3.4 l2.6,-2.6 a2.41,2.41 0 0 1 3.4,0 Z M14.5,12.5 l2,-2 M11.5,9.5 l2,-2 M8.5,6.5 l2,-2 M17.5,15.5 l2,-2",
            ["ti-layout-grid"]         = "M4,3 h5 a1,1 0 0 1 1,1 v5 a1,1 0 0 1 -1,1 h-5 a1,1 0 0 1 -1,-1 v-5 a1,1 0 0 1 1,-1 z M15,3 h5 a1,1 0 0 1 1,1 v5 a1,1 0 0 1 -1,1 h-5 a1,1 0 0 1 -1,-1 v-5 a1,1 0 0 1 1,-1 z M15,14 h5 a1,1 0 0 1 1,1 v5 a1,1 0 0 1 -1,1 h-5 a1,1 0 0 1 -1,-1 v-5 a1,1 0 0 1 1,-1 z M4,14 h5 a1,1 0 0 1 1,1 v5 a1,1 0 0 1 -1,1 h-5 a1,1 0 0 1 -1,-1 v-5 a1,1 0 0 1 1,-1 z",
            ["ti-flame"]               = "M8.5,14.5 a2.5,2.5 0 0 0 2.5,-2.5 c0,-1.38 -0.5,-2 -1,-3 -1.072,-2.143 -0.224,-4.054 2,-6 0.5,2.5 2,4.9 4,6.5 2,1.6 3,3.5 3,5.5 a7,7 0 1 1 -14,0 c0,-1.153 0.433,-2.294 1,-3 a2.5,2.5 0 0 0 2.5,2.5 z",
            ["ti-hand-finger"]         = "M9,11 l5,12 1.8,-5.2 L21,15 Z M7.2,2.2 L8,5.1 M5.1,8 L2.2,7.2 M14,4.1 L12,6 M6,12 l-1.9,2",
            ["ti-bulb"]                = "M15,14 c0.2,-1 0.7,-1.7 1.5,-2.5 1,-0.9 1.5,-2.2 1.5,-3.5 A6,6 0 0 0 6,8 c0,1 0.2,2.2 1.5,3.5 0.7,0.7 1.3,1.5 1.5,2.5 M9,18 h6 M10,22 h4",
            ["ti-clock-bolt"]          = "M3,12 a9,9 0 1 0 18,0 a9,9 0 1 0 -18,0 M12,7 v5 l3,2",
            ["ti-adjustments"]         = "M4,21 v-7 M4,10 v-7 M12,21 v-9 M12,8 v-5 M20,21 v-5 M20,12 v-9 M2,14 h4 M10,8 h4 M18,16 h4",
            ["ti-building-arch"]       = "M6,2 h12 a2,2 0 0 1 2,2 v16 a2,2 0 0 1 -2,2 h-12 a2,2 0 0 1 -2,-2 v-16 a2,2 0 0 1 2,-2 z M9,22 v-4 h6 v4 M8,6 h0.01 M16,6 h0.01 M8,10 h0.01 M16,10 h0.01 M8,14 h0.01 M16,14 h0.01",
            ["ti-building-skyscraper"] = "M3,21 h18 M5,21 V5 l7,-3 v19 M19,21 V9 l-7,-4 M9,9 h0.01 M9,13 h0.01 M9,17 h0.01",
            ["ti-plug"]                = "M12,22 v-5 M9,8 V2 M15,8 V2 M18,8 v3 a4,4 0 0 1 -4,4 h-4 a4,4 0 0 1 -4,-4 V8 Z",
            ["ti-command"]             = "M18,3 a3,3 0 0 0 -3,3 v12 a3,3 0 1 0 3,-3 H6 a3,3 0 1 0 3,3 V6 a3,3 0 1 0 -3,3 h12 a3,3 0 1 0 -3,-3 Z",
            ["ti-star"]                = "M12,2.5 l2.9,6.1 6.6,0.6 -5,4.4 1.5,6.5 L12,17.3 6,20.6 l1.5,-6.5 -5,-4.4 6.6,-0.6 z",
            ["ti-star-filled"]         = "M12,2.5 l2.9,6.1 6.6,0.6 -5,4.4 1.5,6.5 L12,17.3 6,20.6 l1.5,-6.5 -5,-4.4 6.6,-0.6 z",
            ["ti-search-off"]          = "M3,11 a8,8 0 1 0 16,0 a8,8 0 1 0 -16,0 M21,21 l-4.3,-4.3 M8,8 l6,6",
            ["ti-tag"]                 = "M9,5 H5 a2,2 0 0 0 -2,2 v4 l9,9 a1.5,1.5 0 0 0 2.1,0 l4,-4 a1.5,1.5 0 0 0 0,-2.1 L9,5 z M7.5,8.5 h0.01",
            ["ti-plus"]                = "M12,5 v14 M5,12 h14",
            ["ti-trash"]               = "M4,7 h16 M10,11 v6 M14,11 v6 M5,7 l1,12 a2,2 0 0 0 2,2 h8 a2,2 0 0 0 2,-2 l1,-12 M9,7 V4 a1,1 0 0 1 1,-1 h4 a1,1 0 0 1 1,1 v3",
            ["ti-exchange"]            = "M21,7 H3 M18,10 l3,-3 -3,-3 M3,17 h18 M6,20 l-3,-3 3,-3",
            ["ti-pencil"]              = "M4,20 h4 L18.5,9.5 a2.828,2.828 0 1 0 -4,-4 L4,16 v4 M13.5,6.5 l4,4",
            ["ti-eye"]                 = "M10,12 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M21,12 c-2.4,4 -5.4,6 -9,6 c-3.6,0 -6.6,-2 -9,-6 c2.4,-4 5.4,-6 9,-6 c3.6,0 6.6,2 9,6",
            ["ti-sum"]                 = "M18,16 v2 a1,1 0 0 1 -1,1 H6 l6,-7 -6,-7 h11 a1,1 0 0 1 1,1 v2",
            ["ti-copy"]                = "M10,8 h8 a2,2 0 0 1 2,2 v8 a2,2 0 0 1 -2,2 h-8 a2,2 0 0 1 -2,-2 v-8 a2,2 0 0 1 2,-2 z M16,8 V6 a2,2 0 0 0 -2,-2 H6 a2,2 0 0 0 -2,2 v8 a2,2 0 0 0 2,2 h2",
            ["ti-map-pin"]             = "M9,11 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M17.657,16.657 L13.414,20.9 a2,2 0 0 1 -2.827,0 l-4.244,-4.243 a8,8 0 1 1 11.314,0 z",
            ["ti-clipboard-check"]     = "M9,5 H7 a2,2 0 0 0 -2,2 v12 a2,2 0 0 0 2,2 h10 a2,2 0 0 0 2,-2 V7 a2,2 0 0 0 -2,-2 h-2 M9,3 h6 a1,1 0 0 1 1,1 v1 a1,1 0 0 1 -1,1 H9 a1,1 0 0 1 -1,-1 V4 a1,1 0 0 1 1,-1 z M9,14 l2,2 4,-4",
        };

        private static readonly Dictionary<string, Geometry> _geo = new Dictionary<string, Geometry>();

        /// <summary>Frozen icon geometry (24×24 viewbox) for a ti-* key.</summary>
        public static Geometry Icon(string key)
        {
            if (_geo.TryGetValue(key, out var g)) return g;
            var data = IconData.TryGetValue(key, out var s) ? s : IconData["ti-command"];
            var geo = Geometry.Parse(data);
            geo.Freeze();
            _geo[key] = geo;
            return geo;
        }

        public static bool IconFilled(string key) => key == "ti-star-filled";
    }
}
