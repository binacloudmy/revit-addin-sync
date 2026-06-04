using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
    ///
    /// Self-heal: when a generated snippet fails to compile or run, the executor feeds the
    /// REAL error back to the backend (<see cref="AIService.RetryCodeAsync"/>), re-executes,
    /// and repeats up to <see cref="MaxAttempts"/> times. On success the verified fix is
    /// recorded (<see cref="AIService.RecordFixAsync"/>) WITH the prompt so recipe auto-grow
    /// can gate on a real run result. Tier-1 synthesized code is never sent through the loop
    /// (it's deterministic and has no backend prompt to regenerate from).
    /// </summary>
    public class RevitCopilotExecutor : ICopilotExecutor
    {
        /// <summary>Bounded retries for the self-heal loop. 2 retries = up to 3 executions.</summary>
        public int MaxAttempts { get; set; } = 2;

        // ─── Self-heal context (injected by the panel, like the executor itself) ──────────
        /// <summary>Backend client used to regenerate failed code and record verified fixes.
        /// When null, the executor runs once with no self-heal (back-compat).</summary>
        public AIService Ai { get; set; }

        /// <summary>Returns the natural-language prompt for the current run (the user's
        /// request). Used as the retry/record prompt; falls back to the tool title.</summary>
        public Func<string> PromptProvider { get; set; }

        /// <summary>Bearer token for backend calls (optional).</summary>
        public Func<string> AccessTokenProvider { get; set; }

        public int? UserId { get; set; }
        public string SessionId { get; set; }

        /// <summary>Optional progress callback for "Fixing… (attempt N)" cards shown
        /// between retries. Dispatched to the UI thread.</summary>
        public Action<int> OnRetryProgress { get; set; }

        public void Run(ToolDef tool, IDictionary<string, object> values, string code, Action<ExecOutcome> onDone)
        {
            bool tier1 = tool != null && tool.Tier == 1;
            string toRun = tier1 ? SynthVetted(tool, values) : code;

            if (string.IsNullOrEmpty(toRun))
            {
                Dispatch(onDone, new ExecOutcome { Success = false, Error = "Couldn't build runnable code for this tool." });
                return;
            }

            // Tier-1 is deterministic synthesized code with no backend prompt to regenerate
            // from — run it once. Only Tier-2 codegen (or any run with an AIService wired)
            // goes through the bounded self-heal loop.
            if (tier1 || Ai == null)
            {
                RunOnce(toRun, onDone);
                return;
            }

            RunWithSelfHeal(tool, toRun, onDone);
        }

        // Single fire-and-forget execution through the ExternalEvent (original behaviour).
        private void RunOnce(string toRun, Action<ExecOutcome> onDone)
        {
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

        // The bounded retry loop. Runs on a worker thread so it can block on each
        // ExternalEvent round-trip without freezing the UI/Revit thread (the
        // ExternalEvent handler runs on Revit's UI thread and signals us back).
        private void RunWithSelfHeal(ToolDef tool, string initialCode, Action<ExecOutcome> onDone)
        {
            string prompt = ResolvePrompt(tool);
            string token = AccessTokenProvider != null ? AccessTokenProvider() : null;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var result = SelfHeal.RunWithRetries(
                        initialCode: initialCode,
                        executeFn: ExecuteOnUiThread,
                        retryFn: (failedCode, error, attempt) =>
                        {
                            // Surface "Fixing… (attempt N+1)" before regenerating.
                            DispatchProgress(attempt + 1);
                            var resp = Ai.RetryCodeAsync(prompt, failedCode, error, attempt,
                                                         UserId, SessionId, token)
                                         .GetAwaiter().GetResult();
                            return resp != null && resp.Success ? resp.Code : null;
                        },
                        recordFn: (error, workingCode) =>
                        {
                            // Fire-and-forget: record the verified (error, code, prompt) triple.
                            // Passing the prompt is REQUIRED for recipe auto-grow.
                            _ = Ai.RecordFixAsync(error, workingCode, prompt, UserId, SessionId, token);
                        },
                        maxAttempts: MaxAttempts);

                    Dispatch(onDone, Map(result));
                }
                catch (Exception ex)
                {
                    Dispatch(onDone, new ExecOutcome { Success = false, Error = ex.Message });
                }
            });
        }

        // Raise the ExternalEvent and block until OnCompleted fires. Called from the
        // worker thread, so blocking here is safe — the handler runs on Revit's UI thread.
        private ExecutionResult ExecuteOnUiThread(string code)
        {
            ExecutionResult captured = null;
            using (var done = new ManualResetEventSlim(false))
            {
                try
                {
                    App.AIHandler.Action = "execute";
                    App.AIHandler.CodeToExecute = code;
                    App.AIHandler.OnCompleted = r => { captured = r; done.Set(); };
                    App.AIExternalEvent.Raise();
                }
                catch (Exception ex)
                {
                    return new ExecutionResult { Success = false, Error = ex.Message };
                }

                // Bound the wait so a never-firing event can't hang the worker forever.
                // The CodeExecutor itself has a per-execution watchdog (~30s); allow a
                // little headroom for the ExternalEvent to be serviced.
                if (!done.Wait(TimeSpan.FromSeconds(60)))
                    return new ExecutionResult { Success = false, Error = "Execution did not complete in time." };
            }
            return captured ?? new ExecutionResult { Success = false, Error = "No result." };
        }

        private string ResolvePrompt(ToolDef tool)
        {
            string p = PromptProvider != null ? PromptProvider() : null;
            if (!string.IsNullOrWhiteSpace(p)) return p;
            return tool != null ? (tool.Title ?? string.Empty) : string.Empty;
        }

        private void DispatchProgress(int attempt)
        {
            if (OnRetryProgress == null) return;
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.Invoke(() => OnRetryProgress(attempt));
            else
                OnRetryProgress(attempt);
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
