// BinaVibe.Dimensions — dimension planners, Revit-free
// (bina-ai R2 Task 25, dimensions family).
//
// Grid chains: grids grouped per axis and ordered by position; a chain needs
// two or more grids; expected segments = n-1 and expected total = last −
// first (mm). Element chains: which selected elements resolve a dimensionable
// reference along the direction, needing at least two. After commit, each
// created Dimension is re-read (segments, total) and compared within a
// tolerance.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Dimensions
{
    public sealed class GridRow
    {
        public string Name { get; init; } = "";
        public string Axis { get; init; } = "x";   // "x": chain runs along X (vertical grids); "y": along Y
        public double PositionMm { get; init; }
    }

    public sealed class DimRisk
    {
        public long Id { get; init; }
        public string Kind { get; init; } = "";
        public string Note { get; init; } = "";
    }

    public sealed class ChainSpec
    {
        public string Axis { get; init; } = "";
        public IReadOnlyList<string> Grids { get; init; } = Array.Empty<string>();
        public IReadOnlyList<double> PositionsMm { get; init; } = Array.Empty<double>();
        public int Segments => Math.Max(0, Grids.Count - 1);
        public double TotalMm => PositionsMm.Count == 0 ? 0 : PositionsMm.Max() - PositionsMm.Min();
    }

    public sealed class GridDimensionPlan
    {
        public IReadOnlyList<ChainSpec> Chains { get; }
        public IReadOnlyList<DimRisk> Risks { get; }
        public bool IsPlanView { get; }
        public int WouldCreate => IsPlanView ? Chains.Count : 0;

        private GridDimensionPlan(List<ChainSpec> chains, List<DimRisk> risks, bool isPlan) { Chains = chains; Risks = risks; IsPlanView = isPlan; }

        public static GridDimensionPlan Build(IEnumerable<GridRow> grids, bool isPlanView)
        {
            var risks = new List<DimRisk>();
            var chains = new List<ChainSpec>();
            if (!isPlanView)
                risks.Add(new DimRisk { Kind = "not_plan_view", Note = "the active view is not a plan view; open a floor plan first" });
            foreach (var axis in new[] { "x", "y" })
            {
                var g = grids.Where(r => r.Axis == axis).OrderBy(r => r.PositionMm).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
                if (g.Count < 2) continue;
                for (int i = 1; i < g.Count; i++)
                    if (Math.Abs(g[i].PositionMm - g[i - 1].PositionMm) < 0.5)
                        risks.Add(new DimRisk { Kind = "coincident_grids", Note = $"grids {g[i - 1].Name} and {g[i].Name} share a position; the chain will show a zero segment" });
                chains.Add(new ChainSpec { Axis = axis, Grids = g.Select(r => r.Name).ToList(), PositionsMm = g.Select(r => r.PositionMm).ToList() });
            }
            return new GridDimensionPlan(chains, risks, isPlanView);
        }

        public Dictionary<string, object?> ToPreview(string? viewName = null, int existingChains = 0) => new()
        {
            ["ok"] = IsPlanView,
            ["dry_run"] = true,
            ["view"] = viewName,
            ["chains"] = Chains.Select(c => (object)new Dictionary<string, object?>
                { ["axis"] = c.Axis, ["grids"] = c.Grids.ToList(), ["segments"] = c.Segments, ["total_mm"] = Math.Round(c.TotalMm, 1) }).ToList(),
            ["would_create"] = WouldCreate,
            ["existing_chains"] = existingChains,
            ["risks"] = Risks.Select(r => (object)new Dictionary<string, object?> { ["id"] = r.Id, ["kind"] = r.Kind, ["note"] = r.Note }).ToList(),
            ["error"] = IsPlanView ? null : "active view is not a plan view",
            ["headline"] = IsPlanView ? $"{WouldCreate} grid chain(s) would be placed (nothing created yet)" : "cannot dimension: active view is not a plan view",
        };
    }

    public sealed class ExpectedChain
    {
        public long Id { get; init; }
        public int Segments { get; init; }
        public double TotalMm { get; init; }
    }

    public static class ChainVerification
    {
        public static Dictionary<string, object?> Verify(IEnumerable<ExpectedChain> expected, Func<long, (int segments, double totalMm)?> readBack, double toleranceMm)
        {
            var exp = expected.ToList();
            int matches = 0;
            var mismatches = new List<object>();
            foreach (var e in exp)
            {
                var a = readBack(e.Id);
                if (a.HasValue && a.Value.segments == e.Segments && Math.Abs(a.Value.totalMm - e.TotalMm) <= toleranceMm) { matches++; continue; }
                if (mismatches.Count < 50)
                    mismatches.Add(new Dictionary<string, object?>
                    {
                        ["id"] = e.Id, ["expected_segments"] = e.Segments, ["actual_segments"] = a?.segments,
                        ["expected_total_mm"] = Math.Round(e.TotalMm, 1), ["actual_total_mm"] = a.HasValue ? Math.Round(a.Value.totalMm, 1) : (double?)null,
                    });
            }
            return new() { ["expected"] = exp.Count, ["matches"] = matches, ["mismatches"] = mismatches };
        }
    }

    public sealed class ReferenceRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public bool Found { get; init; }
    }

    public sealed class ElementDimensionPlan
    {
        public IReadOnlyList<ReferenceRow> Rows { get; }
        public string Direction { get; }
        public IReadOnlyList<DimRisk> Risks { get; }
        public int WouldMeasure => Rows.Count(r => r.Found);
        public int ExpectedSegments => Math.Max(0, WouldMeasure - 1);
        public bool WouldCreate => WouldMeasure >= 2;

        private ElementDimensionPlan(List<ReferenceRow> rows, string dir, List<DimRisk> risks) { Rows = rows; Direction = dir; Risks = risks; }

        public static ElementDimensionPlan Build(IEnumerable<ReferenceRow> rows, string direction)
        {
            var list = rows.ToList();
            var risks = list.Where(r => !r.Found)
                .Select(r => new DimRisk { Id = r.Id, Kind = "no_reference", Note = $"no dimensionable face on {r.Name} along {direction.ToUpperInvariant()}" }).ToList();
            return new ElementDimensionPlan(list, direction, risks);
        }

        public Dictionary<string, object?> ToPreview(string? viewName = null, IReadOnlyList<double>? directionVec = null) => new()
        {
            ["ok"] = WouldCreate,
            ["dry_run"] = true,
            ["view"] = viewName,
            ["direction"] = directionVec?.ToList(),
            ["references"] = Rows.Select(r => (object)new Dictionary<string, object?> { ["id"] = r.Id, ["name"] = r.Name, ["found"] = r.Found }).ToList(),
            ["would_measure"] = WouldMeasure,
            ["expected_segments"] = ExpectedSegments,
            ["risks"] = Risks.Select(r => (object)new Dictionary<string, object?> { ["id"] = r.Id, ["kind"] = r.Kind, ["note"] = r.Note }).ToList(),
            ["error"] = WouldCreate ? null : "fewer than 2 elements resolve a dimensionable reference — pick at least 2",
            ["headline"] = WouldCreate ? $"one chain with {ExpectedSegments} segment(s) across {WouldMeasure} element(s) (nothing created yet)" : "cannot dimension: fewer than 2 references",
        };
    }
}
