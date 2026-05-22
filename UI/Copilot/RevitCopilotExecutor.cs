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
                    return BuildOpenView(S(v, "type"), S(v, "view"));
                case "select_elements":
                    return BuildSelect(S(v, "category"), S(v, "level"));
                default:
                    return null;
            }
        }

        private static string BuildOpenView(string viewType, string viewName)
        {
            string n = Lit(viewName);
            string t = (viewType ?? "").Trim().ToLowerInvariant();

            // Filter to the requested view kind first, then match by name within it. This is
            // what makes "3D" open a 3D view (not a floor plan with a matching name) and lets a
            // type-only request fall back to the default view of that type.
            string pred;
            if (t.Contains("3d")) pred = "x is View3D";
            else if (t.Contains("section")) pred = "x.ViewType == ViewType.Section";
            else if (t.Contains("elevation")) pred = "x.ViewType == ViewType.Elevation";
            else if (t.Contains("drafting")) pred = "x.ViewType == ViewType.DraftingView";
            else if (t.Contains("floor") || t.Contains("plan"))
                pred = "(x.ViewType == ViewType.FloorPlan || x.ViewType == ViewType.CeilingPlan || x.ViewType == ViewType.AreaPlan)";
            else pred = "true";

            var sb = new StringBuilder();
            // Referencing uidoc.ActiveView opts out of the executor's auto-transaction wrap,
            // so RequestViewChange runs outside a transaction (Revit forbids it inside one).
            sb.AppendLine("var __cur = uidoc.ActiveView;");
            sb.AppendLine($"var __name = \"{n}\";");
            sb.AppendLine("var __all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()");
            sb.AppendLine("    .Where(x => x != null && !x.IsTemplate).ToList();");
            sb.AppendLine($"var __typed = __all.Where(x => {pred}).ToList();");
            sb.AppendLine("var __pool = __typed.Count > 0 ? __typed : __all;");
            sb.AppendLine("View __v = string.IsNullOrEmpty(__name) ? null :");
            sb.AppendLine("    (__pool.FirstOrDefault(x => string.Equals(x.Name, __name, StringComparison.OrdinalIgnoreCase))");
            sb.AppendLine("     ?? __pool.FirstOrDefault(x => x.Name != null && x.Name.IndexOf(__name, StringComparison.OrdinalIgnoreCase) >= 0));");
            sb.AppendLine("if (__v == null) __v = __typed.FirstOrDefault();   // type-only / no name match -> default view of that type (e.g. the {3D} view)");
            sb.AppendLine("if (__v != null) { uidoc.RequestViewChange(__v); SetResult(new { kind = \"plain\", headline = \"Opened \" + __v.Name, sub = \"Switched the active view.\" }); }");
            sb.AppendLine("else { SetResult(new { kind = \"plain\", headline = \"View not found\", sub = \"No matching view for that type/name.\" }); }");
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
