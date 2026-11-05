#nullable enable
// Revit-free "why can't I see it?" model (spec §11 #3, plan §5 W2 Task 4).
// VisibilityExplainer (Revit-bound) fills one VisibilityFacts per element from
// the view's settings; this file turns facts into reasons, always in the same
// order, each with a fix in English and Malay:
//   1 category hidden          6 workset hidden
//   2 element hidden           7 phase
//   3 temporary hide/isolate   8 design option
//   4 view filter              9 view range / crop / section box
//   5 view template controls  10 element on another level
// When a view template controls the setting that hides the element, the fix
// says to change the template (reason 5 is added once).

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public enum VisibilityReasonKind
    {
        CategoryHidden = 1,
        ElementHidden = 2,
        TemporaryHideIsolate = 3,
        Filter = 4,
        TemplateControlled = 5,
        Workset = 6,
        Phase = 7,
        DesignOption = 8,
        ViewRangeOrCrop = 9,
        OtherLevel = 10,
        NotDetermined = 99,
    }

    public enum RangeCheck { NotApplicable, InRange, Below, AboveCut, AboveTop }

    public enum CropCheck { NotCropped, Inside, Partial, Outside }

    /// <summary>Plain facts about one element in one view (no Revit types).</summary>
    public sealed class VisibilityFacts
    {
        public ElementRef Ref = new(0, null);
        public string? Category;
        public string? LinkName;

        /// <summary>FilteredElementCollector(doc, view) returns the element.</summary>
        public bool CollectorVisible;

        public bool CategoryHidden;
        /// <summary>"Show model categories in this view" is off.</summary>
        public bool ModelCategoriesHidden;
        public bool ElementHidden;
        /// <summary>The link instance itself is hidden in the view.</summary>
        public bool LinkInstanceHidden;
        public bool TemporaryHidden;
        public List<string> HidingFilters = new();

        public string? TemplateName;
        public bool TemplateControlsCategories;
        public bool TemplateControlsFilters;
        public bool TemplateControlsWorksets;
        public bool TemplateControlsPhase;
        public bool TemplateControlsRange;

        /// <summary>Name of the workset when it is hidden in the view.</summary>
        public string? WorksetHidden;
        /// <summary>Why phasing hides it, in words (status, view phase, phase filter).</summary>
        public string? PhaseHidden;
        /// <summary>The secondary design option it is in, in words.</summary>
        public string? DesignOptionHidden;

        public CropCheck Crop = CropCheck.NotCropped;
        public bool SectionBox;
        public RangeCheck Range = RangeCheck.NotApplicable;
        public string? RangeDetail;

        /// <summary>The element's level when it differs from the plan's level.</summary>
        public string? OtherLevel;
        public string? ViewLevel;

        public List<string> Notes = new();
    }

    public sealed class VisibilityReason
    {
        public VisibilityReasonKind Kind;
        public string Code = "";
        public string Detail = "";
        public string FixEn = "";
        public string FixMs = "";

        internal string Key => Code + "|" + Detail;
    }

    public static class VisibilityReasons
    {
        private static string Q(string? s) => "'" + (s ?? "") + "'";

        public static List<VisibilityReason> Explain(VisibilityFacts f)
        {
            var list = new List<VisibilityReason>();
            var template = f.TemplateName;
            bool templateUsed = false;

            void Add(VisibilityReasonKind kind, string code, string detail, string fixEn, string fixMs) =>
                list.Add(new VisibilityReason { Kind = kind, Code = code, Detail = detail, FixEn = fixEn, FixMs = fixMs });

            if (f.CategoryHidden || f.ModelCategoriesHidden)
            {
                var what = f.ModelCategoriesHidden && !f.CategoryHidden
                    ? "Model categories are turned off in this view"
                    : "Category " + Q(f.Category) + " is turned off in this view";
                if (template != null && f.TemplateControlsCategories)
                {
                    templateUsed = true;
                    Add(VisibilityReasonKind.CategoryHidden, "category_hidden", what + " (set by view template " + Q(template) + ").",
                        "Turn on " + Q(f.Category) + " in view template " + Q(template) + " (Manage > View Templates > V/G Overrides Model), or remove the template from this view.",
                        "Hidupkan " + Q(f.Category) + " dalam templat pandangan " + Q(template) + " (Manage > View Templates > V/G Overrides Model), atau buang templat daripada pandangan ini.");
                }
                else
                    Add(VisibilityReasonKind.CategoryHidden, "category_hidden", what + ".",
                        "Turn on " + Q(f.Category) + " in Visibility/Graphics (VV) > Model Categories"
                        + (f.ModelCategoriesHidden ? ", and tick 'Show model categories in this view'." : "."),
                        "Hidupkan " + Q(f.Category) + " dalam Visibility/Graphics (VV) > Model Categories"
                        + (f.ModelCategoriesHidden ? ", dan tandakan 'Show model categories in this view'." : "."));
            }

            if (f.ElementHidden || f.LinkInstanceHidden)
                Add(VisibilityReasonKind.ElementHidden, "element_hidden",
                    f.LinkInstanceHidden ? "Link " + Q(f.LinkName) + " is hidden in this view (Hide in View)." : "The element is hidden in this view (Hide in View > Elements).",
                    "Turn on Reveal Hidden Elements (light bulb), select it, then right-click > Unhide in View > Elements.",
                    "Hidupkan Reveal Hidden Elements (ikon mentol), pilih elemen itu, kemudian klik kanan > Unhide in View > Elements.");

            if (f.TemporaryHidden)
                Add(VisibilityReasonKind.TemporaryHideIsolate, "temporary_hide_isolate",
                    "Temporary Hide/Isolate is on in this view and leaves the element out.",
                    "Click Temporary Hide/Isolate (sunglasses icon) > Reset Temporary Hide/Isolate.",
                    "Klik Temporary Hide/Isolate (ikon cermin mata hitam) > Reset Temporary Hide/Isolate.");

            if (f.HidingFilters.Count > 0)
            {
                var names = string.Join(", ", f.HidingFilters.Select(Q));
                var detail = "Hidden by view filter " + names + ".";
                if (template != null && f.TemplateControlsFilters)
                {
                    templateUsed = true;
                    Add(VisibilityReasonKind.Filter, "filter", detail,
                        "In view template " + Q(template) + " > V/G Overrides Filters, tick Visibility for " + names + " or remove the filter.",
                        "Dalam templat pandangan " + Q(template) + " > V/G Overrides Filters, tandakan Visibility untuk " + names + " atau buang penapis itu.");
                }
                else
                    Add(VisibilityReasonKind.Filter, "filter", detail,
                        "In Visibility/Graphics (VV) > Filters, tick Visibility for " + names + " or remove the filter from this view.",
                        "Dalam Visibility/Graphics (VV) > Filters, tandakan Visibility untuk " + names + " atau buang penapis itu daripada pandangan ini.");
            }

            bool worksetByTemplate = f.WorksetHidden != null && template != null && f.TemplateControlsWorksets;
            bool phaseByTemplate = f.PhaseHidden != null && template != null && f.TemplateControlsPhase;
            bool rangeByTemplate = (f.Range is RangeCheck.Below or RangeCheck.AboveCut or RangeCheck.AboveTop) && template != null && f.TemplateControlsRange;
            if (templateUsed || worksetByTemplate || phaseByTemplate || rangeByTemplate)
                Add(VisibilityReasonKind.TemplateControlled, "template_controlled",
                    "View template " + Q(template) + " controls these settings, so they cannot be changed in the view itself.",
                    "Edit template " + Q(template) + " (Manage > View Templates), or set this view's View Template to <None> to change it here. Other views using the template change too.",
                    "Ubah templat " + Q(template) + " (Manage > View Templates), atau tetapkan View Template pandangan ini kepada <None> untuk mengubahnya di sini. Pandangan lain yang guna templat ini turut berubah.");

            if (f.WorksetHidden != null)
                Add(VisibilityReasonKind.Workset, "workset_hidden", "Workset " + Q(f.WorksetHidden) + " is hidden in this view.",
                    "In Visibility/Graphics (VV) > Worksets" + (worksetByTemplate ? " of template " + Q(template) : "") + ", set " + Q(f.WorksetHidden) + " to Show.",
                    "Dalam Visibility/Graphics (VV) > Worksets" + (worksetByTemplate ? " templat " + Q(template) : "") + ", tetapkan " + Q(f.WorksetHidden) + " kepada Show.");

            if (f.PhaseHidden != null)
                Add(VisibilityReasonKind.Phase, "phase", "Phasing: " + f.PhaseHidden + ".",
                    "Change the view's Phase or Phase Filter in Properties (for example Phase Filter 'Show All'), or the element's Phase Created / Phase Demolished.",
                    "Tukar Phase atau Phase Filter pandangan dalam Properties (contohnya Phase Filter 'Show All'), atau Phase Created / Phase Demolished elemen itu.");

            if (f.DesignOptionHidden != null)
                Add(VisibilityReasonKind.DesignOption, "design_option", "In design option " + f.DesignOptionHidden + ", which views show only when chosen.",
                    "Make that option active (Manage > Design Options) or choose it for this view in Visibility/Graphics (VV) > Design Options.",
                    "Jadikan pilihan itu aktif (Manage > Design Options) atau pilih untuk pandangan ini dalam Visibility/Graphics (VV) > Design Options.");

            if (f.Crop == CropCheck.Outside)
                Add(VisibilityReasonKind.ViewRangeOrCrop, "view_range_or_crop",
                    f.SectionBox ? "Outside the 3D view's section box." : "Outside the view's crop region.",
                    f.SectionBox ? "Turn off Section Box in Properties or drag its grips to include the element." : "Turn off Crop View or enlarge the crop region to include it.",
                    f.SectionBox ? "Matikan Section Box dalam Properties atau tarik pemegangnya supaya elemen itu termasuk." : "Matikan Crop View atau besarkan kawasan crop supaya elemen itu termasuk.");
            if (f.Range is RangeCheck.Below or RangeCheck.AboveCut or RangeCheck.AboveTop)
                Add(VisibilityReasonKind.ViewRangeOrCrop, "view_range_or_crop",
                    "Outside the view range: " + (f.RangeDetail ?? f.Range.ToString()) + ".",
                    "Edit View Range (Properties > View Range" + (rangeByTemplate ? ", in template " + Q(template) : "") + "): lower Bottom and View Depth, or raise the Cut Plane / Top.",
                    "Ubah View Range (Properties > View Range" + (rangeByTemplate ? ", dalam templat " + Q(template) : "") + "): rendahkan Bottom dan View Depth, atau naikkan Cut Plane / Top.");

            if (f.OtherLevel != null && f.ViewLevel != null && f.Range != RangeCheck.InRange)
                Add(VisibilityReasonKind.OtherLevel, "other_level",
                    "It is on level " + Q(f.OtherLevel) + "; this plan shows level " + Q(f.ViewLevel) + ".",
                    "Open the plan of " + Q(f.OtherLevel) + ", or show it here with Underlay or a wider View Range.",
                    "Buka pelan " + Q(f.OtherLevel) + ", atau paparkan di sini dengan Underlay atau View Range yang lebih luas.");

            if (list.Count == 0 && !f.CollectorVisible)
                Add(VisibilityReasonKind.NotDetermined, "not_determined",
                    "Revit does not list it in this view, and none of the checked settings hides it.",
                    "Check Detail Level, graphic overrides (transparency, halftone), the Revit Links settings in VV for linked elements, and whether the view type can show this category.",
                    "Semak Detail Level, graphic overrides (ketelusan, halftone), tetapan Revit Links dalam VV untuk elemen pautan, dan sama ada jenis pandangan ini boleh memaparkan kategori ini.");

            return list.OrderBy(r => (int)r.Kind).ToList();
        }

        /// <summary>The report for many elements: identical reason sets are grouped.</summary>
        public static (Dictionary<string, object?> Data, string Summary) Report(
            string viewName, string viewType, string? templateName, IReadOnlyList<VisibilityFacts> facts)
        {
            var explained = facts.Select(f => (Facts: f, Reasons: Explain(f))).ToList();
            var hidden = explained.Where(e => e.Reasons.Count > 0).ToList();
            var groups = hidden
                .GroupBy(e => string.Join("||", e.Reasons.Select(r => r.Key)))
                .Select(g => (Reasons: g.First().Reasons, Refs: g.Select(e => e.Facts.Ref).ToList(), Notes: g.SelectMany(e => e.Facts.Notes).Distinct().ToList()))
                .OrderByDescending(g => g.Refs.Count)
                .ToList();

            var data = new Dictionary<string, object?>
            {
                ["view"] = new Dictionary<string, object?> { ["name"] = viewName, ["type"] = viewType, ["template"] = templateName },
                ["checked"] = facts.Count,
                ["visible"] = facts.Count - hidden.Count,
                ["not_visible"] = hidden.Count,
                ["groups"] = groups.Select(g => new Dictionary<string, object?>
                {
                    ["count"] = g.Refs.Count,
                    ["refs"] = g.Refs.Take(20).ToList(),
                    ["reasons"] = g.Reasons.Select(r => new Dictionary<string, object?>
                    {
                        ["code"] = r.Code,
                        ["detail"] = r.Detail,
                        ["fix_en"] = r.FixEn,
                        ["fix_ms"] = r.FixMs,
                    }).ToList(),
                    ["notes"] = g.Notes.Count == 0 ? null : g.Notes,
                }).ToList(),
                ["order"] = "category, element, temporary hide, filter, template, workset, phase, design option, view range/crop, level",
            };

            string summary;
            if (hidden.Count == 0)
                summary = "In " + Q(viewName) + ", " + (facts.Count == 1 ? "the element is" : "all " + facts.Count + " elements are")
                          + " visible; no hiding setting applies.";
            else
            {
                var parts = groups.Take(3).Select(g => g.Refs.Count + " " + Short(g.Reasons[0])).ToList();
                summary = "In " + Q(viewName) + ", " + hidden.Count + " of " + facts.Count + " element" + (facts.Count == 1 ? " is" : "s are")
                          + " not visible: " + string.Join("; ", parts) + (groups.Count > 3 ? "; …" : "") + ".";
            }
            return (data, summary);
        }

        public static Envelope Build(string viewName, string viewType, string? templateName, IReadOnlyList<VisibilityFacts> facts)
        {
            var (data, summary) = Report(viewName, viewType, templateName, facts);
            return ReadEnvelope.Done(summary, data, count: facts.Count,
                next: "Tell the drafter each reason with its fix (fix_ms for Malay); no change is made by inspect.");
        }

        private static string Short(VisibilityReason r) => r.Code switch
        {
            "category_hidden" => "with the category turned off",
            "element_hidden" => "hidden in view",
            "temporary_hide_isolate" => "left out by Temporary Hide/Isolate",
            "filter" => "hidden by " + r.Detail.Replace("Hidden by view ", "").TrimEnd('.'),
            "template_controlled" => "controlled by the view template",
            "workset_hidden" => "in a hidden workset (" + r.Detail.TrimEnd('.') + ")",
            "phase" => "hidden by phasing",
            "design_option" => "in a design option not shown",
            "view_range_or_crop" => r.Detail.TrimEnd('.').Replace("Outside the", "outside the"),
            "other_level" => "on another level",
            _ => "for a reason the checks did not find",
        };
    }

    /// <summary>Revit-free geometry checks against a view's extent (all values in one unit).</summary>
    public static class ViewExtent
    {
        /// <summary>Floor/structural plan: visible between the view depth and the cut plane
        /// (elements cut by the plane included); most categories are not drawn above the cut.</summary>
        public static RangeCheck PlanRange(double elemMinZ, double elemMaxZ, double depthZ, double cutZ)
        {
            if (elemMaxZ < depthZ) return RangeCheck.Below;
            if (elemMinZ > cutZ) return RangeCheck.AboveCut;
            return RangeCheck.InRange;
        }

        /// <summary>Reflected ceiling plan: looks up from the cut plane to the top.</summary>
        public static RangeCheck CeilingRange(double elemMinZ, double elemMaxZ, double cutZ, double topZ)
        {
            if (elemMaxZ < cutZ) return RangeCheck.Below;
            if (elemMinZ > topZ) return RangeCheck.AboveTop;
            return RangeCheck.InRange;
        }

        /// <summary>An element rectangle against the crop rectangle (both in crop coordinates).</summary>
        public static CropCheck Crop(double minX, double minY, double maxX, double maxY,
            double cropMinX, double cropMinY, double cropMaxX, double cropMaxY)
        {
            if (maxX < cropMinX || minX > cropMaxX || maxY < cropMinY || minY > cropMaxY) return CropCheck.Outside;
            if (minX >= cropMinX && maxX <= cropMaxX && minY >= cropMinY && maxY <= cropMaxY) return CropCheck.Inside;
            return CropCheck.Partial;
        }
    }
}
