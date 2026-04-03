using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents a single Revit element with its cost data
    /// </summary>
    public class CostItem
    {
        public int ElementId { get; set; }
        public string Name { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        public string JkrCode { get; set; }
        public double Quantity { get; set; }
        public string Unit { get; set; }  // "m²", "m", "unit", "m³"
        public double UnitPrice { get; set; }
        public double TotalPrice => Quantity * UnitPrice;
        public string PriceSource { get; set; }  // "manual", "ai", "imported"
    }

    /// <summary>
    /// Price entry for the local price database
    /// </summary>
    public class PriceEntry
    {
        public string JkrCode { get; set; }
        public string Description { get; set; }
        public double UnitPrice { get; set; }
        public string Unit { get; set; }
        public string Source { get; set; }  // "manual", "ai", "imported"
        public string LastUpdated { get; set; }
    }

    /// <summary>
    /// Cost summary grouped by level or category
    /// </summary>
    public class CostSummary
    {
        public double GrandTotal { get; set; }
        public int TotalItems { get; set; }
        public int PricedItems { get; set; }
        public int LevelCount { get; set; }
        public List<CostGroup> ByLevel { get; set; } = new List<CostGroup>();
        public List<CostGroup> ByCategory { get; set; } = new List<CostGroup>();
    }

    /// <summary>
    /// A grouped cost breakdown (by level or by category)
    /// </summary>
    public class CostGroup
    {
        public string Name { get; set; }
        public int ItemCount { get; set; }
        public double TotalCost { get; set; }
        public double Percentage { get; set; }
        public List<CostItem> Items { get; set; } = new List<CostItem>();
    }

    /// <summary>
    /// Quick cost estimate based on total floor area and rate per sqft
    /// </summary>
    public class SqftEstimate
    {
        public double TotalFloorAreaM2 { get; set; }
        public double TotalFloorAreaSqft => TotalFloorAreaM2 * 10.764;
        public double RatePerSqft { get; set; }
        public string BuildingType { get; set; }
        public double EstimatedTotal => TotalFloorAreaSqft * RatePerSqft;
    }
}
