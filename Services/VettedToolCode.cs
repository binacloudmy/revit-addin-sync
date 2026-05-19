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

        internal static string BuildRenameElements(IDictionary<string, object> p) => null;
        internal static string BuildSetParameter(IDictionary<string, object> p) => null;
        internal static string BuildExportSchedule(IDictionary<string, object> p) => null;
    }
}
