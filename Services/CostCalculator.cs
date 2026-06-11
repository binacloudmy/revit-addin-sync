using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Calculates cost summaries from a list of cost items.
    /// Groups by level and category with totals and percentages.
    /// </summary>
    public static class CostCalculator
    {
        /// <summary>
        /// Calculate full cost summary from items
        /// </summary>
        public static CostSummary Calculate(List<CostItem> items)
        {
            var summary = new CostSummary
            {
                TotalItems = items.Count,
                PricedItems = items.Count(i => i.UnitPrice > 0),
                GrandTotal = items.Sum(i => i.TotalPrice)
            };

            // Group by Level
            summary.ByLevel = items
                .GroupBy(i => i.Level ?? "Unassigned")
                .Select(g => new CostGroup
                {
                    Name = g.Key,
                    ItemCount = g.Count(),
                    TotalCost = g.Sum(i => i.TotalPrice),
                    Items = g.OrderBy(i => i.Category).ThenBy(i => i.Name).ToList()
                })
                .OrderByDescending(g => g.TotalCost)
                .ToList();

            // Group by Category
            summary.ByCategory = items
                .GroupBy(i => i.Category ?? "Other")
                .Select(g => new CostGroup
                {
                    Name = g.Key,
                    ItemCount = g.Count(),
                    TotalCost = g.Sum(i => i.TotalPrice),
                    Items = g.OrderBy(i => i.Name).ToList()
                })
                .OrderByDescending(g => g.TotalCost)
                .ToList();

            // Calculate percentages
            if (summary.GrandTotal > 0)
            {
                foreach (var group in summary.ByLevel)
                    group.Percentage = (group.TotalCost / summary.GrandTotal) * 100;
                foreach (var group in summary.ByCategory)
                    group.Percentage = (group.TotalCost / summary.GrandTotal) * 100;
            }

            summary.LevelCount = summary.ByLevel.Count;

            return summary;
        }

        /// <summary>
        /// Calculate summary for a specific level only
        /// </summary>
        public static CostSummary CalculateForLevel(List<CostItem> items, string levelName)
        {
            var filtered = items.Where(i =>
                string.Equals(i.Level, levelName, System.StringComparison.OrdinalIgnoreCase)).ToList();
            return Calculate(filtered);
        }
    }
}
