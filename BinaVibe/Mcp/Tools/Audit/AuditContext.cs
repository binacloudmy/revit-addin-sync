// AuditContext — one per fill_audit call: the live Document plus every model
// inventory the checkers read, collected once and memoised.
//
// Two reasons it exists. (1) Consistency: two rows that talk about the same
// thing must cite the same numbers. base_point and grids_levels used to collect
// grids independently, so one row could say "Grid A/1 tiada" while the next
// said "25 grid" — both true, read as a contradiction. Shared evidence makes
// that impossible. (2) Cost: GraphicalViews() was re-collected by six checkers
// per run.
//
// Read-only throughout — no Transactions. Every list is capped where a broken
// model could otherwise produce tens of thousands of entries.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace BinaVibe.Mcp.Tools.Audit
{
    /// <summary>One grid as the audit sees it: its printed name and whether its
    /// curve is a straight line (only straight grids can be intersected for the
    /// base-point check).</summary>
    public sealed class GridInfo
    {
        public long Id;
        public string Name = "";
        public bool IsLine;
        public Curve? Curve;
    }

    public sealed class AuditContext
    {
        public AuditContext(Document doc) => Doc = doc;

        public Document Doc { get; }

        private List<GridInfo>? _grids;
        public List<GridInfo> Grids => _grids ??= new FilteredElementCollector(Doc)
            .OfCategory(BuiltInCategory.OST_Grids).WhereElementIsNotElementType()
            .Cast<Grid>()
            .Select(g =>
            {
                string name; Curve? curve = null;
                try { name = g.Name ?? ""; } catch { name = ""; }
                try { curve = g.Curve; } catch { /* multi-segment grid */ }
                return new GridInfo { Id = (long)g.Id.Value, Name = name, IsLine = curve is Line, Curve = curve };
            })
            .ToList();

        private List<Level>? _levels;
        public List<Level> Levels => _levels ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(Level)).Cast<Level>().ToList();

        private List<ViewPlan>? _floorPlans;
        public List<ViewPlan> FloorPlans => _floorPlans ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan).ToList();

        private List<ViewPlan>? _areaPlans;
        public List<ViewPlan> AreaPlans => _areaPlans ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.AreaPlan).ToList();

        /// <summary>Printable non-template views, excluding schedules and sheets —
        /// "views" as the audit form means it.</summary>
        private List<View>? _graphicalViews;
        public List<View> GraphicalViews => _graphicalViews ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.CanBePrinted
                        && v.ViewType != ViewType.Schedule
                        && v.ViewType != ViewType.DrawingSheet)
            .ToList();

        /// <summary>Every View Template in the model grouped by the ViewType it
        /// applies to. A "no template" finding is only actionable when this has
        /// an entry for the view's type — see AuditTemplateAvailability.</summary>
        private Dictionary<ViewType, List<View>>? _templatesByViewType;
        public Dictionary<ViewType, List<View>> TemplatesByViewType => _templatesByViewType ??=
            new FilteredElementCollector(Doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate)
                .GroupBy(v => v.ViewType)
                .ToDictionary(g => g.Key, g => g.ToList());

        public int TemplateCount => TemplatesByViewType.Values.Sum(l => l.Count);

        /// <summary>ViewType names (enum ToString) that have ≥1 template — the
        /// Revit-free shape AuditTemplateAvailability.Split consumes.</summary>
        private HashSet<string>? _viewTypesWithTemplates;
        public HashSet<string> ViewTypesWithTemplates => _viewTypesWithTemplates ??=
            new HashSet<string>(TemplatesByViewType.Keys.Select(k => k.ToString()));

        public bool TemplateExistsFor(ViewType type) => TemplatesByViewType.ContainsKey(type);

        /// <summary>Project the views lacking a template into the Revit-free
        /// partition input.</summary>
        public List<UntemplatedView> UntemplatedOf(IEnumerable<View> views) => views
            .Where(v => v.ViewTemplateId == ElementId.InvalidElementId)
            .Select(v => new UntemplatedView
            {
                Id = (long)v.Id.Value,
                Name = v.Name ?? "",
                ViewType = v.ViewType.ToString(),
            })
            .ToList();

        private List<View>? _legends;
        public List<View> Legends => _legends ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend).ToList();

        private List<ViewSchedule>? _schedules;
        public List<ViewSchedule> Schedules => _schedules ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(v => !v.IsTemplate).ToList();

        private List<ViewSheet>? _sheets;
        public List<ViewSheet> Sheets => _sheets ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();

        private List<FamilyInstance>? _titleBlocks;
        public List<FamilyInstance> TitleBlocks => _titleBlocks ??= new FilteredElementCollector(Doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
            .Cast<FamilyInstance>().ToList();

        private List<RevitLinkType>? _linkTypes;
        public List<RevitLinkType> LinkTypes => _linkTypes ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();

        private List<Room>? _rooms;
        /// <summary>Placed rooms only (Area > 0) — an unplaced room has no
        /// geometry to audit.</summary>
        public List<Room> Rooms => _rooms ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(SpatialElement)).Cast<SpatialElement>().OfType<Room>()
            .Where(r => { try { return r.Area > 0; } catch { return false; } }).ToList();

        private XYZ? _basePoint;
        private bool _basePointRead;
        /// <summary>Project Base Point position, or null when absent.</summary>
        public XYZ? ProjectBasePoint
        {
            get
            {
                if (_basePointRead) return _basePoint;
                _basePointRead = true;
                foreach (BasePoint bp in new FilteredElementCollector(Doc).OfClass(typeof(BasePoint)))
                    if (!bp.IsShared) _basePoint = bp.Position;
                return _basePoint;
            }
        }

        public GridInfo? GridNamed(string name) =>
            Grids.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // ─── inventories for unmatched-row context (item 9) ─────────────

        private List<string>? _worksets;
        public List<string> Worksets => _worksets ??= ReadWorksets();

        private List<string> ReadWorksets()
        {
            var names = new List<string>();
            try
            {
                if (!Doc.IsWorkshared) return names;
                foreach (var ws in new FilteredWorksetCollector(Doc).OfKind(WorksetKind.UserWorkset))
                    names.Add(ws.Name ?? "");
            }
            catch { /* not workshared, or worksets unavailable in this context */ }
            return names;
        }

        private List<string>? _phases;
        public List<string> Phases => _phases ??= ReadPhases();

        private List<string> ReadPhases()
        {
            var names = new List<string>();
            try
            {
                foreach (Phase ph in Doc.Phases) names.Add(ph.Name ?? "");
            }
            catch { /* no phases */ }
            return names;
        }

        private List<string>? _designOptions;
        public List<string> DesignOptions => _designOptions ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(DesignOption)).Cast<DesignOption>()
            .Select(d => d.Name ?? "").ToList();

        private List<string>? _scopeBoxes;
        public List<string> ScopeBoxes => _scopeBoxes ??= new FilteredElementCollector(Doc)
            .OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
            .Select(e => e.Name ?? "").ToList();

        private List<string>? _families;
        public List<string> Families => _families ??= new FilteredElementCollector(Doc)
            .OfClass(typeof(Family)).Cast<Family>().Select(f => f.Name ?? "").ToList();

        private List<string>? _sharedParameters;
        public List<string> SharedParameters => _sharedParameters ??= ReadSharedParameters();

        private List<string> ReadSharedParameters()
        {
            var names = new List<string>();
            try
            {
                var it = Doc.ParameterBindings.ForwardIterator();
                while (it.MoveNext())
                    if (it.Key is Definition def) names.Add(def.Name ?? "");
            }
            catch { /* no bindings */ }
            return names;
        }

        private int? _warnings;
        public int Warnings
        {
            get
            {
                if (_warnings == null)
                {
                    try { _warnings = Doc.GetWarnings().Count; } catch { _warnings = 0; }
                }
                return _warnings.Value;
            }
        }
    }
}
