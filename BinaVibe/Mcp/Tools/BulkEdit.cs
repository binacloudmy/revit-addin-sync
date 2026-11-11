// BulkEdit: category (+ level) target resolution for the copilot set_parameters tool.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class BulkEdit
    {
        private static List<Element> Targets(Document doc, JsonElement args, out Dictionary<string, object?>? error)
        {
            error = null;
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var predicate = ArgsHelp.GetString(args, "predicate");
            var bic = Inspectors.ResolveCategoryRobust(doc, category);
            if (bic == null)
            {
                error = new() { ["ok"] = false, ["error"] = $"category '{category}' not recognised" };
                return new List<Element>();
            }
            // A level name that exists nowhere is a refusal with candidates,
            // never a silent zero (gate 2 row 4: "Aras 2" vs "Aras 02" sent
            // the model off-lane).
            var lm = predicate == null ? null : System.Text.RegularExpressions.Regex.Match(predicate, @"(?:^|[;&,]\s*)level\s*=\s*([^;&,]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (lm != null && lm.Success)
            {
                var want = lm.Groups[1].Value.Trim();
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
                if (!levels.Any(n => string.Equals(n, want, StringComparison.OrdinalIgnoreCase)))
                {
                    error = new() { ["ok"] = false, ["error"] = $"level '{want}' not found", ["candidates"] = levels };
                    return new List<Element>();
                }
            }
            return new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType()
                .Where(e => Inspectors.PredicateMatches(e, doc, predicate)).ToList();
        }

        // ---- copilot set_parameters entry ----
        // Entry points for BinaVibe.Copilot.Tools.SetParametersTool (lean harness
        // W3, ruling R2: append-only). The copilot's set_parameters resolves a
        // category (+ level) target set exactly as set_parameter_by_filter does,
        // with the same "level not found -> candidates" refusal, and names
        // elements the same way.

        /// <summary>Host instances of <paramref name="category"/>, optionally on the level named
        /// <paramref name="level"/>. On a refusal returns an empty list and sets
        /// <paramref name="error"/> (the legacy dict: error text, optional candidates).</summary>
        internal static List<Element> CopilotTargets(Document doc, string category, string level, out Dictionary<string, object> error)
        {
            var args = new Dictionary<string, string> { ["category"] = category };
            if (!string.IsNullOrWhiteSpace(level)) args["predicate"] = "level=" + level;
            return Targets(doc, JsonSerializer.SerializeToElement(args), out error);
        }
    }
}
