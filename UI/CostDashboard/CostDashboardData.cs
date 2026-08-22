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
            new DisciplineBreakdown { Name = "Plumbing & Sanitary", BrushKey = "Cd.Purple", SoftBrushKey = "Cd.PurpleSoft", Cost = "RM 236,588",   CostPercent = 3.9,  Items = 613,  PricedPercent = 87 },
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

    // ═══════════════════════════ CHARTS TAB ═══════════════════════════

    /// <summary>One wedge of the "Cost by discipline" donut + its legend row.</summary>
    public sealed class DonutSegment
    {
        public string Name { get; set; }
        public double Percent { get; set; }
        public string BrushKey { get; set; }

        /// <summary>"47.4%"</summary>
        public string PercentLabel => $"{Percent:0.0}%";
    }

    /// <summary>One revision point of the "Cost by revision" line chart (RM millions).</summary>
    public sealed class RevisionPoint
    {
        public string Label { get; set; }
        public double Value { get; set; }
        public bool IsCurrent { get; set; }

        /// <summary>"7.42"</summary>
        public string ValueLabel => Value.ToString("0.00");

        /// <summary>"R7 · current" for the current revision, otherwise "R1".</summary>
        public string AxisLabel => IsCurrent ? Label + " · current" : Label;
    }

    /// <summary>One row of "Top cost drivers".</summary>
    public sealed class CostDriver
    {
        public string Discipline { get; set; }
        public string Name { get; set; }
        /// <summary>Quantity caption, e.g. "118 nos"; null when not shown.</summary>
        public string Quantity { get; set; }
        public string Cost { get; set; }
        public int Percent { get; set; }
        public string BrushKey { get; set; }
        public string SoftBrushKey { get; set; }

        /// <summary>"Architecture · 118 nos" (or just "Architecture").</summary>
        public string DisciplineLine => string.IsNullOrEmpty(Quantity) ? Discipline : Discipline + " · " + Quantity;

        /// <summary>"24%"</summary>
        public string PercentLabel => Percent + "%";
    }

    /// <summary>One row of "Priced cost by level". <see cref="Cost"/> null = no priced cost (shown as "—").</summary>
    public sealed class LevelCost
    {
        public string Name { get; set; }
        public double? Cost { get; set; }
        public bool IsUnassigned { get; set; }

        /// <summary>"RM 3,592,277" or "—".</summary>
        public string CostLabel => Cost.HasValue ? $"RM {Cost.Value:N0}" : "—";
    }

    /// <summary>All numbers shown on the Charts tab. Pure data — no Revit.</summary>
    public sealed class MockChartsModel
    {
        // Card 1 — cost by discipline
        public string DisciplineHeader { get; set; }
        public string DisciplineHint { get; set; }
        public string DonutTotal { get; set; }
        public string DonutTotalLabel { get; set; }
        public IReadOnlyList<DonutSegment> DonutSegments { get; set; }
        public string DisciplineNote { get; set; }

        // Card 2 — cost by revision
        public string RevisionHeader { get; set; }
        public string RevisionPill { get; set; }
        public IReadOnlyList<RevisionPoint> Revisions { get; set; }
        public double RevisionAxisMin { get; set; }
        public double RevisionAxisMax { get; set; }
        public double RevisionAxisStep { get; set; }
        public string RevisionScaleNote { get; set; }
        public string RevisionNotePrefix { get; set; }
        public string RevisionNoteAmount { get; set; }
        public string RevisionNoteSuffix { get; set; }

        // Card 3 — cost per m²
        public string PerM2Header { get; set; }
        public string PerM2Gfa { get; set; }
        public string PerM2Currency { get; set; }
        public string PerM2Value { get; set; }
        public string PerM2Unit { get; set; }
        public string PerM2Pill { get; set; }
        public string ThisDesignLabel { get; set; }
        public double ThisDesign { get; set; }
        public string JkrMedianLabel { get; set; }
        public double JkrMedian { get; set; }
        public string PerM2Note { get; set; }

        // Card 4 — top cost drivers
        public string DriversHeader { get; set; }
        public string DriversHint { get; set; }
        public IReadOnlyList<CostDriver> Drivers { get; set; }

        // Card 5 — priced cost by level
        public string LevelsHeader { get; set; }
        public string LevelsHint { get; set; }
        public IReadOnlyList<LevelCost> LevelCosts { get; set; }

        // Export buttons
        public string ExportPdfLabel { get; set; }
        public string ExportXlsxLabel { get; set; }
    }

    /// <summary>
    /// Hard-coded mock data matching docs/cost-to-bim-CHARTS-reference.png 1:1.
    /// </summary>
    public static class MockChartsData
    {
        public const string DisciplineHeader = "COST BY DISCIPLINE";
        public const string DisciplineHint = "priced cost";
        public const string DonutTotal = "RM 6.01M";
        public const string DonutTotalLabel = "TOTAL";
        public const string DisciplineNote = "Architecture and structure carry 89% of priced cost.";

        public static IReadOnlyList<DonutSegment> DonutSegments { get; } = new List<DonutSegment>
        {
            new DonutSegment { Name = "Architecture", Percent = 47.4, BrushKey = "Cd.Blue"   },
            new DonutSegment { Name = "Structure",    Percent = 41.3, BrushKey = "Cd.Orange" },
            new DonutSegment { Name = "Mechanical",   Percent = 5.7,  BrushKey = "Cd.Teal"   },
            new DonutSegment { Name = "Plumbing",     Percent = 3.9,  BrushKey = "Cd.Purple" },
            new DonutSegment { Name = "Electrical",   Percent = 1.7,  BrushKey = "Cd.Pink"   },
        };

        public const string RevisionHeader = "COST BY REVISION";
        public const string RevisionPill = "−1.1% vs R6";
        public const double RevisionAxisMin = 6.0;
        public const double RevisionAxisMax = 7.5;
        public const double RevisionAxisStep = 0.5;
        public const string RevisionScaleNote = "R1–R7 · scale from RM 6.0M";
        public const string RevisionNotePrefix = "Down ";
        public const string RevisionNoteAmount = "RM 1.02M";
        public const string RevisionNoteSuffix = " since R1 — value engineering of frame and finishes.";

        public static IReadOnlyList<RevisionPoint> Revisions { get; } = new List<RevisionPoint>
        {
            new RevisionPoint { Label = "R1", Value = 7.42 },
            new RevisionPoint { Label = "R2", Value = 7.05 },
            new RevisionPoint { Label = "R3", Value = 6.82 },
            new RevisionPoint { Label = "R4", Value = 6.68 },
            new RevisionPoint { Label = "R5", Value = 6.55 },
            new RevisionPoint { Label = "R6", Value = 6.47 },
            new RevisionPoint { Label = "R7", Value = 6.40, IsCurrent = true },
        };

        public const string PerM2Header = "COST PER M² VS JKR MEDIAN";
        public const string PerM2Gfa = "GFA 3,240 m²";
        public const string PerM2Currency = "RM";
        public const string PerM2Value = "1,975";
        public const string PerM2Unit = "/ m²";
        public const string PerM2Pill = "4% below median";
        public const string ThisDesignLabel = "This design";
        public const double ThisDesign = 1975;
        public const string JkrMedianLabel = "JKR median";
        public const double JkrMedian = 2050;
        public const string PerM2Note =
            "RM 6.40M projected ÷ 3,240 m² GFA from model areas. Median: JKR institutional buildings, Klang Valley, 2024–25.";

        public const string DriversHeader = "TOP COST DRIVERS";
        public const string DriversHint = "all disciplines";

        public static IReadOnlyList<CostDriver> Drivers { get; } = new List<CostDriver>
        {
            new CostDriver { Discipline = "Structure",    Name = "Concrete slabs & foundations", Quantity = "1,240 m²", Cost = "RM 1.53M", Percent = 24, BrushKey = "Cd.Orange", SoftBrushKey = "Cd.OrangeSoft" },
            new CostDriver { Discipline = "Architecture", Name = "Blockwork & partitions",       Quantity = "1,180 m²", Cost = "RM 1.04M", Percent = 16, BrushKey = "Cd.Blue",   SoftBrushKey = "Cd.BlueSoft"   },
            new CostDriver { Discipline = "Architecture", Name = "Doors & windows",              Quantity = "118 nos",  Cost = "RM 736k",  Percent = 12, BrushKey = "Cd.Blue",   SoftBrushKey = "Cd.BlueSoft"   },
            new CostDriver { Discipline = "Structure",    Name = "Columns & beams",              Quantity = "144 nos",  Cost = "RM 664k",  Percent = 10, BrushKey = "Cd.Orange", SoftBrushKey = "Cd.OrangeSoft" },
            new CostDriver { Discipline = "Mechanical",   Name = "ACMV ductwork & units",        Quantity = "210 m",    Cost = "RM 375k",  Percent = 6,  BrushKey = "Cd.Teal",   SoftBrushKey = "Cd.TealSoft"   },
            new CostDriver { Discipline = "Plumbing",     Name = "Sanitary fittings & pipework", Quantity = "48 nos",   Cost = "RM 282k",  Percent = 4,  BrushKey = "Cd.Purple", SoftBrushKey = "Cd.PurpleSoft" },
            new CostDriver { Discipline = "Electrical",   Name = "Lighting & containment",       Quantity = "246 nos",  Cost = "RM 208k",  Percent = 3,  BrushKey = "Cd.Pink",   SoftBrushKey = "Cd.PinkSoft"   },
        };

        public const string LevelsHeader = "PRICED COST BY LEVEL";
        public const string LevelsHint = "all disciplines";

        public static IReadOnlyList<LevelCost> LevelCosts { get; } = new List<LevelCost>
        {
            new LevelCost { Name = "Aras 01",      Cost = 3592277 },
            new LevelCost { Name = "Aras 02",      Cost = 1832633 },
            new LevelCost { Name = "Aras Tanah",   Cost = 561171  },
            new LevelCost { Name = "Aras Langkan", Cost = 24162   },
            new LevelCost { Name = "Unassigned",   Cost = 4506, IsUnassigned = true },
            new LevelCost { Name = "Aras Bumbung", Cost = null },
            new LevelCost { Name = "Aras Pentas",  Cost = null },
        };

        public const string ExportPdfLabel = "Board summary (PDF)";
        public const string ExportXlsxLabel = "XLSX";

        /// <summary>Builds a fresh chart model populated with the mock values above.</summary>
        public static MockChartsModel Create() => new MockChartsModel
        {
            DisciplineHeader = DisciplineHeader,
            DisciplineHint = DisciplineHint,
            DonutTotal = DonutTotal,
            DonutTotalLabel = DonutTotalLabel,
            DonutSegments = DonutSegments,
            DisciplineNote = DisciplineNote,
            RevisionHeader = RevisionHeader,
            RevisionPill = RevisionPill,
            Revisions = Revisions,
            RevisionAxisMin = RevisionAxisMin,
            RevisionAxisMax = RevisionAxisMax,
            RevisionAxisStep = RevisionAxisStep,
            RevisionScaleNote = RevisionScaleNote,
            RevisionNotePrefix = RevisionNotePrefix,
            RevisionNoteAmount = RevisionNoteAmount,
            RevisionNoteSuffix = RevisionNoteSuffix,
            PerM2Header = PerM2Header,
            PerM2Gfa = PerM2Gfa,
            PerM2Currency = PerM2Currency,
            PerM2Value = PerM2Value,
            PerM2Unit = PerM2Unit,
            PerM2Pill = PerM2Pill,
            ThisDesignLabel = ThisDesignLabel,
            ThisDesign = ThisDesign,
            JkrMedianLabel = JkrMedianLabel,
            JkrMedian = JkrMedian,
            PerM2Note = PerM2Note,
            DriversHeader = DriversHeader,
            DriversHint = DriversHint,
            Drivers = Drivers,
            LevelsHeader = LevelsHeader,
            LevelsHint = LevelsHint,
            LevelCosts = LevelCosts,
            ExportPdfLabel = ExportPdfLabel,
            ExportXlsxLabel = ExportXlsxLabel,
        };
    }
}
