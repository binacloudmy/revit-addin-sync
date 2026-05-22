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
            sb.AppendLine("var __diffs = new List<object>();");
            sb.AppendLine("foreach (var __e in __els) {");
            // Skip group members — renaming them outside group-edit mode throws a hard error.
            sb.AppendLine("  if (__e.GroupId != null && __e.GroupId != ElementId.InvalidElementId) continue;");
            sb.AppendLine("  try {");
            sb.AppendLine("    var __o = __e.Name;");
            sb.AppendLine($"    if (!string.IsNullOrEmpty(__o) && __o.IndexOf(\"{f}\", StringComparison.Ordinal) >= 0) {{");
            sb.AppendLine($"      __e.Name = __o.Replace(\"{f}\", \"{r}\"); __n++;");
            sb.AppendLine("      __diffs.Add(new { from = __o, to = __e.Name });");
            sb.AppendLine("    }");
            sb.AppendLine("  } catch { }");
            sb.AppendLine("}");
            sb.AppendLine($"SetResult(new {{ kind = \"list\", headline = __n + \" {c} element(s) renamed\", diffs = __diffs }});");
            return sb.ToString();
        }
        internal static string BuildSetParameter(IDictionary<string, object> p)
        {
            var cat = Get(p, "target_category", "category");
            var name = Get(p, "parameter_name", "parameter", "param");
            var val = Get(p, "value");
            if (cat == null || name == null || val == null) return null;
            string c = Lit(cat), pn = Lit(name), v = Lit(val);
            var sb = new StringBuilder();
            sb.AppendLine($"var __cat = doc.Settings.Categories.Cast<Category>()");
            sb.AppendLine($"    .FirstOrDefault(x => x != null && string.Equals(x.Name, \"{c}\", StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine("var __els = __cat == null ? new List<Element>() : new FilteredElementCollector(doc)");
            sb.AppendLine("    .OfCategoryId(__cat.Id).WhereElementIsNotElementType().Cast<Element>().ToList();");
            sb.AppendLine("int __n = 0; int __g = 0;");
            sb.AppendLine("foreach (var __e in __els) {");
            // Editing a member of a Revit group outside group-edit mode throws a hard,
            // un-ignorable error ("changes to groups are allowed only in group edit mode")
            // that the transaction failure-handler can't swallow — so skip grouped members.
            sb.AppendLine("  if (__e.GroupId != null && __e.GroupId != ElementId.InvalidElementId) { __g++; continue; }");
            sb.AppendLine($"  var __p = __e.LookupParameter(\"{pn}\");");
            sb.AppendLine("  if (__p == null || __p.IsReadOnly) continue;");
            sb.AppendLine("  try {");
            sb.AppendLine("    switch (__p.StorageType) {");
            sb.AppendLine($"      case StorageType.String: __p.Set(\"{v}\"); __n++; break;");
            sb.AppendLine($"      case StorageType.Integer: {{ if (int.TryParse(\"{v}\", out var __i)) {{ __p.Set(__i); __n++; }} else if (bool.TryParse(\"{v}\", out var __b)) {{ __p.Set(__b ? 1 : 0); __n++; }} break; }}");
            sb.AppendLine($"      case StorageType.Double: {{ if (double.TryParse(\"{v}\", out var __d)) {{ __p.Set(__d); __n++; }} break; }}");
            sb.AppendLine("      default: break;");
            sb.AppendLine("    }");
            sb.AppendLine("  } catch { }");
            sb.AppendLine("}");
            sb.AppendLine($"SetResult(new {{ kind = \"plain\", headline = __n + \" {c} element(s) updated\", sub = \"Set {pn} to {v}\" + (__g > 0 ? \" · \" + __g + \" skipped (in groups)\" : \"\"), grouped = __g }});");
            return sb.ToString();
        }

        /// <summary>
        /// Opt-in variant of set-parameter: ungroups the group instances that contain target
        /// elements, then sets the parameter on everything. DESTRUCTIVE (dissolves those groups)
        /// — only invoked when the user explicitly clicks "Ungroup & apply".
        /// </summary>
        internal static string BuildSetParameterUngroup(IDictionary<string, object> p)
        {
            var cat = Get(p, "target_category", "category");
            var name = Get(p, "parameter_name", "parameter", "param");
            var val = Get(p, "value");
            if (cat == null || name == null || val == null) return null;
            string c = Lit(cat), pn = Lit(name), v = Lit(val);
            var sb = new StringBuilder();
            sb.AppendLine($"var __cat = doc.Settings.Categories.Cast<Category>().FirstOrDefault(x => x != null && string.Equals(x.Name, \"{c}\", StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine("var __els = __cat == null ? new List<Element>() : new FilteredElementCollector(doc).OfCategoryId(__cat.Id).WhereElementIsNotElementType().Cast<Element>().ToList();");
            sb.AppendLine("var __grpIds = __els.Where(e => e.GroupId != null && e.GroupId != ElementId.InvalidElementId).Select(e => e.GroupId).Distinct().ToList();");
            sb.AppendLine("int __ung = 0;");
            sb.AppendLine("foreach (var __gid in __grpIds) { var __grp = doc.GetElement(__gid) as Group; if (__grp != null) { try { __grp.UngroupMembers(); __ung++; } catch { } } }");
            sb.AppendLine("var __els2 = __cat == null ? new List<Element>() : new FilteredElementCollector(doc).OfCategoryId(__cat.Id).WhereElementIsNotElementType().Cast<Element>().ToList();");
            sb.AppendLine("int __n = 0;");
            sb.AppendLine("foreach (var __e in __els2) {");
            sb.AppendLine("  if (__e.GroupId != null && __e.GroupId != ElementId.InvalidElementId) continue;");
            sb.AppendLine($"  var __p = __e.LookupParameter(\"{pn}\"); if (__p == null || __p.IsReadOnly) continue;");
            sb.AppendLine("  try { switch (__p.StorageType) {");
            sb.AppendLine($"    case StorageType.String: __p.Set(\"{v}\"); __n++; break;");
            sb.AppendLine($"    case StorageType.Integer: {{ if (int.TryParse(\"{v}\", out var __i)) {{ __p.Set(__i); __n++; }} else if (bool.TryParse(\"{v}\", out var __b)) {{ __p.Set(__b ? 1 : 0); __n++; }} break; }}");
            sb.AppendLine($"    case StorageType.Double: {{ if (double.TryParse(\"{v}\", out var __d)) {{ __p.Set(__d); __n++; }} break; }}");
            sb.AppendLine("    default: break; } } catch { }");
            sb.AppendLine("}");
            sb.AppendLine($"SetResult(new {{ kind = \"plain\", headline = __n + \" {c} element(s) updated\", sub = \"Set {pn} to {v} · ungrouped \" + __ung + \" group(s)\", grouped = 0 }});");
            return sb.ToString();
        }

        internal static string BuildExportSchedule(IDictionary<string, object> p)
        {
            var name = Get(p, "schedule_name", "name");
            if (name == null) return null;
            var fmt = (Get(p, "format") ?? "csv").ToLowerInvariant();
            bool xlsx = fmt.Contains("xls");
            var outPath = Get(p, "output_path");
            string n = Lit(name);
            string file = (n.Replace(" ", "_")) + (xlsx ? ".xlsx" : ".csv");
            var sb = new StringBuilder();
            sb.AppendLine($"var __s = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()");
            sb.AppendLine($"    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, \"{n}\", StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine($"if (__s == null) __s = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()");
            sb.AppendLine($"    .FirstOrDefault(v => !v.IsTemplate && v.Name != null && v.Name.IndexOf(\"{n}\", StringComparison.OrdinalIgnoreCase) >= 0);");
            sb.AppendLine($"if (__s == null) {{ ShowMessage(\"Not found\", \"No schedule matching '{n}'.\"); }}");
            sb.AppendLine("else {");
            sb.AppendLine("  var __b = __s.GetTableData().GetSectionData(SectionType.Body);");
            sb.AppendLine("  var __data = new List<List<string>>();");
            sb.AppendLine("  for (int __r = 0; __r < __b.NumberOfRows; __r++) {");
            sb.AppendLine("    var __row = new List<string>();");
            sb.AppendLine("    for (int __col = 0; __col < __b.NumberOfColumns; __col++)");
            sb.AppendLine("      __row.Add(__s.GetCellText(SectionType.Body, __r, __col) ?? \"\");");
            sb.AppendLine("    __data.Add(__row);");
            sb.AppendLine("  }");
            if (!string.IsNullOrEmpty(outPath))
                sb.AppendLine($"  var __path = @\"{outPath.Replace("\"", "\"\"")}\";");
            else
                sb.AppendLine($"  var __path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), \"{file}\");");
            if (xlsx)
            {
                sb.AppendLine("  var __hdr = __data.Count > 0 ? __data[0] : new List<string>();");
                sb.AppendLine("  var __rows = __data.Count > 1 ? __data.Skip(1).ToList() : new List<List<string>>();");
                sb.AppendLine("  WriteExcel(__path, __hdr, __rows);");
            }
            else
            {
                sb.AppendLine("  System.IO.File.WriteAllLines(__path, __data.Select(rw =>");
                sb.AppendLine("    string.Join(\",\", rw.Select(cell => \"\\\"\" + (cell ?? \"\").Replace(\"\\\"\", \"\\\"\\\"\") + \"\\\"\"))));");
            }
            sb.AppendLine("  SetResult(new { kind = \"file\", headline = System.IO.Path.GetFileName(__path), sub = \"Exported \" + __s.Name, path = System.IO.Path.GetDirectoryName(__path) });");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
