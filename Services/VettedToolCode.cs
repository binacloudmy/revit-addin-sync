using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Pure, Revit-free synthesizers for SP3a's vetted tools. Primitive
    /// signatures (no RouteAction → no Newtonsoft) so the Tests project can
    /// compile-link this file, exactly like AiUrl.cs (AB1 lesson).
    /// Each Build* returns runnable C# (executor auto-wraps the transaction)
    /// or null when required params are missing → caller falls through.
    /// </summary>
    internal static class VettedToolCode
    {
        internal static string Get(IDictionary<string, object> p, params string[] keys)
        {
            if (p == null) return null;
            foreach (var k in keys)
            {
                if (p.TryGetValue(k, out var v) && v != null)
                {
                    var s = v.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            return null;
        }

        internal static bool IsAutoRunSafe(string tool, string type)
        {
            if (!string.IsNullOrEmpty(tool))
                return string.Equals(tool, "open_view", StringComparison.OrdinalIgnoreCase);
            return string.Equals(type, "open_view", StringComparison.OrdinalIgnoreCase);
        }

        internal static string TryBuild(string tool, IDictionary<string, object> p)
        {
            if (string.IsNullOrEmpty(tool)) return null;
            switch (tool.ToLowerInvariant())
            {
                case "rename_elements": return BuildRenameElements(p);
                case "set_parameter":  return BuildSetParameter(p);
                case "export_schedule": return BuildExportSchedule(p);
                default: return null;
            }
        }

        // Strip characters that would break the emitted C# string literal.
        private static string Lit(string s) =>
            (s ?? "").Replace("\\", "").Replace("\"", "");

        internal static string BuildRenameElements(IDictionary<string, object> p)
        {
            var cat = Get(p, "target_category", "category");
            var find = Get(p, "find");
            var repl = Get(p, "replace");
            var scope = Get(p, "scope");
            if (cat == null || find == null || repl == null) return null;
            string c = Lit(cat), f = Lit(find), r = Lit(repl), sc = Lit(scope);
            var sb = new StringBuilder();
            sb.AppendLine($"var __cat = doc.Settings.Categories.Cast<Category>()");
            sb.AppendLine($"    .FirstOrDefault(x => x != null && string.Equals(x.Name, \"{c}\", StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine("var __els = __cat == null ? new List<Element>() : new FilteredElementCollector(doc)");
            sb.AppendLine("    .OfCategoryId(__cat.Id).WhereElementIsNotElementType().Cast<Element>().ToList();");
            if (!string.IsNullOrEmpty(sc))
            {
                sb.AppendLine($"var __lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()");
                sb.AppendLine($"    .FirstOrDefault(l => string.Equals(l.Name, \"{sc}\", StringComparison.OrdinalIgnoreCase));");
                sb.AppendLine("if (__lvl != null) __els = __els.Where(e => e.LevelId == __lvl.Id).ToList();");
            }
            sb.AppendLine("int __n = 0;");
            sb.AppendLine("foreach (var __e in __els) {");
            sb.AppendLine("  try {");
            sb.AppendLine("    var __o = __e.Name;");
            sb.AppendLine($"    if (!string.IsNullOrEmpty(__o) && __o.IndexOf(\"{f}\", StringComparison.Ordinal) >= 0) {{");
            sb.AppendLine($"      __e.Name = __o.Replace(\"{f}\", \"{r}\"); __n++;");
            sb.AppendLine("    }");
            sb.AppendLine("  } catch { }");
            sb.AppendLine("}");
            sb.AppendLine($"ShowMessage(\"Renamed\", __n + \" {c} element(s)\");");
            return sb.ToString();
        }
        internal static string BuildSetParameter(IDictionary<string, object> p) => null;
        internal static string BuildExportSchedule(IDictionary<string, object> p) => null;
    }
}
