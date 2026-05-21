using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Runs Copilot tools against the live Revit document using the shared
    /// App.AIExternalEvent / CodeExecutionHandler / CodeExecutor pipeline.
    ///
    /// Tier-1 vetted tools are synthesized deterministically here (rename/set-param/export
    /// via the existing VettedToolCode; open-view/select natively). Tier-2 commands run the
    /// C# passed in (real codegen via AIService is wired in Task 12). The CodeExecutor
    /// auto-wraps an undoable transaction and swallows Revit warnings.
    /// </summary>
    public class RevitCopilotExecutor : ICopilotExecutor
    {
        public void Run(ToolDef tool, IDictionary<string, object> values, string code, Action<ExecOutcome> onDone)
        {
            string toRun = tool != null && tool.Tier == 1 ? SynthVetted(tool, values) : code;

            if (string.IsNullOrEmpty(toRun))
            {
                Dispatch(onDone, new ExecOutcome { Success = false, Error = "Couldn't build runnable code for this tool." });
                return;
            }

            try
            {
                App.AIHandler.Action = "execute";
                App.AIHandler.CodeToExecute = toRun;
                App.AIHandler.OnCompleted = result => Dispatch(onDone, Map(result));
                App.AIExternalEvent.Raise();
            }
            catch (Exception ex)
            {
                Dispatch(onDone, new ExecOutcome { Success = false, Error = ex.Message });
            }
        }

        private static ExecOutcome Map(ExecutionResult r)
        {
            if (r == null) return new ExecOutcome { Success = false, Error = "No result." };
            return new ExecOutcome { Success = r.Success, Message = r.Message, Error = r.Error, Data = r.Data };
        }

        private static void Dispatch(Action<ExecOutcome> onDone, ExecOutcome outcome)
        {
            if (onDone == null) return;
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.Invoke(() => onDone(outcome));
            else
                onDone(outcome);
        }

        // ─── Tier-1 synthesis ────────────────────────────────────────────────
        private static string Lit(string s) => (s ?? "").Replace("\\", "").Replace("\"", "");
        private static string S(IDictionary<string, object> v, string k)
            => v != null && v.TryGetValue(k, out var o) && o != null ? o.ToString() : "";

        private static string SynthVetted(ToolDef tool, IDictionary<string, object> v)
        {
            switch (tool.BackendName)
            {
                case "rename_elements":
                    return VettedToolCode.TryBuild("rename_elements", new Dictionary<string, object>
                    {
                        ["category"] = S(v, "category"),
                        ["find"] = S(v, "find"),
                        ["replace"] = S(v, "replace"),
                    });
                case "set_parameter":
                    return VettedToolCode.TryBuild("set_parameter", new Dictionary<string, object>
                    {
                        ["category"] = S(v, "category"),
                        ["param"] = S(v, "param"),
                        ["value"] = S(v, "value"),
                    });
                case "export_schedule":
                    return VettedToolCode.TryBuild("export_schedule", new Dictionary<string, object>
                    {
                        ["name"] = S(v, "schedule"),
                        ["format"] = S(v, "format"),
                    });
                case "open_view":
                    return BuildOpenView(S(v, "view"));
                case "select_elements":
                    return BuildSelect(S(v, "category"), S(v, "level"));
                default:
                    return null;
            }
        }

        private static string BuildOpenView(string viewName)
        {
            string n = Lit(viewName);
            var sb = new StringBuilder();
            // Referencing uidoc.ActiveView opts out of the executor's auto-transaction wrap,
            // so RequestViewChange runs outside a transaction (Revit forbids it inside one).
            sb.AppendLine("var __cur = uidoc.ActiveView;");
            sb.AppendLine($"var __name = \"{n}\";");
            sb.AppendLine("var __v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()");
            sb.AppendLine("    .FirstOrDefault(x => x != null && !x.IsTemplate && string.Equals(x.Name, __name, StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine("if (__v == null) __v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()");
            sb.AppendLine("    .FirstOrDefault(x => x != null && !x.IsTemplate && x.Name != null && x.Name.IndexOf(__name, StringComparison.OrdinalIgnoreCase) >= 0);");
            // View-TYPE fallback. Revit's default 3D view is named "{3D}",
            // elevations/sections have arbitrary names — a pure name match
            // never finds them when the user says "the 3D view" / "an
            // elevation". If the request reads as a view kind, resolve by
            // ViewType / View3D instead of failing.
            sb.AppendLine("if (__v == null) {");
            sb.AppendLine("    var __q = (__name ?? \"\").ToLowerInvariant();");
            sb.AppendLine("    if (__q.Contains(\"3d\")) __v = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()");
            sb.AppendLine("        .Where(x => !x.IsTemplate).OrderByDescending(x => x.Name == \"{3D}\").FirstOrDefault();");
            sb.AppendLine("    else {");
            sb.AppendLine("        ViewType __t = ViewType.Undefined;");
            sb.AppendLine("        if (__q.Contains(\"eleva\")) __t = ViewType.Elevation;");
            sb.AppendLine("        else if (__q.Contains(\"section\")) __t = ViewType.Section;");
            sb.AppendLine("        else if (__q.Contains(\"ceiling\")) __t = ViewType.CeilingPlan;");
            sb.AppendLine("        else if (__q.Contains(\"legend\")) __t = ViewType.Legend;");
            sb.AppendLine("        else if (__q.Contains(\"draft\")) __t = ViewType.DraftingView;");
            sb.AppendLine("        else if (__q.Contains(\"plan\")) __t = ViewType.FloorPlan;");
            sb.AppendLine("        if (__t != ViewType.Undefined) __v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()");
            sb.AppendLine("            .FirstOrDefault(x => !x.IsTemplate && x.ViewType == __t);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("if (__v != null) { uidoc.RequestViewChange(__v); SetResult(new { kind = \"plain\", headline = \"Opened \" + __v.Name, sub = \"Switched the active view.\" }); }");
            sb.AppendLine("else { SetResult(new { kind = \"plain\", headline = \"View not found\", sub = \"No view named '\" + __name + \"'.\" }); }");
            return sb.ToString();
        }

        private static string BuildSelect(string category, string level)
        {
            string c = Lit(category);
            string lvl = Lit(level);
            var sb = new StringBuilder();
            sb.AppendLine("var __cur = uidoc.ActiveView;");
            sb.AppendLine($"var __catName = \"{c}\";");
            sb.AppendLine("var __cat = doc.Settings.Categories.Cast<Category>()");
            sb.AppendLine("    .FirstOrDefault(x => x != null && string.Equals(x.Name, __catName, StringComparison.OrdinalIgnoreCase));");
            sb.AppendLine("var __els = __cat == null ? new List<Element>() : new FilteredElementCollector(doc)");
            sb.AppendLine("    .OfCategoryId(__cat.Id).WhereElementIsNotElementType().Cast<Element>().ToList();");
            if (!string.IsNullOrEmpty(lvl) && !string.Equals(lvl, "Any", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"var __lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()");
                sb.AppendLine($"    .FirstOrDefault(l => string.Equals(l.Name, \"{lvl}\", StringComparison.OrdinalIgnoreCase));");
                sb.AppendLine("if (__lvl != null) __els = __els.Where(e => e.LevelId == __lvl.Id).ToList();");
            }
            sb.AppendLine("var __ids = __els.Select(e => e.Id).ToList();");
            sb.AppendLine("uidoc.Selection.SetElementIds(__ids);");
            sb.AppendLine("if (__ids.Count > 0) uidoc.ShowElements(__ids);");
            sb.AppendLine($"SetResult(new {{ kind = \"plain\", headline = __ids.Count + \" {c} selected\", sub = \"Zoomed to selection.\" }});");
            return sb.ToString();
        }
    }
}
