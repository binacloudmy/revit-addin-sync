using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>What a spec check tests — the four aspects of model-audit-spec.md §0.2 / §D
    /// (nama, maklumat-parameter, kualiti-integriti, design geometri).</summary>
    public enum SpecAspect { Naming, Parameters, Quality, Geometry }

    /// <summary>
    /// The Result screen's "Aspects" view: the same 61 spec checks as the Borang
    /// sections, grouped N / P / Q / G by what they test. Rows are built exactly as
    /// section rows are (<see cref="SpecChecklist.Group"/>). WPF- and Revit-free.
    /// </summary>
    public static class SpecAspects
    {
        // DRAFT mapping — to be confirmed by Ashraf (spec Q1).
        // One entry per SpecCatalog check id. A check missing here fails
        // SpecAspectsTests (coverage); at runtime an unknown id falls back to Quality.
        public static readonly IReadOnlyDictionary<string, SpecAspect> Table = Build(
            // N — names: file name / model code, view and link names, type and material names.
            (SpecAspect.Naming, new[] { "A2.1", "A2.2", "A7.2", "C1.1.2", "C5.1.2", "Da.1", "Dd.1" }),
            // P — information filled in: parameters, Project Information, title block data, materials and rooms recorded.
            (SpecAspect.Parameters, new[] { "A6.1", "A6.3", "A7.1", "A7.3", "B1.1", "B1.2", "B1.3", "C1.6.3", "C4.2.2", "Db.1", "Db.2", "De.1" }),
            // Q — model quality and integrity: structure and links, views / schedules / sheets present and right, templates.
            (SpecAspect.Quality, new[]
            {
                "A3.1", "A3.2", "A3.3", "A3.4", "A3.5", "A6.2",
                "C1.0.1", "C1.1.1", "C1.1.3", "C1.2.1", "C1.3.1", "C1.4.1", "C1.5.1", "C1.5.2",
                "C1.6.1", "C1.6.2", "C2.1.1", "C3.1.1", "C3.1.2",
                "C4.1.1", "C4.1.2", "C4.1.3", "C4.2.1", "C4.2.3", "C5.1.1", "C5.1.3",
                "Dd.2", "De.2", "De.3", "E.1",
            }),
            // G — design and geometry: Need Statement design (A1), position, grids and levels, sizes in names vs geometry.
            (SpecAspect.Geometry, new[] { "A1.1", "A1.2", "A1.3", "A1.4", "A1.5", "A1.6", "A1.7", "A4.1", "A5.1", "A5.2", "A5.3", "Dc.1" }));

        public static readonly IReadOnlyList<SpecAspect> All =
            new[] { SpecAspect.Naming, SpecAspect.Parameters, SpecAspect.Quality, SpecAspect.Geometry };

        private static IReadOnlyDictionary<string, SpecAspect> Build(params (SpecAspect Aspect, string[] Ids)[] groups)
        {
            var d = new Dictionary<string, SpecAspect>(StringComparer.Ordinal);
            foreach (var (aspect, ids) in groups)
                foreach (var id in ids) d.Add(id, aspect);   // a duplicate id throws: one aspect per check
            return d;
        }

        public static bool IsMapped(string checkId) => checkId != null && Table.ContainsKey(checkId);

        /// <summary>The aspect of a spec check; Quality for an id the table does not know.</summary>
        public static SpecAspect Of(string checkId) =>
            checkId != null && Table.TryGetValue(checkId, out var a) ? a : SpecAspect.Quality;

        public static string Letter(SpecAspect a)
        {
            switch (a)
            {
                case SpecAspect.Naming: return "N";
                case SpecAspect.Parameters: return "P";
                case SpecAspect.Geometry: return "G";
                default: return "Q";
            }
        }

        public static string Name(SpecAspect a, string lang)
        {
            var en = string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase);
            switch (a)
            {
                case SpecAspect.Naming: return en ? "Naming" : "Penamaan";
                case SpecAspect.Parameters: return en ? "Parameters and information" : "Parameter dan maklumat";
                case SpecAspect.Geometry: return en ? "Design geometry" : "Geometri reka bentuk";
                default: return en ? "Quality and integrity" : "Kualiti dan integriti";
            }
        }

        /// <summary>The aspect rows N / P / Q / G (the "Aspects" view).</summary>
        public static List<GroupRow> Rows(SpecReport r, string lang) =>
            r == null ? new List<GroupRow>()
                : All.Select(a => SpecChecklist.Group(r, Letter(a), Letter(a), Name(a, lang), c => Of(c.Id) == a, lang)).ToList();
    }
}
