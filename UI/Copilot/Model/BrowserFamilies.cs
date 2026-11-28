using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// What the Project Browser lists under Families, as text (built-in category key, category type, Revit class of the type), so the rule is unit-tested
    /// without the Revit API. Worked out on 26 Nov 2026 by comparing the picker's categories with the browser's own rows (38 in Revit-Test-v14):
    /// the browser lists model families and system families, puts every annotation family (tags, heads, title blocks) under one "Annotation Symbols"
    /// category, and does not list settings types (fluids, pipe materials, wire insulations), stair and railing parts, links and imports.
    /// The denied keys are Revit's own built-in categories, the same in every project.
    /// </summary>
    public static class BrowserFamilies
    {
        public const string AnnotationLabel = "Annotation Symbols";

        private static readonly HashSet<string> DeniedKeys = new HashSet<string>
        {
            "OST_StairsRailingBaluster", "OST_ConduitStandards", "OST_CoverType", "OST_StairsCutMarks", "OST_ElecDistributionSys", "OST_DivisionRules",
            "OST_Fascia", "OST_Fluids", "OST_Gutter", "OST_RailingHandRail", "OST_StairsLandings", "OST_MassFloorsAll", "OST_MassOpening", "OST_MassRoof",
            "OST_MassShade", "OST_MassWallsAll", "OST_MassGlazingAll", "OST_MechanicalEquipmentSet", "OST_PipeConnections", "OST_PipeMaterials",
            "OST_PipeSchedules", "OST_SiteProperty", "OST_RvtLinks", "OST_RasterImages", "OST_Reveals", "OST_RoofSoffit", "OST_StairsRuns", "OST_EdgeSlab",
            "OST_StackedWalls", "OST_StairsStringerCarriage", "OST_RailingSupport", "OST_RailingTopRail", "OST_ElectricalVoltage", "OST_Cornices", "OST_WireInsulations",
            "OST_WireMaterials", "OST_WireTemperatureRatings",
        };

        private static readonly HashSet<string> DeniedClasses = new HashSet<string> { "CADLinkType", "RevitLinkType", "ImageType" };

        // Categories the browser lists although Revit does not call them Model categories.
        private static readonly HashSet<string> ExtraKeys = new HashSet<string> { "OST_TilePatterns", "OST_MEPSystemZone", "OST_LinksAnalytical" };

        private static readonly HashSet<string> AnnotationClasses = new HashSet<string> { "FamilySymbol", "AnnotationSymbolType", "RoomTagType", "AreaTagType" };

        /// <summary>True when the browser lists a type of this category and class under Families.</summary>
        public static bool Listed(string categoryKey, string categoryType, string className)
        {
            // A category with no built-in key is an import (a linked DWG's "Import Symbol"), not a family category.
            if (string.IsNullOrEmpty(categoryKey) || DeniedClasses.Contains(className ?? "") || DeniedKeys.Contains(categoryKey)) return false;
            if (categoryType == "Model" || ExtraKeys.Contains(categoryKey ?? "")) return true;
            return categoryType == "Annotation" && AnnotationClasses.Contains(className ?? "");
        }

        /// <summary>The Families category a type sits under: its own category, or "Annotation Symbols" for an annotation family.</summary>
        public static string CategoryLabel(string categoryName, string categoryType) => categoryType == "Annotation" ? AnnotationLabel : categoryName;
    }
}
