// BinaVibe.Documentation — planners for tags and schedules, Revit-free
// (bina-ai R2 Task 25, documentation family).
//
// tag_all_in_view: which elements in the active view would be tagged and why
// the rest are skipped (already tagged / grouped / no location), plus the
// risk of a missing tag family; after commit, the tagged set is re-read.
// create_schedule: requested fields resolved against the schedulable ones
// (exact, case-insensitive, then contains), unresolved named, a unique name
// proposed; after commit, the view and its field count are verified.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Documentation
{
    public sealed class TagRow
    {
        public long Id { get; init; }
        public bool AlreadyTagged { get; init; }
        public bool Grouped { get; init; }
        public bool HasLocation { get; init; } = true;
    }

    public sealed class DocRisk
    {
        public string Category { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Note { get; init; } = "";
    }

    public sealed class TagPlan
    {
        public string Category { get; }
        public IReadOnlyList<long> ToTag { get; }
        public int AlreadyTagged { get; }
        public int GroupedSkipped { get; }
        public int NoLocation { get; }
        public int Matched { get; }
        public bool TagFamilyLoaded { get; }
        public IReadOnlyList<DocRisk> Risks { get; }

        private TagPlan(string cat, List<long> toTag, int already, int grouped, int noLoc, int matched, bool loaded, List<DocRisk> risks)
        { Category = cat; ToTag = toTag; AlreadyTagged = already; GroupedSkipped = grouped; NoLocation = noLoc; Matched = matched; TagFamilyLoaded = loaded; Risks = risks; }

        public static TagPlan Build(string category, IEnumerable<TagRow> rows, bool tagFamilyLoaded)
        {
            var list = rows.ToList();
            var toTag = new List<long>();
            int already = 0, grouped = 0, noLoc = 0;
            foreach (var r in list)
            {
                if (r.AlreadyTagged) { already++; continue; }
                if (r.Grouped) { grouped++; continue; }
                if (!r.HasLocation) { noLoc++; continue; }
                toTag.Add(r.Id);
            }
            var risks = new List<DocRisk>();
            if (!tagFamilyLoaded && toTag.Count > 0)
                risks.Add(new DocRisk { Category = category, Kind = "no_tag_family", Note = $"no tag family for {category} is loaded; tags would fail — load one first" });
            return new TagPlan(category, toTag, already, grouped, noLoc, list.Count, tagFamilyLoaded, risks);
        }

        public Dictionary<string, object?> ToRow() => new()
        {
            ["category"] = Category, ["matched"] = Matched, ["untagged"] = ToTag.Count,
            ["already_tagged"] = AlreadyTagged, ["grouped_skipped"] = GroupedSkipped, ["no_location"] = NoLocation,
            ["tag_family_loaded"] = TagFamilyLoaded,
        };

        public static Dictionary<string, object?> Verify(IEnumerable<long> expected, ISet<long> nowTagged)
        {
            var exp = expected.ToList();
            var mismatches = exp.Where(id => !nowTagged.Contains(id)).Take(50)
                .Select(id => (object)new Dictionary<string, object?> { ["id"] = id, ["expected"] = "tagged", ["actual"] = "untagged" }).ToList();
            return new() { ["expected"] = exp.Count, ["now_tagged"] = exp.Count - mismatches.Count, ["mismatches"] = mismatches };
        }
    }

    public sealed class SchedulePlan
    {
        public string Category { get; }
        public IReadOnlyList<string> Resolved { get; }
        public IReadOnlyList<string> Unresolved { get; }
        public IReadOnlyList<string> AvailableSample { get; }
        public string ProposedName { get; }
        public bool NameExists { get; }
        public bool WouldCreate => Resolved.Count > 0;

        private SchedulePlan(string cat, List<string> resolved, List<string> unresolved, List<string> sample, string name, bool nameExists)
        { Category = cat; Resolved = resolved; Unresolved = unresolved; AvailableSample = sample; ProposedName = name; NameExists = nameExists; }

        public static IReadOnlyList<string> DefaultFields(string category) => category.ToLowerInvariant() switch
        {
            "doors" => new[] { "Mark", "Family and Type", "Width", "Height", "Level" },
            "windows" => new[] { "Mark", "Family and Type", "Width", "Height", "Level" },
            "rooms" => new[] { "Number", "Name", "Area", "Level" },
            "walls" => new[] { "Type", "Length", "Area", "Base Constraint" },
            _ => new[] { "Family and Type", "Level", "Comments" },
        };

        public static SchedulePlan Build(string category, IEnumerable<string>? requested, IEnumerable<string> available, IEnumerable<string> existingNames)
        {
            var avail = available.Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
            var wanted = (requested?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new List<string>());
            if (wanted.Count == 0) wanted = DefaultFields(category).ToList();
            var resolved = new List<string>();
            var unresolved = new List<string>();
            foreach (var f in wanted)
            {
                var key = avail.FirstOrDefault(a => string.Equals(a, f, StringComparison.Ordinal))
                       ?? avail.FirstOrDefault(a => string.Equals(a, f, StringComparison.OrdinalIgnoreCase))
                       ?? avail.FirstOrDefault(a => a.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                if (key != null && !resolved.Contains(key)) resolved.Add(key);
                else if (key == null) unresolved.Add(f);
            }
            var baseName = SingularTitle(category) + " Schedule";
            var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
            var name = baseName;
            for (int i = 2; names.Contains(name); i++) name = $"{baseName} {i}";
            return new SchedulePlan(category, resolved, unresolved, avail.Take(20).ToList(), name, names.Contains(baseName));
        }

        private static string SingularTitle(string category)
        {
            var c = category.Trim();
            if (c.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !c.EndsWith("ss", StringComparison.OrdinalIgnoreCase)) c = c.Substring(0, c.Length - 1);
            return c;
        }

        public Dictionary<string, object?> ToPreview() => new()
        {
            ["ok"] = true,
            ["dry_run"] = true,
            ["category"] = Category,
            ["name"] = ProposedName,
            ["name_exists"] = NameExists,
            ["fields"] = new Dictionary<string, object?> { ["resolved"] = Resolved.ToList(), ["unresolved"] = Unresolved.ToList() },
            ["available_sample"] = AvailableSample.ToList(),
            ["would_create"] = WouldCreate,
            ["headline"] = WouldCreate
                ? $"would create '{ProposedName}' with {Resolved.Count} field(s)" + (Unresolved.Count > 0 ? $"; {Unresolved.Count} field name(s) not found" : "") + " — nothing created yet"
                : "cannot create: none of the requested fields exist on this category",
        };
    }
}
