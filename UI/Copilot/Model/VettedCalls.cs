#nullable enable
// The library's vetted tools and /open-view as CopilotToolHost calls
// (lean-harness spec §7.2 "Existing features move over"; W5 Task 5).
// Revit-free. They used to run synthesized C# directly; now each one is one
// of the 7 add-in tools, so it gets the same envelope,
// preview, confirm card, receipt and Undo as the model's calls:
//
//   open_view        -> run_revit_code  (write request, ruling R13; W4 decides
//                                        it reads, so no card)
//   select_elements  -> find_elements   highlight + zoom (read)
//   set_parameter    -> set_parameters  filter by category (+ level / view /
//                                        selected ids)            (write)
//   rename_elements  -> run_revit_code  name find/replace in a category (write)
//   export_schedule  -> inspect         schedule rows, then the pane exports (read)
//
// User text never becomes code: names go into C# string literals escaped here.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class VettedCall
    {
        public ToolCallDto Call = new ToolCallDto();
        public bool Write;
        /// <summary>export_schedule: the pane writes the rows to a file.</summary>
        public bool ExportSchedule;
        public string Format = "xlsx";
        public string AfterExport = "";
        public string Label = "";
    }

    public static class VettedCalls
    {
        public const int MaxSelectionIds = SnapshotLimits.MaxSelectionIds;

        /// <summary>Null when a required value is missing (with the reason).</summary>
        public static VettedCall? Build(string backendName, IDictionary<string, object?> v, SnapshotSelection? selection, out string error)
        {
            error = "";
            switch (backendName)
            {
                case "open_view":
                {
                    var view = S(v, "view");
                    if (view.Length == 0) { error = "Name a view to open."; return null; }
                    return Code("Open view " + view, OpenViewCode(view), write: true);
                }
                case "select_elements":
                {
                    var category = S(v, "category");
                    if (category.Length == 0) { error = "Pick a category."; return null; }
                    var query = new Dictionary<string, object> { ["category"] = category };
                    var level = S(v, "level");
                    if (level.Length > 0 && !level.Equals("Any", StringComparison.OrdinalIgnoreCase)) query["level"] = level;
                    return Typed(ToolNames.FindElements, "Select " + category, write: false, new Dictionary<string, object>
                    {
                        ["queries"] = new[] { query },
                        ["highlight"] = true,
                        ["zoom"] = true,
                    });
                }
                case "set_parameter":
                {
                    var category = S(v, "category");
                    var param = S(v, "param");
                    var value = S(v, "value");
                    if (category.Length == 0 || param.Length == 0) { error = "Pick a category and a parameter."; return null; }
                    object target;
                    var scope = S(v, "scope");
                    if (scope.Equals("Selection", StringComparison.OrdinalIgnoreCase))
                    {
                        var ids = selection?.Ids ?? new List<long>();
                        if (ids.Count == 0) { error = "Nothing is selected in Revit."; return null; }
                        if ((selection?.Count ?? ids.Count) > MaxSelectionIds)
                        {
                            error = "Select at most " + MaxSelectionIds + " elements, or use Whole model.";
                            return null;
                        }
                        target = new Dictionary<string, object> { ["ids"] = ids.ToArray() };
                    }
                    else
                    {
                        var filter = new Dictionary<string, object> { ["category"] = category };
                        if (scope.Equals("Active view", StringComparison.OrdinalIgnoreCase)) filter["in_view"] = true;
                        target = new Dictionary<string, object> { ["filter"] = filter };
                    }
                    return Typed(ToolNames.SetParameters, "Set " + param + " on " + category, write: true, new Dictionary<string, object>
                    {
                        ["target"] = target,
                        ["param"] = param,
                        ["value"] = value,
                    });
                }
                case "rename_elements":
                {
                    var category = S(v, "category");
                    var find = S(v, "find");
                    if (category.Length == 0 || find.Length == 0) { error = "Pick a category and the text to find."; return null; }
                    var selectionOnly = S(v, "scope").Equals("Selection", StringComparison.OrdinalIgnoreCase);
                    return Code("Rename " + category + ": " + find + " → " + S(v, "replace"),
                        RenameCode(category, find, S(v, "replace"), selectionOnly), write: true);
                }
                case "export_schedule":
                {
                    var schedule = S(v, "schedule");
                    if (schedule.Length == 0) { error = "Pick a schedule."; return null; }
                    var call = Typed(ToolNames.Inspect, "Export " + schedule, write: false, new Dictionary<string, object> { ["schedule"] = schedule });
                    call.ExportSchedule = true;
                    call.Format = S(v, "format").Equals("csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "xlsx";
                    call.AfterExport = S(v, "open");
                    return call;
                }
                default:
                    error = "This library tool is not available in this add-in build.";
                    return null;
            }
        }

        /// <summary>The pane's Highlight on a check rule (or any handle): find_elements
        /// over the handle's elements, selected in Revit. A read: no card.</summary>
        public static VettedCall? Highlight(string? handle, out string error)
        {
            error = "";
            var h = (handle ?? "").Trim();
            if (h.Length == 0) { error = "Nothing to highlight."; return null; }
            return Typed(ToolNames.FindElements, "Highlight", write: false, new
            {
                queries = new[] { new Dictionary<string, object>() },
                handle = h,
                highlight = true,
            });
        }

        /// <summary>/open-view &lt;name&gt; from the composer.</summary>
        public static VettedCall? OpenView(string viewName, out string error) =>
            Build("open_view", new Dictionary<string, object?> { ["view"] = viewName }, null, out error);

        // ------------------------------------------------------------------

        private static VettedCall Typed(string tool, string label, bool write, object args) => new VettedCall
        {
            Label = label,
            Write = write,
            Call = new ToolCallDto
            {
                CallId = "lib_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                IdempotencyKey = "lib:" + Guid.NewGuid().ToString("N"),
                Tool = tool,
                Args = JsonSerializer.SerializeToElement(args),
            },
        };

        private static VettedCall Code(string intent, string code, bool write) =>
            Typed(ToolNames.RunRevitCode, intent, write, new Dictionary<string, object>
            {
                ["code"] = code,
                ["intent"] = intent.Length <= 120 ? intent : intent.Substring(0, 119) + "…",
            });

        internal static string OpenViewCode(string viewName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("var name = " + Literal(viewName) + ";");
            sb.AppendLine("var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(x => !x.IsTemplate).ToList();");
            sb.AppendLine("var target = views.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))");
            sb.AppendLine("    ?? views.FirstOrDefault(x => x.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);");
            sb.AppendLine("if (target == null) Print(\"No view named '\" + name + \"'.\");");
            sb.AppendLine("else { uidoc.RequestViewChange(target); Print(\"Opened \" + target.Name); }");
            return sb.ToString();
        }

        internal static string RenameCode(string category, string find, string replace, bool selectionOnly)
        {
            var sb = new StringBuilder();
            sb.AppendLine("var categoryName = " + Literal(category) + ";");
            sb.AppendLine("var find = " + Literal(find) + ";");
            sb.AppendLine("var replace = " + Literal(replace) + ";");
            sb.AppendLine("Category category = null;");
            sb.AppendLine("foreach (Category c in doc.Settings.Categories)");
            sb.AppendLine("    if (string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase)) { category = c; break; }");
            sb.AppendLine("if (category == null) Print(\"No category named '\" + categoryName + \"'.\");");
            sb.AppendLine("else");
            sb.AppendLine("{");
            sb.AppendLine("    var elements = new FilteredElementCollector(doc).OfCategoryId(category.Id).WhereElementIsNotElementType().ToElements();");
            if (selectionOnly)
            {
                sb.AppendLine("    var selected = uidoc.Selection.GetElementIds();");
                sb.AppendLine("    elements = elements.Where(e => selected.Contains(e.Id)).ToList();");
            }
            sb.AppendLine("    var renamed = 0;");
            sb.AppendLine("    var skipped = 0;");
            sb.AppendLine("    foreach (var e in elements)");
            sb.AppendLine("    {");
            sb.AppendLine("        var old = e.Name;");
            sb.AppendLine("        if (string.IsNullOrEmpty(old) || old.IndexOf(find, StringComparison.Ordinal) < 0) continue;");
            sb.AppendLine("        try { e.Name = old.Replace(find, replace); renamed++; }");
            sb.AppendLine("        catch (Exception) { skipped++; }");
            sb.AppendLine("    }");
            sb.AppendLine("    Print(renamed + \" renamed\" + (skipped > 0 ? \", \" + skipped + \" could not be renamed\" : \"\"));");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>A C# regular string literal; control characters are dropped.</summary>
        internal static string Literal(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (char.IsControl(c)) continue;
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string S(IDictionary<string, object?> v, string k) =>
            v != null && v.TryGetValue(k, out var o) && o != null ? (o.ToString() ?? "").Trim() : "";
    }
}
