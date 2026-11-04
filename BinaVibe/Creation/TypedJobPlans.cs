// BinaVibe.Creation — Revit-free planners for the typed "frequent job" tools
// (AI-223 / P3.4): a whole grid system in one call, and a sheet per plan view
// with its viewport in one call. Both rows (S-05, D-01) used to loop through
// generated C#; the plan below is what the tool would make, computed before
// any transaction so dry_run and the real run agree. Lengths in millimetres.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BinaVibe.Creation
{
    public static class GridSystemPlan
    {
        public const int MaxLines = 100;
        public const double MaxSpacingMm = 100_000;

        public sealed class GridLine
        {
            public string Name { get; init; } = "";
            /// <summary>"vertical" (runs along Y, spaced along X) or "horizontal".</summary>
            public string Direction { get; init; } = "";
            public double X1 { get; init; }
            public double Y1 { get; init; }
            public double X2 { get; init; }
            public double Y2 { get; init; }
            public double LengthMm => Math.Round(Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)), 1);
        }

        /// <summary>An existing grid, for name clashes and the default origin.</summary>
        public sealed class ExistingGrid
        {
            public string Name { get; init; } = "";
            public double X1 { get; init; }
            public double Y1 { get; init; }
            public double X2 { get; init; }
            public double Y2 { get; init; }
        }

        public sealed class Result
        {
            public List<GridLine> Lines { get; } = new();
            /// <summary>Requested names already used by a grid in the model.</summary>
            public List<string> NamesTaken { get; } = new();
            public List<string> Errors { get; } = new();
            public double OriginX { get; init; }
            public double OriginY { get; init; }
            /// <summary>True when origin_mm was not given and the plan moved
            /// clear of the existing grids.</summary>
            public bool OriginAuto { get; init; }
            public bool Ok => Errors.Count == 0 && NamesTaken.Count == 0 && Lines.Count > 0;
        }

        /// <summary>"1-5" → 1,2,3,4,5; "A-E" → A..E; "A,B" → A,B; anything else
        /// stays as written. Lists may mix ranges and names.</summary>
        public static List<string> ExpandLabels(IEnumerable<string>? raw)
        {
            var outList = new List<string>();
            if (raw == null) return outList;
            foreach (var item0 in raw)
            {
                if (string.IsNullOrWhiteSpace(item0)) continue;
                foreach (var part0 in item0.Split(','))
                {
                    var part = part0.Trim();
                    if (part.Length == 0) continue;
                    var r = TryRange(part);
                    if (r != null) outList.AddRange(r);
                    else outList.Add(part);
                    if (outList.Count > MaxLines) return outList;
                }
            }
            return outList;
        }

        private static List<string>? TryRange(string s)
        {
            // Malay "hingga"/"to" are handled by the caller's wording; here only a dash.
            var dash = s.IndexOf('-', 1);
            if (dash <= 0 || dash == s.Length - 1) return null;
            var a = s.Substring(0, dash).Trim();
            var b = s.Substring(dash + 1).Trim();
            if (int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var ia)
                && int.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var ib))
            {
                if (ib < ia || ib - ia >= MaxLines) return null;
                return Enumerable.Range(ia, ib - ia + 1).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
            }
            if (a.Length == 1 && b.Length == 1 && char.IsLetter(a[0]) && char.IsLetter(b[0])
                && char.IsUpper(a[0]) == char.IsUpper(b[0]) && b[0] >= a[0])
            {
                var list = new List<string>();
                for (var c = a[0]; c <= b[0]; c++) list.Add(c.ToString());
                return list;
            }
            return null;
        }

        /// <summary>Vertical lines (named <paramref name="vertical"/>) sit at
        /// x = origin + i·spacingX and run past the horizontal set by
        /// <paramref name="overhangMm"/>; horizontal lines likewise along Y.
        /// Origin omitted + grids already in the model → the new system starts
        /// 10 m clear of their extents (to the right), so it never lands on
        /// top of the building.</summary>
        public static Result Build(
            IList<string> vertical, IList<string> horizontal,
            double spacingXmm, double spacingYmm,
            double? originXmm, double? originYmm, double overhangMm,
            IEnumerable<ExistingGrid> existing)
        {
            var ex = (existing ?? Array.Empty<ExistingGrid>()).ToList();
            bool auto = !originXmm.HasValue || !originYmm.HasValue;
            double ox = originXmm ?? 0, oy = originYmm ?? 0;
            if (auto && ex.Count > 0)
            {
                ox = Math.Round(ex.Max(g => Math.Max(g.X1, g.X2)) + 10_000, 0);
                oy = Math.Round(ex.Min(g => Math.Min(g.Y1, g.Y2)), 0);
            }
            var res = new Result { OriginX = ox, OriginY = oy, OriginAuto = auto && ex.Count > 0 };

            vertical ??= new List<string>();
            horizontal ??= new List<string>();
            if (vertical.Count + horizontal.Count == 0) res.Errors.Add("no grid names given");
            if (vertical.Count + horizontal.Count > MaxLines) res.Errors.Add($"more than {MaxLines} grid lines");
            if (vertical.Count > 1 && !(spacingXmm > 0 && spacingXmm <= MaxSpacingMm))
                res.Errors.Add("spacing along X must be between 0 and 100000 mm");
            if (horizontal.Count > 1 && !(spacingYmm > 0 && spacingYmm <= MaxSpacingMm))
                res.Errors.Add("spacing along Y must be between 0 and 100000 mm");
            if (overhangMm < 0 || overhangMm > 20_000) res.Errors.Add("overhang_mm must be 0 to 20000");

            var all = vertical.Concat(horizontal).ToList();
            var dup = all.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dup.Count > 0) res.Errors.Add("names repeated in the request: " + string.Join(", ", dup));
            if (res.Errors.Count > 0) return res;

            var taken = new HashSet<string>(ex.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var n in all) if (taken.Contains(n)) res.NamesTaken.Add(n);

            // Lines must have length even when the other direction is empty.
            double spanX = Math.Max(0, vertical.Count - 1) * spacingXmm;
            double spanY = Math.Max(0, horizontal.Count - 1) * spacingYmm;
            double ext = Math.Max(overhangMm, 1000);
            for (int i = 0; i < vertical.Count; i++)
            {
                double x = ox + i * spacingXmm;
                res.Lines.Add(new GridLine { Name = vertical[i], Direction = "vertical",
                    X1 = x, Y1 = oy - ext, X2 = x, Y2 = oy + spanY + ext });
            }
            for (int j = 0; j < horizontal.Count; j++)
            {
                double y = oy + j * spacingYmm;
                res.Lines.Add(new GridLine { Name = horizontal[j], Direction = "horizontal",
                    X1 = ox - ext, Y1 = y, X2 = ox + spanX + ext, Y2 = y });
            }
            return res;
        }
    }

    public static class SheetSetPlan
    {
        public const int MaxSheets = 200;

        public sealed class PlanView
        {
            public string Name { get; init; } = "";
            public long Id { get; init; }
            public double LevelElevation { get; init; }
            /// <summary>Number of the sheet the view already sits on, or null.</summary>
            public string? OnSheet { get; init; }
            /// <summary>Why Revit would refuse to place this view (empty view,
            /// CanAddViewToSheet false), or null when it can go on a sheet.
            /// Checked before the card so the card never promises a sheet
            /// that the apply would delete again.</summary>
            public string? Unplaceable { get; init; }
        }

        public sealed class Match
        {
            public List<long> Ids { get; } = new();
            public List<string> Missing { get; } = new();
            /// <summary>Requested name → the ids of every plan with that name
            /// (case-insensitive). Revit allows "Level 1" and "LEVEL 1", and
            /// area plans repeat names across schemes.</summary>
            public Dictionary<string, List<long>> Ambiguous { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Resolve requested view names against the candidate plans.
        /// A name matching more than one plan is ambiguous (ask, never pick).</summary>
        public static Match MatchViews(IEnumerable<(string name, long id)> candidates, IEnumerable<string> wanted)
        {
            var m = new Match();
            var byName = (candidates ?? Array.Empty<(string, long)>())
                .GroupBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(c => c.id).ToList(), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<long>();
            foreach (var w0 in wanted ?? Array.Empty<string>())
            {
                var w = (w0 ?? "").Trim();
                if (w.Length == 0) continue;
                if (!byName.TryGetValue(w, out var ids)) { m.Missing.Add(w); continue; }
                if (ids.Count > 1) { m.Ambiguous[w] = ids; continue; }
                if (seen.Add(ids[0])) m.Ids.Add(ids[0]);
            }
            return m;
        }

        public sealed class Row
        {
            public string ViewName { get; init; } = "";
            public long ViewId { get; init; }
            public string SheetNumber { get; init; } = "";
            public string SheetName { get; init; } = "";
        }

        public sealed class Skip
        {
            public string ViewName { get; init; } = "";
            public string Reason { get; init; } = "";
        }

        public sealed class Result
        {
            public List<Row> Rows { get; } = new();
            public List<Skip> Skipped { get; } = new();
            public List<string> Errors { get; } = new();
        }

        /// <summary>One row per view not yet on a sheet, ordered by level then
        /// name. Numbers run prefix + start (zero-padded to
        /// <paramref name="digits"/>), stepping past numbers already taken.
        /// <paramref name="namePattern"/>'s {view} becomes the view name.</summary>
        public static Result Build(
            IEnumerable<PlanView> views, IEnumerable<string> existingNumbers,
            string prefix, int start, int digits, string namePattern)
        {
            var res = new Result();
            prefix ??= "";
            if (start < 0) res.Errors.Add("start_number must be 0 or more");
            if (digits < 1 || digits > 6) res.Errors.Add("digits must be 1 to 6");
            if (string.IsNullOrWhiteSpace(namePattern)) namePattern = "{view}";
            if (res.Errors.Count > 0) return res;

            var taken = new HashSet<string>(existingNumbers ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            int n = start;
            foreach (var v in (views ?? Array.Empty<PlanView>())
                         .OrderBy(v => v.LevelElevation).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(v.OnSheet))
                {
                    res.Skipped.Add(new Skip { ViewName = v.Name, Reason = "already on sheet " + v.OnSheet });
                    continue;
                }
                if (!string.IsNullOrEmpty(v.Unplaceable))
                {
                    res.Skipped.Add(new Skip { ViewName = v.Name, Reason = v.Unplaceable! });
                    continue;
                }
                if (res.Rows.Count >= MaxSheets)
                {
                    res.Skipped.Add(new Skip { ViewName = v.Name, Reason = $"over the {MaxSheets}-sheet limit" });
                    continue;
                }
                string number;
                do { number = prefix + n.ToString("D" + digits, CultureInfo.InvariantCulture); n++; }
                while (taken.Contains(number));
                taken.Add(number);
                res.Rows.Add(new Row
                {
                    ViewName = v.Name, ViewId = v.Id, SheetNumber = number,
                    SheetName = namePattern.Replace("{view}", v.Name),
                });
            }
            return res;
        }
    }
}
