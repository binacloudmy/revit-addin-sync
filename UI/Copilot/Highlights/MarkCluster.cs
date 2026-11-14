#nullable enable
// Dots that would overlap on screen become one badge with their count (the "Gabung" result view, G3). Two marks are
// together when their centres are closer on screen than 1.2 dot diameters; together is transitive (a chain of close
// dots is one badge). The points are screen points in DIP; a grid of cells one reach wide finds the neighbours, so a
// few thousand marks cost a few thousand cell lookups, not every pair. Revit-free and WPF-free.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    public static class MarkCluster
    {
        /// <summary>Two marks are together when their centres are closer than this many dot diameters.</summary>
        public const double Together = 1.2;

        /// <summary>The marks that go together, as lists of indexes into <paramref name="points"/>: each list in ascending
        /// order, the lists ordered by their first index. A point that is not a number stays alone.</summary>
        public static List<List<int>> Group(IReadOnlyList<(double X, double Y)> points, double diameter)
        {
            var n = points?.Count ?? 0;
            var parent = new int[n];
            for (var i = 0; i < n; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

            var reach = Together * diameter;
            if (n > 1 && reach > 0)
            {
                var cells = new Dictionary<(long, long), List<int>>();
                var reach2 = reach * reach;
                for (var i = 0; i < n; i++)
                {
                    var (x, y) = points![i];
                    if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) continue;
                    var cx = (long)Math.Floor(x / reach); var cy = (long)Math.Floor(y / reach);
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            if (!cells.TryGetValue((cx + dx, cy + dy), out var near)) continue;
                            foreach (var j in near)
                            {
                                var ex = points[j].X - x; var ey = points[j].Y - y;
                                if (ex * ex + ey * ey < reach2) Join(i, j);
                            }
                        }
                    if (!cells.TryGetValue((cx, cy), out var own)) cells[(cx, cy)] = own = new List<int>();
                    own.Add(i);
                }
            }

            var byRoot = new Dictionary<int, List<int>>();
            var groups = new List<List<int>>();
            for (var i = 0; i < n; i++)
            {
                var r = Find(i);
                if (!byRoot.TryGetValue(r, out var g)) { byRoot[r] = g = new List<int>(); groups.Add(g); }
                g.Add(i);
            }
            return groups;
        }

        /// <summary>The middle of a group's points (the badge goes there).</summary>
        public static (double X, double Y) Centre(IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int> members)
        {
            double x = 0, y = 0;
            foreach (var i in members) { x += points[i].X; y += points[i].Y; }
            return members.Count == 0 ? (0, 0) : (x / members.Count, y / members.Count);
        }

        /// <summary>A count badge's text: "4"; above 99, "99+".</summary>
        public static string CountLabel(int count) => count > 99 ? "99+" : Math.Max(0, count).ToString(CultureInfo.InvariantCulture);

        /// <summary>A numbered badge's text: its number; "3+" when it stands for more than one numbered mark (the lowest
        /// number shows).</summary>
        public static string NumberLabel(int lowest, bool more) => lowest.ToString(CultureInfo.InvariantCulture) + (more ? "+" : "");
    }
}
