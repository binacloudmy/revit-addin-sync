using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>
    /// compare_levels — the ONE tested Level Visualiser (CIDB #1 / DEV-01).
    ///
    /// Eight CIDB rounds (43-50) tried to do this via model-generated C#. Every
    /// round failed DIFFERENTLY on identical source: one-to-many matching, a
    /// leftover level both consumed and re-emitted as ONLY_LINK (12 rows for 11
    /// levels), a describe-without-executing non-answer, stale/leaked output.
    /// The comparison itself is deterministic and simple — so, like
    /// extract_cad_geometry did for CAD reading, this compiled tool replaces the
    /// codegen entirely. The model calls it; the result frame IS the answer.
    ///
    /// Matching: exact name (case-insensitive) first, then elevation fallback
    /// (|Δ| ≤ 5mm) for the leftovers, nearest-Δ first. Each link level is
    /// consumed at most once (a HashSet of used link ids) — the row count always
    /// equals host_count + unmatched_link_count, never more.
    /// </summary>
    internal static class LevelCompare
    {
        private const double NameTolFt = 5.0 / 304.8;    // 5mm — same-elevation tolerance
        private const double MmPerFt = 304.8;

        private static Dictionary<string, object?> Err(string message)
            => new Dictionary<string, object?> { ["ok"] = false, ["error"] = message };

        private sealed class Lvl
        {
            public long Id;
            public string Name = "";
            public double Ft;
            public double Mm => Math.Round(Ft * MmPerFt, 1);
        }

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            string? linkFilter = ArgsHelp.GetString(args, "link_name");
            bool writeCsv = ArgsHelp.GetBool(args, "write_csv") ?? true;

            // ── 1. resolve the linked document ──────────────────────────────
            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            if (links.Count == 0)
                return Err("no Revit links in the model — link the structural model first (Insert > Link Revit)");

            RevitLinkInstance? chosen = null;
            if (!string.IsNullOrWhiteSpace(linkFilter))
            {
                chosen = links.FirstOrDefault(l =>
                    (doc.GetElement(l.GetTypeId())?.Name ?? "")
                        .IndexOf(linkFilter, StringComparison.OrdinalIgnoreCase) >= 0
                    || l.Name.IndexOf(linkFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                if (chosen == null)
                    return Err($"no link matches '{linkFilter}'. Links present: "
                        + string.Join(", ", links.Select(l => doc.GetElement(l.GetTypeId())?.Name ?? l.Name)));
            }
            else
            {
                var loaded = links.Where(l => l.GetLinkDocument() != null).ToList();
                if (loaded.Count == 0)
                    return Err("no link is currently loaded — reload the structural link in Manage Links");
                if (loaded.Count > 1)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["ambiguous"] = true,
                        ["links"] = loaded.Select(l => doc.GetElement(l.GetTypeId())?.Name ?? l.Name).ToList(),
                        ["note"] = "several links loaded — call again with link_name",
                    };
                chosen = loaded[0];
            }

            var linkDoc = chosen.GetLinkDocument();
            if (linkDoc == null)
                return Err("the chosen link is not loaded (status Not Found / Unloaded) — reload it in Manage Links");
            string linkName = doc.GetElement(chosen.GetTypeId())?.Name ?? chosen.Name;

            // ── 2. collect levels from both documents ───────────────────────
            List<Lvl> Collect(Document d) => new FilteredElementCollector(d)
                .OfClass(typeof(Level)).Cast<Level>()
                .Select(l => new Lvl { Id = l.Id.Value, Name = l.Name, Ft = l.Elevation })
                .OrderBy(l => l.Ft).ToList();

            var host = Collect(doc);
            var link = Collect(linkDoc);
            if (host.Count == 0) return Err("host model has no levels");
            if (link.Count == 0) return Err($"linked model '{linkName}' has no levels");

            // ── 3. match: name first, then elevation fallback; 1:1 ──────────
            var usedLink = new HashSet<long>();
            var rows = new List<Dictionary<string, object?>>();
            int match = 0, matchDiffName = 0, diffElev = 0, onlyHost = 0;

            Lvl? TakeByName(string name) => link.FirstOrDefault(
                lk => !usedLink.Contains(lk.Id)
                   && string.Equals(lk.Name, name, StringComparison.OrdinalIgnoreCase));

            Lvl? TakeByElev(double ft) => link
                .Where(lk => !usedLink.Contains(lk.Id) && Math.Abs(lk.Ft - ft) <= NameTolFt)
                .OrderBy(lk => Math.Abs(lk.Ft - ft)).FirstOrDefault();

            void AddRow(string status, Lvl? h, Lvl? lk)
                => rows.Add(new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["host_level"] = h?.Name,
                    ["host_elev_mm"] = h?.Mm,
                    ["link_level"] = lk?.Name,
                    ["link_elev_mm"] = lk?.Mm,
                    ["delta_mm"] = (h != null && lk != null) ? (object)Math.Round((lk.Ft - h.Ft) * MmPerFt, 1) : null,
                });

            foreach (var h in host)
            {
                var byName = TakeByName(h.Name);
                if (byName != null)
                {
                    usedLink.Add(byName.Id);
                    if (Math.Abs(byName.Ft - h.Ft) <= NameTolFt) { match++; AddRow("MATCH", h, byName); }
                    else { diffElev++; AddRow("DIFF_ELEV", h, byName); }
                    continue;
                }
                var byElev = TakeByElev(h.Ft);
                if (byElev != null)
                {
                    usedLink.Add(byElev.Id);
                    matchDiffName++;
                    AddRow("MATCH_DIFF_NAME", h, byElev);
                    continue;
                }
                onlyHost++;
                AddRow("ONLY_HOST", h, null);
            }

            // leftover link levels — consumed set guarantees no double-listing
            var onlyLink = link.Where(lk => !usedLink.Contains(lk.Id)).ToList();
            foreach (var lk in onlyLink) AddRow("ONLY_LINK", null, lk);

            // ── 4. write the CSV (real file on disk, verified) ──────────────
            string? csvPath = null; bool csvOk = false; string? csvErr = null;
            if (writeCsv)
            {
                try
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    csvPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "Level_Comparison_" + stamp + ".csv");
                    var sb = new StringBuilder();
                    sb.AppendLine("Status,Host Level,Host Elev (mm),Link Level,Link Elev (mm),Delta (mm)");
                    foreach (var r in rows)
                    {
                        string C(object? o) => o == null ? "" : o.ToString() ?? "";
                        string Q(object? o) => "\"" + C(o).Replace("\"", "\"\"") + "\"";
                        sb.AppendLine(string.Join(",",
                            Q(r["status"]), Q(r["host_level"]), C(r["host_elev_mm"]),
                            Q(r["link_level"]), C(r["link_elev_mm"]), C(r["delta_mm"])));
                    }
                    File.WriteAllText(csvPath, sb.ToString());
                    csvOk = File.Exists(csvPath);
                }
                catch (Exception ex) { csvErr = ex.Message; csvPath = null; }
            }

            // ── 5. structured result — the frame IS the answer ──────────────
            int hostCount = host.Count, linkCount = link.Count;
            string headline =
                $"Levels: {match} match, {matchDiffName} match (diff name), {diffElev} elev diff, "
                + $"{onlyHost} only in host, {onlyLink.Count} only in link "
                + $"(host {hostCount} vs {linkName} {linkCount}).";

            // invariant proof: row count == host + only-link, never more
            int expectRows = hostCount + onlyLink.Count;

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["link_name"] = linkName,
                ["host_level_count"] = hostCount,
                ["link_level_count"] = linkCount,
                ["match"] = match,
                ["match_diff_name"] = matchDiffName,
                ["diff_elev"] = diffElev,
                ["only_host"] = onlyHost,
                ["only_link"] = onlyLink.Count,
                ["rows"] = rows,
                ["row_count"] = rows.Count,
                ["rows_balanced"] = rows.Count == expectRows,
                ["csv_path"] = csvOk ? csvPath : null,
                ["csv_written"] = csvOk,
                ["csv_error"] = csvErr,
                ["headline"] = headline,
            };
        }
    }
}
