#nullable enable
// Revit-bound half of "why can't I see it?": reads one view's settings for
// each element into VisibilityFacts (plain values). VisibilityReasons
// (Revit-free) orders the reasons and writes the EN/BM fixes. Read-only: no
// transaction, nothing changes.
//
// Everything that belongs to the view (template control, filters, phase and
// phase filter, levels and view-range heights, crop / section box) is read
// once per view in ViewInfo (review M8), then each element is checked against it.
//
// Checks, in the order they are reported: category (and "show model
// categories"), Hide in View, Temporary Hide/Isolate, view filters, view
// template control, workset, phase, design option, crop / section box / view
// range, element on another level. Linked elements: the link instance and the
// host view's category settings are checked; filters and phases of the link
// are not (noted).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    internal static class VisibilityExplainer
    {
        /// <summary>A view's settings, read once.</summary>
        internal sealed class ViewInfo
        {
            public Document Doc = null!;
            public View View = null!;
            public string? TemplateName;
            public bool TemplateCategories, TemplateFilters, TemplateWorksets, TemplatePhase, TemplateRange;
            public bool TempMode;
            public List<(string Name, ElementFilter? Rule, ICollection<ElementId>? Categories, ICollection<ElementId>? Ids)> HidingFilters = new();
            public ElementId PhaseId = ElementId.InvalidElementId;
            public string PhaseName = "?";
            public PhaseFilter? PhaseFilter;
            public ElementId ActiveOption = ElementId.InvalidElementId;
            public BoundingBoxXYZ? Crop;
            public bool SectionBox;
            public string? GenLevel;
            public bool IsPlan, IsCeiling;
            public double? CutZ, DepthZ, TopZ;
            public double CutOffset, DepthOffset;
            public Dictionary<int, string?> HiddenWorksets = new();   // workset id -> name when hidden
            public WorksetDefaultVisibilitySettings? WorksetDefaults;
        }

        public static ViewInfo ForView(Document doc, View view)
        {
            var v = new ViewInfo { Doc = doc, View = view };
            Try(() => Template(doc, view, v));
            Try(() => v.TempMode = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate));
            Try(() =>
            {
                foreach (var fid in view.GetFilters())
                {
                    try
                    {
                        if (view.GetFilterVisibility(fid) || !view.GetIsFilterEnabled(fid)) continue;
                        switch (doc.GetElement(fid))
                        {
                            case ParameterFilterElement pfe:
                                v.HidingFilters.Add((pfe.Name, pfe.GetElementFilter(), pfe.GetCategories(), null));
                                break;
                            case SelectionFilterElement sfe:
                                v.HidingFilters.Add((sfe.Name, null, null, sfe.GetElementIds()));
                                break;
                        }
                    }
                    catch { }
                }
            });
            Try(() =>
            {
                var pid = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                if (pid != null && pid != ElementId.InvalidElementId)
                {
                    v.PhaseId = pid;
                    v.PhaseName = doc.GetElement(pid)?.Name ?? "?";
                }
                var fid = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId();
                if (fid != null && fid != ElementId.InvalidElementId) v.PhaseFilter = doc.GetElement(fid) as PhaseFilter;
            });
            Try(() => v.ActiveOption = DesignOption.GetActiveDesignOptionId(doc) ?? ElementId.InvalidElementId);
            Try(() =>
            {
                if (view is View3D v3 && v3.IsSectionBoxActive) { v.SectionBox = true; v.Crop = v3.GetSectionBox(); }
                else if (view.CropBoxActive) v.Crop = view.CropBox;
            });
            Try(() => { if (doc.IsWorkshared) v.WorksetDefaults = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc); });
            Try(() => Range(doc, view, v));
            return v;
        }

        public static VisibilityFacts Facts(ViewInfo v, Element el, RevitLinkInstance? link, string? linkName)
        {
            var view = v.View;
            var f = new VisibilityFacts
            {
                Ref = new ElementRef(el.Id.Value, link?.Id.Value),
                Category = el.Category?.Name,
                LinkName = linkName,
                TemplateName = v.TemplateName,
                TemplateControlsCategories = v.TemplateCategories,
                TemplateControlsFilters = v.TemplateFilters,
                TemplateControlsWorksets = v.TemplateWorksets,
                TemplateControlsPhase = v.TemplatePhase,
                TemplateControlsRange = v.TemplateRange,
            };

            Try(() => Category(view, el, f));
            Try(() =>
            {
                if (link == null) f.ElementHidden = el.IsHidden(view);
                else f.LinkInstanceHidden = link.IsHidden(view);
            });
            Try(() =>
            {
                if (v.TempMode)
                    f.TemporaryHidden = !view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, (link ?? el).Id);
            });
            if (link == null)
            {
                Try(() => Filters(v, el, f));
                Try(() => Workset(v, el, f));
                Try(() => Phase(v, el, f));
                Try(() => Option(v, el, f));
                Try(() =>
                {
                    if (el.OwnerViewId != ElementId.InvalidElementId && el.OwnerViewId != view.Id)
                        f.Notes.Add("It is a view-specific element drawn in view '" + (v.Doc.GetElement(el.OwnerViewId)?.Name ?? "?") + "' only.");
                });
            }
            else
            {
                Try(() => Workset(v, link, f));
                f.Notes.Add("Linked element: also check Visibility/Graphics > Revit Links (By Host View / By Linked View / Custom); the link's own filters and phases are not checked.");
            }

            Try(() => Extent(v, el, link, f));
            Try(() => f.CollectorVisible = CollectorSees(v, el, link, f));
            return f;
        }

        private static void Try(Action a)
        {
            try { a(); } catch { /* one unreadable setting must not hide the others */ }
        }

        private static void Template(Document doc, View view, ViewInfo v)
        {
            var tid = view.ViewTemplateId;
            if (tid == null || tid == ElementId.InvalidElementId) return;
            if (doc.GetElement(tid) is not View template) return;
            v.TemplateName = template.Name;
            var free = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(i => (long)i.Value));
            bool Controls(BuiltInParameter bip) => !free.Contains((long)new ElementId(bip).Value);
            v.TemplateCategories = Controls(BuiltInParameter.VIS_GRAPHICS_MODEL);
            v.TemplateFilters = Controls(BuiltInParameter.VIS_GRAPHICS_FILTERS);
            v.TemplateWorksets = Controls(BuiltInParameter.VIS_GRAPHICS_WORKSETS);
            v.TemplatePhase = Controls(BuiltInParameter.VIEW_PHASE_FILTER) || Controls(BuiltInParameter.VIEW_PHASE);
            v.TemplateRange = Controls(BuiltInParameter.PLAN_VIEW_RANGE);
        }

        private static void Category(View view, Element el, VisibilityFacts f)
        {
            var cat = el.Category;
            if (cat == null) return;
            bool hidden = false;
            try { hidden = view.GetCategoryHidden(cat.Id); } catch { }
            if (!hidden && cat.Parent != null)
                try { hidden = view.GetCategoryHidden(cat.Parent.Id); } catch { }
            f.CategoryHidden = hidden;
            if (cat.CategoryType == CategoryType.Model) f.ModelCategoriesHidden = view.AreModelCategoriesHidden;
            else if (cat.CategoryType == CategoryType.Annotation) f.ModelCategoriesHidden = view.AreAnnotationCategoriesHidden;
        }

        private static void Filters(ViewInfo v, Element el, VisibilityFacts f)
        {
            var catId = el.Category?.Id;
            foreach (var (name, rule, categories, ids) in v.HidingFilters)
            {
                try
                {
                    bool applies = ids != null
                        ? ids.Contains(el.Id)
                        : catId != null && categories != null && categories.Contains(catId) && (rule?.PassesFilter(el) ?? true);
                    if (applies) f.HidingFilters.Add(name);
                }
                catch { }
            }
        }

        private static void Workset(ViewInfo v, Element el, VisibilityFacts f)
        {
            if (!v.Doc.IsWorkshared) return;
            var wid = el.WorksetId;
            if (wid == null || wid == WorksetId.InvalidWorksetId) return;
            if (!v.HiddenWorksets.TryGetValue(wid.IntegerValue, out var name))
            {
                var vis = v.View.GetWorksetVisibility(wid);
                bool hidden = vis == WorksetVisibility.Hidden
                    || (vis == WorksetVisibility.UseGlobalSetting && v.WorksetDefaults != null && !v.WorksetDefaults.IsWorksetVisible(wid));
                name = hidden ? v.Doc.GetWorksetTable().GetWorkset(wid)?.Name ?? "workset " + wid.IntegerValue : null;
                v.HiddenWorksets[wid.IntegerValue] = name;
            }
            if (name != null) f.WorksetHidden = name;
        }

        private static void Phase(ViewInfo v, Element el, VisibilityFacts f)
        {
            if (v.PhaseId == ElementId.InvalidElementId || !el.HasPhases()) return;
            var status = el.GetPhaseStatus(v.PhaseId);
            if (status == ElementOnPhaseStatus.Future)
            {
                f.PhaseHidden = "created in a later phase than the view's phase '" + v.PhaseName + "'";
                return;
            }
            if (status == ElementOnPhaseStatus.Past)
            {
                f.PhaseHidden = "demolished in an earlier phase than the view's phase '" + v.PhaseName + "'";
                return;
            }
            if (v.PhaseFilter != null && v.PhaseFilter.GetPhaseStatusPresentation(status) == PhaseStatusPresentation.DontShow)
                f.PhaseHidden = "status '" + status + "' in phase '" + v.PhaseName + "' is not shown by phase filter '" + v.PhaseFilter.Name + "'";
        }

        private static void Option(ViewInfo v, Element el, VisibilityFacts f)
        {
            var opt = el.DesignOption;
            if (opt == null || opt.IsPrimary || opt.Id == v.ActiveOption) return;
            var setId = opt.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
            var set = setId != null && setId != ElementId.InvalidElementId ? v.Doc.GetElement(setId)?.Name : null;
            f.DesignOptionHidden = "'" + opt.Name + "' (secondary" + (set != null ? ", set '" + set + "'" : "") + ")";
        }

        private static void Range(Document doc, View view, ViewInfo v)
        {
            if (view is not ViewPlan plan) return;
            v.IsPlan = true;
            v.IsCeiling = plan.ViewType == ViewType.CeilingPlan;
            var gen = plan.GenLevel;
            v.GenLevel = gen?.Name;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            var range = plan.GetViewRange();

            double? Z(PlanViewPlane p, bool low)
            {
                var lid = range.GetLevelId(p);
                var off = range.GetOffset(p);
                Level? lvl;
                if (lid == PlanViewRange.Unlimited) return low ? double.NegativeInfinity : double.PositiveInfinity;
                if (lid == PlanViewRange.Current) lvl = gen;
                else if (lid == PlanViewRange.LevelBelow || lid == PlanViewRange.LevelAbove)
                {
                    var i = gen == null ? -1 : levels.FindIndex(l => l.Id == gen.Id);
                    var j = lid == PlanViewRange.LevelBelow ? i - 1 : i + 1;
                    if (i < 0 || j < 0 || j >= levels.Count) return low ? double.NegativeInfinity : double.PositiveInfinity;
                    lvl = levels[j];
                }
                else lvl = doc.GetElement(lid) as Level;
                return lvl == null ? (double?)null : lvl.ProjectElevation + off;
            }

            v.CutZ = Z(PlanViewPlane.CutPlane, low: false);
            v.DepthZ = Z(PlanViewPlane.ViewDepthPlane, low: true);
            v.TopZ = Z(PlanViewPlane.TopClipPlane, low: false);
            v.CutOffset = range.GetOffset(PlanViewPlane.CutPlane);
            v.DepthOffset = range.GetOffset(PlanViewPlane.ViewDepthPlane);
        }

        private static (XYZ Min, XYZ Max)? ModelBox(Element el, RevitLinkInstance? link)
        {
            var bb = el.get_BoundingBox(null);
            if (bb == null) return null;
            var tf = link?.GetTotalTransform();
            XYZ? min = null, max = null;
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
            {
                var p = new XYZ(x, y, z);
                if (bb.Transform != null && !bb.Transform.IsIdentity) p = bb.Transform.OfPoint(p);
                if (tf != null) p = tf.OfPoint(p);
                min = min == null ? p : new XYZ(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = max == null ? p : new XYZ(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
            return (min!, max!);
        }

        private static CropCheck Against(BoundingBoxXYZ box, XYZ min, XYZ max, bool threeD)
        {
            var inv = box.Transform.Inverse;
            XYZ? lo = null, hi = null;
            foreach (var x in new[] { min.X, max.X })
            foreach (var y in new[] { min.Y, max.Y })
            foreach (var z in new[] { min.Z, max.Z })
            {
                var p = inv.OfPoint(new XYZ(x, y, z));
                lo = lo == null ? p : new XYZ(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                hi = hi == null ? p : new XYZ(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
            }
            var xy = ViewExtent.Crop(lo!.X, lo.Y, hi!.X, hi.Y, box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
            if (!threeD || xy == CropCheck.Outside) return xy;
            var z3 = ViewExtent.Crop(lo.Z, 0, hi.Z, 0, box.Min.Z, 0, box.Max.Z, 0);
            return z3 == CropCheck.Outside ? CropCheck.Outside : xy == CropCheck.Inside && z3 == CropCheck.Inside ? CropCheck.Inside : CropCheck.Partial;
        }

        private static void Extent(ViewInfo v, Element el, RevitLinkInstance? link, VisibilityFacts f)
        {
            var box = ModelBox(el, link);
            if (box == null) return;
            var (min, max) = box.Value;
            if (v.Crop != null)
            {
                f.SectionBox = v.SectionBox;
                f.Crop = Against(v.Crop, min, max, threeD: v.SectionBox);
            }
            if (!v.IsPlan) return;

            if (v.IsCeiling)
            {
                if (v.CutZ.HasValue && v.TopZ.HasValue)
                {
                    f.Range = ViewExtent.CeilingRange(min.Z, max.Z, v.CutZ.Value, v.TopZ.Value);
                    if (f.Range == RangeCheck.Below) f.RangeDetail = "below the cut plane of this ceiling plan";
                    if (f.Range == RangeCheck.AboveTop) f.RangeDetail = "above the top of the view range";
                }
            }
            else if (v.CutZ.HasValue && v.DepthZ.HasValue)
            {
                f.Range = ViewExtent.PlanRange(min.Z, max.Z, v.DepthZ.Value, v.CutZ.Value);
                if (f.Range == RangeCheck.Below)
                    f.RangeDetail = "entirely below the View Depth (" + RevitValues.FormatLength(v.Doc, v.DepthOffset) + " from its level)";
                if (f.Range == RangeCheck.AboveCut)
                    f.RangeDetail = "entirely above the Cut Plane (" + RevitValues.FormatLength(v.Doc, v.CutOffset) + " above " + (v.GenLevel ?? "the level") + "); most categories are not drawn above it";
            }

            if (v.GenLevel != null)
            {
                var elLevel = RevitValues.LevelName(el.Document, el);
                if (elLevel != null && !string.Equals(elLevel, v.GenLevel, StringComparison.OrdinalIgnoreCase))
                {
                    f.OtherLevel = elLevel;
                    f.ViewLevel = v.GenLevel;
                }
            }
        }

        private static bool CollectorSees(ViewInfo v, Element el, RevitLinkInstance? link, VisibilityFacts f)
        {
            if (link == null)
                return new FilteredElementCollector(v.Doc, v.View.Id).WherePasses(new ElementIdSetFilter(new List<ElementId> { el.Id })).GetElementCount() > 0;
#if REVIT2023_24
            f.Notes.Add("On Revit 2023/2024 the add-in cannot ask Revit whether a linked element is drawn; only the settings above were checked.");
            return true;
#else
            return new FilteredElementCollector(v.Doc, v.View.Id, link.Id).WherePasses(new ElementIdSetFilter(new List<ElementId> { el.Id })).GetElementCount() > 0;
#endif
        }
    }
}
