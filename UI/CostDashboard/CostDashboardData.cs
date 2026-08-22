using System.Collections.Generic;

namespace RevitWebAppSync.UI.CostDashboard
{
    /// <summary>
    /// One discipline row of the "Breakdown by discipline" list.
    /// <see cref="BrushKey"/> / <see cref="SoftBrushKey"/> are resource keys in
    /// <c>CostDashboardTokens.xaml</c> (e.g. "Cd.Blue" / "Cd.BlueSoft").
    /// </summary>
    public sealed class DisciplineBreakdown
    {
        public string Name { get; set; }
        public string BrushKey { get; set; }
        public string SoftBrushKey { get; set; }
        public int Items { get; set; }
        public int PricedPercent { get; set; }
        public string Cost { get; set; }
        public double CostPercent { get; set; }

        /// <summary>"2,145 items · 98% priced"</summary>
        public string ItemsLine => $"{Items:N0} items · {PricedPercent}% priced";

        /// <summary>"47.4% of cost"</summary>
        public string CostPercentLine => $"{CostPercent:0.0}% of cost";
    }

    /// <summary>
    /// All numbers shown on the dashboard. Pure data — no Revit, no computation.
    /// </summary>
    public sealed class MockDashboardModel
    {
        // Header
        public string Title { get; set; }
        public string ModelCode { get; set; }
        public string HostPill { get; set; }
        public string Scope { get; set; }

        // Estimated cost card
        public string Currency { get; set; }
        public string EstimatedCost { get; set; }
        public string ConfidencePill { get; set; }
        public string ProjectionPrefix { get; set; }
        public string ProjectionAmount { get; set; }
        public string ProjectionSuffix { get; set; }
        public double GaugePercent { get; set; }
        public string GaugeLabel { get; set; }

        // Stats strip
        public string QuantifiedItems { get; set; }
        public string QuantifiedItemsLabel { get; set; }
        public string Levels { get; set; }
        public string LevelsLabel { get; set; }
        public string AwaitingRate { get; set; }
        public string AwaitingRateLabel { get; set; }

        // Breakdown
        public string BreakdownHeader { get; set; }
        public string BreakdownHint { get; set; }
        public IReadOnlyList<DisciplineBreakdown> Disciplines { get; set; }

        // Footer
        public string Disclaimer { get; set; }
        public string SyncLabel { get; set; }
        public string SyncSubtext { get; set; }
        public string DataLabel { get; set; }
        public string Status { get; set; }
        public string LastSyncTime { get; set; }

        /// <summary>"Projects to RM 6.40M when every item carries a rate."</summary>
        public string Projection => ProjectionPrefix + ProjectionAmount + ProjectionSuffix;
    }

    /// <summary>
    /// Hard-coded mock data matching docs/cost-to-bim-OVERVIEW-reference.png 1:1.
    /// Downstream Overview / Charts tab cards read from here; the live Revit
    /// data source replaces this module later.
    /// </summary>
    public static class MockDashboardData
    {
        public const string Title = "Design to Cost";
        public const string ModelCode = "jkrAR24_5a_(BEde1A_p14-001)_A1_w-02_(S)…";
        public const string HostPill = "Revit 2026";
        public const string Scope = "5 disciplines · 7 levels";

        public const string Currency = "RM";
        public const string EstimatedCost = "6,014,750";
        public const string ConfidencePill = "High confidence";
        public const string ProjectionPrefix = "Projects to ";
        public const string ProjectionAmount = "RM 6.40M";
        public const string ProjectionSuffix = " when every item carries a rate.";
        public const double GaugePercent = 94;
        public const string GaugeLabel = "PRICED";

        public const string QuantifiedItems = "4,463";
        public const string QuantifiedItemsLabel = "quantified items";
        public const string Levels = "7";
        public const string LevelsLabel = "levels";
        public const string AwaitingRate = "267";
        public const string AwaitingRateLabel = "awaiting a rate";

        public const string BreakdownHeader = "BREAKDOWN BY DISCIPLINE";
        public const string BreakdownHint = "tap to open levels";

        public const string Disclaimer =
            "Rates from JKR Schedule of Rates 2024 and your Master DB.  Indicative order of cost — not a tender sum.";
        public const string SyncLabel = "Sync model";
        public const string SyncSubtext = "last run 2m";
        public const string DataLabel = "Data";
        public const string Status = "Model synced 2m · 4,196 of 4,463 items priced";
        public const string LastSyncTime = "14:50";

        public static IReadOnlyList<DisciplineBreakdown> Disciplines { get; } = new List<DisciplineBreakdown>
        {
            new DisciplineBreakdown { Name = "Architecture",        BrushKey = "Cd.Blue",   SoftBrushKey = "Cd.BlueSoft",   Cost = "RM 2,850,800", CostPercent = 47.4, Items = 2145, PricedPercent = 98 },
            new DisciplineBreakdown { Name = "Structure",           BrushKey = "Cd.Orange", SoftBrushKey = "Cd.OrangeSoft", Cost = "RM 2,481,896", CostPercent = 41.3, Items = 612,  PricedPercent = 98 },
            new DisciplineBreakdown { Name = "Mechanical",          BrushKey = "Cd.Teal",   SoftBrushKey = "Cd.TealSoft",   Cost = "RM 340,619",   CostPercent = 5.7,  Items = 388,  PricedPercent = 84 },
            new DisciplineBreakdown { Name = "Plumbing & sanitary", BrushKey = "Cd.Purple", SoftBrushKey = "Cd.PurpleSoft", Cost = "RM 236,588",   CostPercent = 3.9,  Items = 613,  PricedPercent = 87 },
            new DisciplineBreakdown { Name = "Electrical",          BrushKey = "Cd.Pink",   SoftBrushKey = "Cd.PinkSoft",   Cost = "RM 104,847",   CostPercent = 1.7,  Items = 705,  PricedPercent = 88 },
        };

        /// <summary>Builds a fresh model instance populated with the mock values above.</summary>
        public static MockDashboardModel Create() => new MockDashboardModel
        {
            Title = Title,
            ModelCode = ModelCode,
            HostPill = HostPill,
            Scope = Scope,
            Currency = Currency,
            EstimatedCost = EstimatedCost,
            ConfidencePill = ConfidencePill,
            ProjectionPrefix = ProjectionPrefix,
            ProjectionAmount = ProjectionAmount,
            ProjectionSuffix = ProjectionSuffix,
            GaugePercent = GaugePercent,
            GaugeLabel = GaugeLabel,
            QuantifiedItems = QuantifiedItems,
            QuantifiedItemsLabel = QuantifiedItemsLabel,
            Levels = Levels,
            LevelsLabel = LevelsLabel,
            AwaitingRate = AwaitingRate,
            AwaitingRateLabel = AwaitingRateLabel,
            BreakdownHeader = BreakdownHeader,
            BreakdownHint = BreakdownHint,
            Disciplines = Disciplines,
            Disclaimer = Disclaimer,
            SyncLabel = SyncLabel,
            SyncSubtext = SyncSubtext,
            DataLabel = DataLabel,
            Status = Status,
            LastSyncTime = LastSyncTime,
        };
    }
}
