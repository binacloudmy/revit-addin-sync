// BinaVibe.Naming — rename planner, Revit-free (bina-ai R2 Task 21, naming pack).
//
// rename_elements previews must show EXACT old→new pairs AND the collisions
// Revit would refuse, BEFORE any transaction; the apply path skips those
// collisions up front instead of "try and see". Both paths build this plan
// from the same (id, name) list so preview and apply can never disagree.
//
// Collision rules:
//   * the target name already exists among the scope's names and that element
//     is NOT itself being renamed away  → "name already exists";
//   * two sources map to the same target → the second is a "duplicate target".

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Naming
{
    public sealed class RenameRow
    {
        public long Id { get; init; }
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public string? Reason { get; init; }   // null = will rename; else collision reason
    }

    public sealed class RenamePlan
    {
        public IReadOnlyList<RenameRow> Renames { get; }
        public IReadOnlyList<RenameRow> Collisions { get; }
        public int WouldRename => Renames.Count;

        private RenamePlan(List<RenameRow> renames, List<RenameRow> collisions)
        {
            Renames = renames;
            Collisions = collisions;
        }

        public static RenamePlan Build(IEnumerable<(long id, string name)> scope, string find, string replace)
        {
            var items = scope.Where(s => s.name != null).ToList();
            if (string.IsNullOrEmpty(find))
                return new RenamePlan(new List<RenameRow>(), new List<RenameRow>());

            // candidates: name contains `find`, and the result is a real new name
            var candidates = new List<(long id, string from, string to)>();
            foreach (var (id, name) in items)
            {
                if (!name.Contains(find)) continue;
                var to = name.Replace(find, replace ?? "");
                if (to == name || string.IsNullOrWhiteSpace(to)) continue;
                candidates.Add((id, name, to));
            }
            var renamedAway = new HashSet<string>(candidates.Select(c => c.from), StringComparer.Ordinal);
            var existing = new HashSet<string>(items.Select(i => i.name), StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            var renames = new List<RenameRow>();
            var collisions = new List<RenameRow>();
            foreach (var (id, from, to) in candidates)
            {
                if (existing.Contains(to) && !renamedAway.Contains(to))
                {
                    collisions.Add(new RenameRow { Id = id, From = from, To = to, Reason = "name already exists" });
                    continue;
                }
                if (!claimed.Add(to))
                {
                    collisions.Add(new RenameRow { Id = id, From = from, To = to, Reason = "duplicate target: another element renames to the same name" });
                    continue;
                }
                renames.Add(new RenameRow { Id = id, From = from, To = to });
            }
            return new RenamePlan(renames, collisions);
        }

        /// <summary>Wire shape for rename_elements dry_run. Counts are exact even when the row list is capped.</summary>
        public Dictionary<string, object?> ToPreview(int cap = 200, string? scope = null)
        {
            var rows = Renames.Take(cap).Select(r => (object)new Dictionary<string, object?>
                { ["id"] = r.Id, ["from"] = r.From, ["to"] = r.To, ["kind"] = "" }).ToList();
            var colls = Collisions.Take(cap).Select(r => (object)new Dictionary<string, object?>
                { ["id"] = r.Id, ["from"] = r.From, ["to"] = r.To, ["reason"] = r.Reason }).ToList();
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dry_run"] = true,
                ["scope"] = scope,
                ["would_rename"] = WouldRename,
                ["preview"] = rows,
                ["preview_truncated"] = Renames.Count > cap || Collisions.Count > cap,
                ["collisions"] = colls,
                ["collision_count"] = Collisions.Count,
                ["nothing"] = WouldRename == 0,
                ["headline"] = WouldRename + " name(s) would change (nothing renamed yet)"
                               + (Collisions.Count > 0 ? $", {Collisions.Count} collision(s) will be skipped" : ""),
            };
        }
    }
}
