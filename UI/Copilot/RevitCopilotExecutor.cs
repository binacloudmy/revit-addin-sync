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
            RunCode(toRun, onDone);
        }

        /// <summary>Run a raw synthesized snippet through the shared Revit ExternalEvent.</summary>
        public void RunCode(string code, Action<ExecOutcome> onDone)
        {
            if (string.IsNullOrEmpty(code))
            {
                Dispatch(onDone, new ExecOutcome { Success = false, Error = "No code to run." });
                return;
            }
            try
            {
                App.AIHandler.Action = "execute";
                App.AIHandler.CodeToExecute = code;
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

        /// <summary>
        /// Synthesize runnable C# from a backend /route action that is a VETTED tool (which
        /// carries params, not code). Lets the chat path run vetted actions through the same
        /// type-aware synthesizers as the forms — e.g. open_view honors the view type.
        /// Returns null for unvetted tools (caller uses action.Code).
        /// </summary>
        public static string SynthForChat(string tool, IDictionary<string, object> p)
        {
            if (string.IsNullOrEmpty(tool)) return null;
            switch (tool.ToLowerInvariant())
            {
                case "open_view": return BuildOpenView(S(p, "view_type"), S(p, "view_name"));
                case "select_elements": return BuildSelect(S(p, "target_category"), S(p, "level"), S(p, "filter"));
                case "rename_elements":
                case "set_parameter":
                case "export_schedule":
                    return VettedToolCode.TryBuild(tool, p); // VettedToolCode.Get accepts the backend param keys
                default: return null;
            }
        }

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
                    return BuildSelect(S(v, "category"), S(v, "level"), S(v, "filter"));
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
            // Schedules/column/panel schedules are View subclasses (FilteredElementCollector
            // returns them) but "Open a view" must never land on one — exclude them up front so
            // even the no-match-of-type fallback can't open a schedule.
            sb.AppendLine("var __all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()");
            sb.AppendLine("    .Where(x => x != null && !x.IsTemplate");
            sb.AppendLine("        && x.ViewType != ViewType.Schedule && x.ViewType != ViewType.ColumnSchedule");
            sb.AppendLine("        && x.ViewType != ViewType.PanelSchedule).ToList();");
            sb.AppendLine($"var __typed = __all.Where(x => {pred}).ToList();");
            // Match strictly within the requested type — never widen to other types (that's how
            // "3D" could end up on a floor plan when no 3D view matched). For an unconstrained
            // type the predicate is `true`, so __typed already is every graphical view.
            sb.AppendLine("var __pool = __typed;");
            sb.AppendLine("View __v = string.IsNullOrEmpty(__name) ? null :");
            sb.AppendLine("    (__pool.FirstOrDefault(x => string.Equals(x.Name, __name, StringComparison.OrdinalIgnoreCase))");
            sb.AppendLine("     ?? __pool.FirstOrDefault(x => x.Name != null && x.Name.IndexOf(__name, StringComparison.OrdinalIgnoreCase) >= 0));");
            sb.AppendLine("if (__v == null) __v = __typed.FirstOrDefault();   // type-only / no name match -> default view of that type (e.g. the {3D} view)");
            sb.AppendLine("if (__v != null) { uidoc.RequestViewChange(__v); SetResult(new { kind = \"plain\", headline = \"Opened \" + __v.Name, sub = \"Switched the active view.\" }); }");
            sb.AppendLine("else { SetResult(new { kind = \"plain\", headline = \"View not found\", sub = \"No matching view for that type/name.\" }); }");
            return sb.ToString();
        }

        private static string BuildSelect(string category, string level, string filter = null)
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
            // Optional free-form numeric filter: "height>3000", "length<=2", "area>5 m" etc.
            // Maps to LookupParameter(<Height|Length|Area|Width>). Values in mm by default; cm/m/sqm
            // recognised. If parse fails, no filter is applied (the user sees the full set).
            var parsed = ParseSelectFilter(filter);
            if (parsed.HasValue)
            {
                var f = parsed.Value;
                string paramLit = Lit(f.ParamName);
                sb.AppendLine($"// filter: {paramLit} {f.Op} {f.FeetValue:R} (ft)");
                sb.AppendLine("__els = __els.Where(__e => {");
                sb.AppendLine($"    var __pp = __e.LookupParameter(\"{paramLit}\");");
                sb.AppendLine("    if (__pp == null || __pp.StorageType != StorageType.Double) return false;");
                sb.AppendLine("    var __vv = __pp.AsDouble();");
                sb.AppendLine($"    return __vv {f.Op} {f.FeetValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};");
                sb.AppendLine("}).ToList();");
            }
            sb.AppendLine("var __ids = __els.Select(e => e.Id).ToList();");
            sb.AppendLine("uidoc.Selection.SetElementIds(__ids);");
            sb.AppendLine("if (__ids.Count > 0) uidoc.ShowElements(__ids);");
            string filterMsg = string.IsNullOrWhiteSpace(filter) ? "" : (parsed.HasValue ? " (filter: " + Lit(filter) + ")" : " (filter ignored: not parseable)");
            sb.AppendLine($"SetResult(new {{ kind = \"plain\", headline = __ids.Count + \" {c} selected\", sub = \"Zoomed to selection.{filterMsg}\" }});");
            return sb.ToString();
        }

        private struct SelectFilter { public string ParamName; public string Op; public double FeetValue; }

        // Cheap, deliberately small parser: <param> <op> <number>[unit]
        // param: height|length|area|width  op: < <= > >= =
        // unit (length): mm (default), cm, m   unit (area): sqm/m2 (default mm² if unitless under
        // 100, else m² is too risky — so we require an explicit unit for area; otherwise treat as
        // the param's native feet/sqft). Returns null if anything is off.
        private static SelectFilter? ParseSelectFilter(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                raw.Trim(),
                @"^(?<p>height|length|area|width)\s*(?<o><=|>=|<|>|=)\s*(?<n>-?\d+(?:\.\d+)?)\s*(?<u>mm|cm|m|sqm|m2)?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string p = char.ToUpperInvariant(m.Groups["p"].Value[0]) + m.Groups["p"].Value.Substring(1).ToLowerInvariant();
            string op = m.Groups["o"].Value; if (op == "=") op = "=="; // C#
            if (!double.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var num)) return null;
            string u = m.Groups["u"].Value.ToLowerInvariant();
            double feet;
            bool isArea = string.Equals(p, "Area", StringComparison.OrdinalIgnoreCase);
            if (isArea)
            {
                // Default: square metres if no unit (areas in mm² are absurdly large numbers).
                // 1 m² = 10.7639 sqft (feet²).
                feet = num * 10.7639;
                if (u == "sqm" || u == "m2" || u == "m" || u == "") feet = num * 10.7639;
            }
            else
            {
                // Length default mm. mm→ft = *0.0032808399; cm = *0.032808399; m = *3.2808399.
                if (u == "m") feet = num * 3.2808399;
                else if (u == "cm") feet = num * 0.032808399;
                else feet = num * 0.0032808399; // mm default
            }
            return new SelectFilter { ParamName = p, Op = op, FeetValue = feet };
        }
    }
}
