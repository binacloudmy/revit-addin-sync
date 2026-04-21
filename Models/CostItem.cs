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
        // Auto-priceable items (excludes Rebar, Fittings, Connections, etc. that are
        // rolled into parent prices in Malaysian QS practice)
        public int PriceableItems { get; set; }
        public int PriceablePricedItems { get; set; }
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

    /// <summary>
    /// A sub-group within a component (e.g., "Column 450x600mm")
    /// </summary>
    public class ComponentSubGroup
    {
        public string Name { get; set; }
        public int ItemCount { get; set; }
        public int UnpricedCount { get; set; }
        public double TotalQuantity { get; set; }
        public string Unit { get; set; }
        public double TotalCost { get; set; }
        public double AverageUnitPrice { get; set; }
    }

    /// <summary>
    /// A component group (e.g., "Structural Columns")
    /// </summary>
    public class ComponentGroup
    {
        public string Category { get; set; }
        public int ItemCount { get; set; }
        public int UnpricedCount { get; set; }
        public double TotalCost { get; set; }
        public double Percentage { get; set; }
        public List<ComponentSubGroup> SubGroups { get; set; } = new List<ComponentSubGroup>();
    }

    /// <summary>
    /// Summary of costs grouped by Revit component type
    /// </summary>
    public class ComponentSummary
    {
        public int TotalComponents { get; set; }
        public int TotalItems { get; set; }
        public double TotalCost { get; set; }
        public List<ComponentGroup> Groups { get; set; } = new List<ComponentGroup>();
    }

    // ==================== M2 Cost Estimation Models ====================

    /// <summary>
    /// Request payload for POST /cost/m2-estimate
    /// </summary>
    public class M2EstimateRequest
    {
        public string kategori_bangunan { get; set; }
        public string sub_jenis_bangunan { get; set; }
        public string nama_bangunan { get; set; }
        public string kawasan { get; set; }
        public double luas_tapak { get; set; }
        public List<string> kerja_pakar_selected { get; set; }
        public string kerja_luar_sub_jenis { get; set; }
        public string project_name { get; set; } = "Untitled";
    }

    /// <summary>
    /// Item from GET /cost/m2-estimate/kerja-luar-types search
    /// </summary>
    public class M2KerjaLuarItem
    {
        public string sub_jenis { get; set; }
        public int bilangan_contoh { get; set; }
        public double peratusan { get; set; }
    }

    /// <summary>
    /// Response from POST /cost/m2-estimate
    /// </summary>
    public class M2EstimateResponse
    {
        public bool success { get; set; }
        public string error { get; set; }
        public M2CostBreakdown breakdown { get; set; }
    }

    /// <summary>
    /// Full breakdown of m2 cost estimation
    /// </summary>
    public class M2CostBreakdown
    {
        // Inputs
        public string kategori_bangunan { get; set; }
        public string sub_jenis_bangunan { get; set; }
        public string nama_bangunan { get; set; }
        public string kawasan { get; set; }
        public double faktor_lokaliti { get; set; }
        public double luas_tapak { get; set; }
        // Step 1
        public int bilangan_kajian { get; set; }
        public int jumlah_bil_kajian { get; set; }
        public double purata_sem_malaysia { get; set; }
        public double kos_kerja_utama { get; set; }
        public string fallback_kawasan { get; set; }
        // Step 2
        public List<M2SpecialistItem> kerja_pakar { get; set; } = new List<M2SpecialistItem>();
        public double jumlah_kerja_pakar { get; set; }
        // Step 3
        public double kerja_luar_peratusan { get; set; }
        public int kerja_luar_bilangan_contoh { get; set; }
        public double kos_kerja_luar { get; set; }
        // Step 4
        public double kerja_awalan_peratusan { get; set; }
        public string kerja_awalan_kategori { get; set; }
        public double kos_kerja_awalan { get; set; }
        // Step 5
        public double jumlah_kecil { get; set; }
        // Step 6
        public double pelbagai_peratusan { get; set; }
        public double kos_pelbagai { get; set; }
        // Step 7
        public double jumlah_kos_per_m2 { get; set; }
        public double jumlah_anggaran_kos_projek { get; set; }
        // Reference
        public List<string> pengecualian { get; set; } = new List<string>();
        public string sumber { get; set; }
    }

    /// <summary>
    /// A single specialist works item in the m2 breakdown
    /// </summary>
    public class M2SpecialistItem
    {
        public string jenis_pemasangan { get; set; }
        public double peratusan { get; set; }
        public double jumlah { get; set; }
    }

    /// <summary>
    /// Building type option from GET /cost/m2-estimate/building-types
    /// </summary>
    public class M2BuildingType
    {
        public string kategori_bangunan { get; set; }
        public List<M2SubJenis> sub_jenis { get; set; } = new List<M2SubJenis>();
        public List<string> nama_bangunan { get; set; } = new List<string>();
    }

    /// <summary>
    /// Sub-jenis with its own list of nama_bangunan
    /// </summary>
    public class M2SubJenis
    {
        public string name { get; set; }
        public List<string> nama_bangunan { get; set; } = new List<string>();
    }

    /// <summary>
    /// Region option from GET /cost/m2-estimate/regions
    /// </summary>
    public class M2Region
    {
        public string kawasan { get; set; }
        public double faktor_lokaliti { get; set; }
        public List<string> negeri { get; set; } = new List<string>();
    }
}
