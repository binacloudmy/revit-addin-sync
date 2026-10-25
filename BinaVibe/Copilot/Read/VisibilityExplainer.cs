#nullable enable
// Revit-bound half of "why can't I see it?": reads one view's settings for
// each element into VisibilityFacts (plain values). VisibilityReasons
// (Revit-free) orders the reasons and writes the EN/BM fixes. Read-only: no
// transaction, nothing changes.
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
        public static VisibilityFacts Facts(Document hostDoc, View view, Element el, RevitLinkInstance? link, string? linkName)
        {
            var doc = el.Document;
            var f = new VisibilityFacts
            {
                Ref = new ElementRef(el.Id.Value, link?.Id.Value),
                Category = el.Category?.Name,
                LinkName = linkName,
            };

            Template(hostDoc, view, f);
            Try(() => Category(view, el, f));
            Try(() =>
            {
                if (link == null) f.ElementHidden = el.IsHidden(view);
                else f.LinkInstanceHidden = link.IsHidden(view);
            });
            Try(() =>
            {
                if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                    f.TemporaryHidden = !view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, (link ?? el).Id);
            });
            if (link == null)
            {
                Try(() => Filters(hostDoc, view, el, f));
                Try(() => Workset(hostDoc, view, el, f));
                Try(() => Phase(hostDoc, view, el, f));
                Try(() => Option(hostDoc, el, f));
                Try(() =>
                {
                    if (el.OwnerViewId != ElementId.InvalidElementId && el.OwnerViewId != view.Id)
                        f.Notes.Add("It is a view-specific element drawn in view '" + (hostDoc.GetElement(el.OwnerViewId)?.Name ?? "?") + "' only.");
                });
            }
            else
            {
                Try(() => Workset(hostDoc, view, link, f));
                f.Notes.Add("Linked element: also check Visibility/Graphics > Revit Links (By Host View / By Linked View / Custom); the link's own filters and phases are not checked.");
            }

            Try(() => Extent(hostDoc, view, el, link, f));
            Try(() => f.CollectorVisible = CollectorSees(hostDoc, view, el, link, f));
            return f;
        }

        private static void Try(Action a)
        {
            try { a(); } catch { /* one unreadable setting must not hide the others */ }
        }

        private static void Template(Document doc, View view, VisibilityFacts f)
        {
            try
            {
                var tid = view.ViewTemplateId;
                if (tid == null || tid == ElementId.InvalidElementId) return;
                if (doc.GetElement(tid) is not View template) return;
                f.TemplateName = template.Name;
                var free = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(i => (long)i.Value));
                bool Controls(BuiltInParameter bip) => !free.Contains((long)new ElementId(bip).Value);
                f.TemplateControlsCategories = Controls(BuiltInParameter.VIS_GRAPHICS_MODEL);
                f.TemplateControlsFilters = Controls(BuiltInParameter.VIS_GRAPHICS_FILTERS);
                f.TemplateControlsWorksets = Controls(BuiltInParameter.VIS_GRAPHICS_WORKSETS);
                f.TemplateControlsPhase = Controls(BuiltInParameter.VIEW_PHASE_FILTER) || Controls(BuiltInParameter.VIEW_PHASE);
                f.TemplateControlsRange = Controls(BuiltInParameter.PLAN_VIEW_RANGE);
            }
            catch { }
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

        private static void Filters(Document doc, View view, Element el, VisibilityFacts f)
        {
            var catId = el.Category?.Id;
            foreach (var fid in view.GetFilters())
            {
                try
                {
                    if (view.GetFilterVisibility(fid)) continue;
                    if (!view.GetIsFilterEnabled(fid)) continue;
                    var fe = doc.GetElement(fid);
                    bool applies = fe switch
                    {
                        ParameterFilterElement pfe => catId != null && pfe.GetCategories().Contains(catId)
                                                     && (pfe.GetElementFilter()?.PassesFilter(el) ?? true),
                        SelectionFilterElement sfe => sfe.GetElementIds().Contains(el.Id),
                        _ => false,
                    };
                    if (applies) f.HidingFilters.Add(fe!.Name);
                }
                catch { }
            }
        }

        private static void Workset(Document doc, View view, Element el, VisibilityFacts f)
        {
            if (!doc.IsWorkshared) return;
            var wid = el.WorksetId;
            if (wid == null || wid == WorksetId.InvalidWorksetId) return;
            var vis = view.GetWorksetVisibility(wid);
            bool hidden = vis == WorksetVisibility.Hidden
                || (vis == WorksetVisibility.UseGlobalSetting
                    && !WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc).IsWorksetVisible(wid));
            if (hidden) f.WorksetHidden = doc.GetWorksetTable().GetWorkset(wid)?.Name ?? "workset " + wid.IntegerValue;
        }

        private static void Phase(Document doc, View view, Element el, VisibilityFacts f)
        {
            if (!el.HasPhases()) return;
            var phaseId = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
            if (phaseId == null || phaseId == ElementId.InvalidElementId) return;
            var status = el.GetPhaseStatus(phaseId);
            var phaseName = doc.GetElement(phaseId)?.Name ?? "?";
            if (status == ElementOnPhaseStatus.Future)
            {
                f.PhaseHidden = "created in a later phase than the view's phase '" + phaseName + "'";
                return;
            }
            if (status == ElementOnPhaseStatus.Past)
            {
                f.PhaseHidden = "demolished in an earlier phase than the view's phase '" + phaseName + "'";
                return;
            }
            var filterId = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId();
            if (filterId == null || filterId == ElementId.InvalidElementId) return;
            if (doc.GetElement(filterId) is not PhaseFilter pf) return;
            if (pf.GetPhaseStatusPresentation(status) == PhaseStatusPresentation.DontShow)
                f.PhaseHidden = "status '" + status + "' in phase '" + phaseName + "' is not shown by phase filter '" + pf.Name + "'";
        }

        private static void Option(Document doc, Element el, VisibilityFacts f)
        {
            var opt = el.DesignOption;
            if (opt == null || opt.IsPrimary) return;
            var active = DesignOption.GetActiveDesignOptionId(doc);
            if (active != null && active == opt.Id) return;
            var setId = opt.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId();
            var set = setId != null && setId != ElementId.InvalidElementId ? doc.GetElement(setId)?.Name : null;
            f.DesignOptionHidden = "'" + opt.Name + "' (secondary" + (set != null ? ", set '" + set + "'" : "") + ")";
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

        private static void Extent(Document hostDoc, View view, Element el, RevitLinkInstance? link, VisibilityFacts f)
        {
            var box = ModelBox(el, link);
            if (box == null) return;
            var (min, max) = box.Value;

            if (view is View3D v3 && v3.IsSectionBoxActive)
            {
                f.SectionBox = true;
                f.Crop = Against(v3.GetSectionBox(), min, max, threeD: true);
            }
            else if (view.CropBoxActive)
                f.Crop = Against(view.CropBox, min, max, threeD: false);

            if (view is not ViewPlan plan) return;
            var gen = plan.GenLevel;
            var levels = new FilteredElementCollector(hostDoc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            var range = plan.GetViewRange();

            double? Z(PlanViewPlane plane, bool low)
            {
                var lid = range.GetLevelId(plane);
                var off = range.GetOffset(plane);
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
                else lvl = hostDoc.GetElement(lid) as Level;
                return lvl == null ? (double?)null : lvl.ProjectElevation + off;
            }

            var cut = Z(PlanViewPlane.CutPlane, low: false);
            if (plan.ViewType == ViewType.CeilingPlan)
            {
                var top = Z(PlanViewPlane.TopClipPlane, low: false);
                if (cut.HasValue && top.HasValue)
                {
                    f.Range = ViewExtent.CeilingRange(min.Z, max.Z, cut.Value, top.Value);
                    if (f.Range == RangeCheck.Below) f.RangeDetail = "below the cut plane of this ceiling plan";
                    if (f.Range == RangeCheck.AboveTop) f.RangeDetail = "above the top of the view range";
                }
            }
            else
            {
                var depth = Z(PlanViewPlane.ViewDepthPlane, low: true);
                if (cut.HasValue && depth.HasValue)
                {
                    f.Range = ViewExtent.PlanRange(min.Z, max.Z, depth.Value, cut.Value);
                    if (f.Range == RangeCheck.Below)
                        f.RangeDetail = "entirely below the View Depth (" + RevitValues.FormatLength(hostDoc, range.GetOffset(PlanViewPlane.ViewDepthPlane)) + " from its level)";
                    if (f.Range == RangeCheck.AboveCut)
                        f.RangeDetail = "entirely above the Cut Plane (" + RevitValues.FormatLength(hostDoc, range.GetOffset(PlanViewPlane.CutPlane)) + " above " + (gen?.Name ?? "the level") + "); most categories are not drawn above it";
                }
            }

            if (gen != null)
            {
                var elLevel = RevitValues.LevelName(el.Document, el);
                if (elLevel != null && !string.Equals(elLevel, gen.Name, StringComparison.OrdinalIgnoreCase))
                {
                    f.OtherLevel = elLevel;
                    f.ViewLevel = gen.Name;
                }
            }
        }

        private static bool CollectorSees(Document hostDoc, View view, Element el, RevitLinkInstance? link, VisibilityFacts f)
        {
            if (link == null)
                return new FilteredElementCollector(hostDoc, view.Id).WherePasses(new ElementIdSetFilter(new List<ElementId> { el.Id })).GetElementCount() > 0;
#if REVIT2023_24
            f.Notes.Add("On Revit 2023/2024 the add-in cannot ask Revit whether a linked element is drawn; only the settings above were checked.");
            return true;
#else
            return new FilteredElementCollector(hostDoc, view.Id, link.Id).WherePasses(new ElementIdSetFilter(new List<ElementId> { el.Id })).GetElementCount() > 0;
#endif
        }
    }
}
