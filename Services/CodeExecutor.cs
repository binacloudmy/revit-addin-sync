using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    public class CodeExecutor
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly View _activeView;

        // Hard cap on a single snippet's execution. A generated loop that hangs
        // must not freeze Revit forever. Revit API calls must run on the UI
        // thread, so we cannot safely abort a runaway Invoke mid-flight (killing
        // the UI thread corrupts the session). v1 contract: detect the overrun,
        // log it, and surface a timeout ExecutionResult instead of reporting
        // success — the work that did run stays committed, but the user is told
        // it exceeded the limit rather than being left staring at a frozen UI.
        private static readonly TimeSpan ExecutionWatchdogTimeout = TimeSpan.FromSeconds(30);

        public CodeExecutor(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
            _activeView = _doc.ActiveView;
        }

        /// <summary>
        /// Compile and execute AI-generated code
        /// </summary>
        public ExecutionResult Execute(string code)
        {
            AssemblyLoadContext loadContext = null;
            try
            {
                // Wrap code in executable class
                string fullCode = WrapCode(code, out int userCodeLineOffset);

                // Compile  (timed — Roslyn compile cost)
                var _swCompile = System.Diagnostics.Stopwatch.StartNew();
                var assembly = CompileCode(fullCode, userCodeLineOffset, out loadContext);
                _swCompile.Stop();

                // Execute  (timed — runs on UI thread: includes transaction
                // commit + regen, the freeze suspect)
                var type = assembly.GetType("RevitWebAppSync.Dynamic.AIGeneratedCode");
                var method = type.GetMethod("Execute");
                var instance = Activator.CreateInstance(type);

                var _swExec = System.Diagnostics.Stopwatch.StartNew();
                var result = method.Invoke(instance, new object[] { _doc, _uidoc, _activeView });
                _swExec.Stop();

                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][timing] codegen compile={_swCompile.ElapsedMilliseconds}ms " +
                    $"exec(tx+regen)={_swExec.ElapsedMilliseconds}ms");

                // Watchdog: a snippet that ran past the hard cap is reported as a
                // timeout rather than success — the self-heal loop then feeds this
                // back as an error (e.g. "tighten the loop / add a bound").
                if (_swExec.Elapsed > ExecutionWatchdogTimeout)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BinaVibe][timing] codegen EXEC OVERRAN watchdog — " +
                        $"{_swExec.ElapsedMilliseconds}ms > {ExecutionWatchdogTimeout.TotalMilliseconds}ms");
                    return new ExecutionResult
                    {
                        Success = false,
                        Error = $"Execution exceeded {(int)ExecutionWatchdogTimeout.TotalSeconds}s and was stopped."
                    };
                }

                // A string return is a status message; any other object/array is structured
                // model data — capture it as JSON so the Copilot card renders real numbers.
                string message;
                string data = null;
                if (result is string s)
                {
                    message = string.IsNullOrEmpty(s) ? "Done" : s;
                }
                else if (result != null)
                {
                    message = "Done";
                    data = SafeJson(result);
                }
                else
                {
                    message = "Executed successfully";
                }

                return new ExecutionResult
                {
                    Success = true,
                    Message = message,
                    Data = data
                };
            }
            catch (TargetInvocationException ex)
            {
                var innerEx = ex.InnerException ?? ex;
                return new ExecutionResult
                {
                    Success = false,
                    Error = $"Execution error: {innerEx.Message}"
                };
            }
            catch (CompilationException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][timing] codegen COMPILE FAILED — {ex.Message}");
                return new ExecutionResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
            catch (Exception ex)
            {
                return new ExecutionResult
                {
                    Success = false,
                    Error = $"Error: {ex.Message}"
                };
            }
            finally
            {
                // Release the per-execution assembly so the Revit session doesn't
                // accumulate one dynamic assembly per generated snippet.
                loadContext?.Unload();
            }
        }

        /// <summary>
        /// Compile-only check — no execution, no Revit thread.
        ///
        /// Wraps + compiles the snippet with the EXACT same wrapper and reference
        /// set <see cref="Execute"/> uses, but stops after Roslyn emit: it never
        /// loads or invokes the assembly and never opens a transaction. Pure CPU,
        /// so it runs safely on a worker thread — the self-heal loop calls this
        /// BEFORE the ExternalEvent, so a snippet with a hallucinated API member
        /// (wrong overload, removed member, misspelled enum) is caught and fed
        /// back to the model without a single Revit round-trip.
        /// </summary>
        /// <returns>null when it compiles clean; otherwise the formatted error
        /// string (same shape as a runtime <see cref="CompilationException"/>).</returns>
        public static string CompileCheck(string userCode)
        {
            try
            {
                string fullCode = WrapCode(userCode, out int userCodeLineOffset);
                var syntaxTree = CSharpSyntaxTree.ParseText(fullCode);
                var compilation = CSharpCompilation.Create(
                    $"AICheck_{Guid.NewGuid():N}",
                    new[] { syntaxTree },
                    BuildReferences(),
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        optimizationLevel: OptimizationLevel.Release,
                        allowUnsafe: false));

                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);
                if (emitResult.Success) return null;

                return "Compilation failed:\n" + FormatErrors(emitResult.Diagnostics, userCodeLineOffset);
            }
            catch (Exception ex)
            {
                // A check that itself throws must not block execution — let the
                // normal run path surface any real problem.
                System.Diagnostics.Debug.WriteLine($"[BinaVibe] CompileCheck threw, skipping gate: {ex.Message}");
                return null;
            }
        }

        // Map Roslyn error diagnostics to user-line-numbered strings.
        private static string FormatErrors(IEnumerable<Diagnostic> diagnostics, int userCodeLineOffset)
        {
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var lineSpan = d.Location.GetLineSpan();
                    var adjustedLine = Math.Max(1, lineSpan.StartLinePosition.Line - userCodeLineOffset + 1);
                    return $"Line {adjustedLine}: {d.GetMessage()}";
                });
            return string.Join("\n", errors);
        }

        /// <summary>
        /// Wrap user code in a class structure
        /// </summary>
        // For each `var x = new Transaction(...)` (or `Transaction x = new ...`)
        // in the agent's own code, inject our silent fail-handler right after
        // `x.Start();` and replace `x.Commit();` with a checked Commit that
        // throws on RolledBack — so a Revit warning modal becomes a silent
        // rollback that the host can auto-retry, and a rollback can't be
        // mis-reported as success by the agent's "Done" line.
        private static string InjectFailureHandlingIntoUserTransactions(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(
                code,
                @"\b(?:var|Transaction)\s+(\w+)\s*=\s*new\s+Transaction\s*\("))
            {
                names.Add(m.Groups[1].Value);
            }
            if (names.Count == 0) return code;

            foreach (var n in names)
            {
                var en = Regex.Escape(n);

                // After `<n>.Start();`, attach the failure preprocessor.
                code = Regex.Replace(
                    code,
                    $@"\b{en}\s*\.\s*Start\s*\(\s*\)\s*;",
                    m => m.Value
                         + " { var __fho_" + n + " = " + n + ".GetFailureHandlingOptions();"
                         + " __fho_" + n + " = __fho_" + n + ".SetFailuresPreprocessor(new __FailHandler());"
                         + " " + n + ".SetFailureHandlingOptions(__fho_" + n + "); }");

                // `<n>.Commit();` (as a bare statement) → checked Commit that
                // throws on rollback. Only matches when Commit() is a statement;
                // doesn't touch `if (<n>.Commit() == ...)` etc.
                code = Regex.Replace(
                    code,
                    $@"\b{en}\s*\.\s*Commit\s*\(\s*\)\s*;",
                    "if (" + n + ".Commit() != TransactionStatus.Committed)"
                    + " throw new InvalidOperationException(\"Changes were not applied — the transaction was rolled back (Revit blocked the edit, e.g. shared/locked elements, join conflicts, or warnings escalated to errors).\");");
            }
            return code;
        }

        // Serialize a snippet's structured return to JSON, defensively — ignores reference
        // loops, caps depth, and drops oversized payloads (e.g. a raw element dump) so a
        // badly-shaped return degrades to a plain card instead of crashing.
        private static string SafeJson(object value)
        {
            try
            {
                var settings = new Newtonsoft.Json.JsonSerializerSettings
                {
                    ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                    MaxDepth = 6,
                    Error = (sender, e) => { e.ErrorContext.Handled = true; },
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(value, settings);
                if (json != null && json.Length > 60000) return null;
                return json;
            }
            catch { return null; }
        }

        private static string WrapCode(string userCode, out int userCodeLineOffset)
        {
            var sb = new StringBuilder();
            var code = userCode ?? string.Empty;

            // Only pull ClosedXML into the wrapper when the snippet actually needs
            // it — keeps non-Excel snippets free of that dependency (and its
            // compile-time failure surface).
            bool needsExcel = code.Contains("ReadExcel") || code.Contains("WriteExcel")
                              || code.Contains("FindExcelFile") || code.Contains("XLWorkbook");

            // Don't auto-wrap in a Transaction when:
            //  - the code already manages its own ("Transaction" mentioned), or
            //  - the code changes the active view — RequestViewChange / ActiveView
            //    setter throw "Cannot change the active view ... with a transaction
            //    currently open", and opening a view isn't a model edit anyway.
            bool selfManagesTransaction = code.Contains("Transaction")
                                          || code.Contains("OpenView")
                                          || code.Contains("RequestViewChange")
                                          || code.Contains(".ActiveView =")
                                          || code.Contains(".ActiveView=");

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Text.RegularExpressions;");
            sb.AppendLine("using Autodesk.Revit.DB;");
            sb.AppendLine("using Autodesk.Revit.DB.Architecture;");
            sb.AppendLine("using Autodesk.Revit.DB.Structure;");
            sb.AppendLine("using Autodesk.Revit.UI;");
            sb.AppendLine("using Autodesk.Revit.UI.Selection;");
            if (needsExcel) sb.AppendLine("using ClosedXML.Excel;");
            sb.AppendLine();
            sb.AppendLine("namespace RevitWebAppSync.Dynamic");
            sb.AppendLine("{");
            sb.AppendLine("    public class AIGeneratedCode");
            sb.AppendLine("    {");
            // Stored so the helper methods below can reach the model/UI without
            // every helper taking them as parameters.
            sb.AppendLine("        private Document doc;");
            sb.AppendLine("        private UIDocument uidoc;");
            sb.AppendLine("        private View activeView;");
            sb.AppendLine("        private System.Text.StringBuilder __aiOutput = new System.Text.StringBuilder();");
            sb.AppendLine("        private object __result = null;");
            sb.AppendLine();
            sb.AppendLine("        private string GetParameterValue(Element elem, BuiltInParameter param)");
            sb.AppendLine("        {");
            sb.AppendLine("            var p = elem.get_Parameter(param);");
            sb.AppendLine("            if (p == null) return \"N/A\";");
            sb.AppendLine("            return p.AsString() ?? p.AsValueString() ?? \"N/A\";");
            sb.AppendLine("        }");
            sb.AppendLine();
            // Helpers the revit_ai agent is prompted to use. ShowMessage accumulates
            // into __aiOutput, which Execute returns — the addin shows it in the chat
            // (no modal popup).
            sb.AppendLine("        private void ShowMessage(string title, string message)");
            sb.AppendLine("        {");
            sb.AppendLine("            var t = string.IsNullOrEmpty(title) ? string.Empty : title + \": \";");
            sb.AppendLine("            __aiOutput.AppendLine(t + (message ?? string.Empty));");
            sb.AppendLine("        }");
            sb.AppendLine();
            // Snippets call SetResult(...) to emit a structured result card (count/issues/list/
            // file/plain) from real model data. Stored and returned after the transaction commits.
            sb.AppendLine("        private void SetResult(object data) { __result = data; }");
            sb.AppendLine();
            sb.AppendLine("        private void OpenView(View view)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (view != null && this.uidoc != null) this.uidoc.RequestViewChange(view);");
            sb.AppendLine("        }");
            sb.AppendLine();
            // Silent failure handler — used both for the auto-wrap path AND injected
            // into any Transaction the agent's code starts on its own (see below).
            // Clears warnings, rolls back the transaction on any real error WITHOUT
            // popping a modal — the host then auto-retries with the error fed back
            // to the agent.
            sb.AppendLine("        private class __FailHandler : IFailuresPreprocessor");
            sb.AppendLine("        {");
            sb.AppendLine("            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)");
            sb.AppendLine("            {");
            sb.AppendLine("                bool hasError = false;");
            sb.AppendLine("                foreach (var f in a.GetFailureMessages())");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);");
            sb.AppendLine("                    else hasError = true;");
            sb.AppendLine("                }");
            sb.AppendLine("                return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            if (needsExcel) AppendExcelHelpers(sb);
            sb.AppendLine("        public object Execute(Document doc, UIDocument uidoc, View activeView)");
            sb.AppendLine("        {");
            sb.AppendLine("            this.doc = doc; this.uidoc = uidoc; this.activeView = activeView;");

            // Auto-wrap the body in a Transaction (with the silent failure handler)
            // so model edits commit + get a named undo entry on the first attempt.
            string bodyIndent = selfManagesTransaction ? "            " : "                ";

            if (!selfManagesTransaction)
            {
                sb.AppendLine("            using (var __tx = new Transaction(doc, \"AI Assistant\"))");
                sb.AppendLine("            {");
                sb.AppendLine("                __tx.Start();");
                sb.AppendLine("                var __fho = __tx.GetFailureHandlingOptions();");
                sb.AppendLine("                __fho = __fho.SetFailuresPreprocessor(new __FailHandler());");
                sb.AppendLine("                __tx.SetFailureHandlingOptions(__fho);");
            }

            // Count preamble lines so CompileCode can map errors to user lines.
            userCodeLineOffset = sb.ToString().Split('\n').Length - 1;

            // When the agent self-manages a Transaction, inject the silent
            // fail-handler into it and turn its Commit() statement into a
            // checked one — otherwise Revit pops the warnings modal (e.g.
            // walls with join conflicts) and a rolled-back transaction
            // silently looks like success.
            string processedCode = code;
            if (selfManagesTransaction)
                processedCode = InjectFailureHandlingIntoUserTransactions(processedCode);

            var lines = processedCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                sb.AppendLine(bodyIndent + line);
            }

            if (!selfManagesTransaction)
            {
                sb.AppendLine("                if (__tx.Commit() != TransactionStatus.Committed)");
                sb.AppendLine("                    throw new InvalidOperationException(\"Changes were not applied — the transaction was rolled back (you may have cancelled, or Revit blocked the edit, e.g. elements inside a group).\");");
                sb.AppendLine("            }");
            }

            sb.AppendLine();
            // Prefer an explicit structured result (SetResult), else the accumulated messages.
            sb.AppendLine("            if (__result != null) return __result;");
            sb.AppendLine("            return __aiOutput.Length > 0 ? (object)__aiOutput.ToString().TrimEnd() : (object)\"Done\";");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        // ClosedXML-backed Excel helpers the revit_ai agent is prompted to use.
        // Only emitted into the wrapper when the snippet references one of them
        // (so non-Excel snippets don't carry the ClosedXML dependency).
        private static void AppendExcelHelpers(StringBuilder sb)
        {
            sb.AppendLine("        private string FindExcelFile(string folderPath, string fileName)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return null;");
            sb.AppendLine("            var nameNoExt = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);");
            sb.AppendLine("            foreach (var f in Directory.GetFiles(folderPath, \"*.xls*\"))");
            sb.AppendLine("            {");
            sb.AppendLine("                if (string.Equals(Path.GetFileNameWithoutExtension(f), nameNoExt, StringComparison.OrdinalIgnoreCase)) return f;");
            sb.AppendLine("            }");
            sb.AppendLine("            return null;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private Dictionary<string, string> ReadExcelAsDictionary(string filePath, int keyColumn, int valueColumn, int startRow)");
            sb.AppendLine("        {");
            sb.AppendLine("            var dict = new Dictionary<string, string>();");
            sb.AppendLine("            using (var wb = new XLWorkbook(filePath))");
            sb.AppendLine("            {");
            sb.AppendLine("                var ws = wb.Worksheet(1);");
            sb.AppendLine("                var lastRow = ws.LastRowUsed() != null ? ws.LastRowUsed().RowNumber() : 0;");
            sb.AppendLine("                for (int r = startRow; r <= lastRow; r++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var k = ws.Cell(r, keyColumn).GetString();");
            sb.AppendLine("                    if (!string.IsNullOrWhiteSpace(k)) dict[k] = ws.Cell(r, valueColumn).GetString();");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return dict;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private List<Dictionary<string, string>> ReadExcelAsRows(string filePath, int headerRow)");
            sb.AppendLine("        {");
            sb.AppendLine("            var rows = new List<Dictionary<string, string>>();");
            sb.AppendLine("            using (var wb = new XLWorkbook(filePath))");
            sb.AppendLine("            {");
            sb.AppendLine("                var ws = wb.Worksheet(1);");
            sb.AppendLine("                var lastRow = ws.LastRowUsed() != null ? ws.LastRowUsed().RowNumber() : 0;");
            sb.AppendLine("                var lastCol = ws.LastColumnUsed() != null ? ws.LastColumnUsed().ColumnNumber() : 0;");
            sb.AppendLine("                var headers = new List<string>();");
            sb.AppendLine("                for (int c = 1; c <= lastCol; c++) headers.Add(ws.Cell(headerRow, c).GetString());");
            sb.AppendLine("                for (int r = headerRow + 1; r <= lastRow; r++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var row = new Dictionary<string, string>();");
            sb.AppendLine("                    for (int c = 1; c <= lastCol; c++)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        var key = (c - 1 < headers.Count && !string.IsNullOrEmpty(headers[c - 1])) ? headers[c - 1] : (\"Column\" + c);");
            sb.AppendLine("                        row[key] = ws.Cell(r, c).GetString();");
            sb.AppendLine("                    }");
            sb.AppendLine("                    rows.Add(row);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return rows;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void WriteExcel(string filePath, List<string> headers, List<List<string>> rows)");
            sb.AppendLine("        {");
            sb.AppendLine("            using (var wb = new XLWorkbook())");
            sb.AppendLine("            {");
            sb.AppendLine("                var ws = wb.Worksheets.Add(\"Sheet1\");");
            sb.AppendLine("                if (headers != null) for (int c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c] ?? string.Empty;");
            sb.AppendLine("                if (rows != null) for (int r = 0; r < rows.Count; r++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var row = rows[r] ?? new List<string>();");
            sb.AppendLine("                    for (int c = 0; c < row.Count; c++) ws.Cell(r + 2, c + 1).Value = row[c] ?? string.Empty;");
            sb.AppendLine("                }");
            sb.AppendLine("                var __dir = Path.GetDirectoryName(filePath);");
            sb.AppendLine("                if (!string.IsNullOrEmpty(__dir) && !Directory.Exists(__dir)) Directory.CreateDirectory(__dir);");
            sb.AppendLine("                wb.SaveAs(filePath);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// Build the Roslyn reference set — every loaded assembly with a location,
        /// plus a guaranteed RevitAPI / RevitAPIUI / ClosedXML. Shared by the
        /// execution path AND the compile-only gate so both see the IDENTICAL
        /// reference surface (a divergent set would give false pass/fail).
        /// </summary>
        private static List<MetadataReference> BuildReferences()
        {
            var references = new List<MetadataReference>();

            // Add references from all currently loaded assemblies that have a location
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                    {
                        references.Add(MetadataReference.CreateFromFile(assembly.Location));
                    }
                }
                catch
                {
                    // Skip assemblies that can't be referenced
                }
            }

            // Helper: make sure a specific assembly is in the reference set.
            void EnsureRef(Assembly asm, string nameHint)
            {
                try
                {
                    if (asm == null || string.IsNullOrEmpty(asm.Location)) return;
                    if (references.Any(r => r.Display != null && r.Display.IndexOf(nameHint, StringComparison.OrdinalIgnoreCase) >= 0)) return;
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
                }
                catch { }
            }

            // Ensure Revit API is included — the LOADED (running version's) assemblies,
            // so the compiler catches version-specific hallucinations (a 2024 member
            // used against 2026 references fails to compile).
            EnsureRef(typeof(Document).Assembly, "RevitAPI");
            EnsureRef(typeof(UIDocument).Assembly, "RevitAPIUI");

            // Ensure ClosedXML is included — the wrapper may `using ClosedXML.Excel;`
            // and use the WriteExcel/ReadExcel* helpers. typeof(...) also forces a load.
            try { EnsureRef(typeof(ClosedXML.Excel.XLWorkbook).Assembly, "ClosedXML"); } catch { }

            return references;
        }

        /// <summary>
        /// Compile code using Roslyn
        /// </summary>
        private static Assembly CompileCode(string code, int userCodeLineOffset, out AssemblyLoadContext loadContext)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(code);

            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false);

            var compilation = CSharpCompilation.Create(
                $"AIGenerated_{Guid.NewGuid():N}",
                new[] { syntaxTree },
                BuildReferences(),
                compilationOptions);

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            if (!emitResult.Success)
                throw new CompilationException("Compilation failed:\n" + FormatErrors(emitResult.Diagnostics, userCodeLineOffset));

            ms.Seek(0, SeekOrigin.Begin);

            // Load into collectible context so the caller can Unload() after use.
            loadContext = new AssemblyLoadContext(null, isCollectible: true);
            return loadContext.LoadFromStream(ms);
        }
    }

    public class ExecutionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        /// <summary>JSON of the snippet's structured return value (real model data), when it
        /// returned an object/array rather than a status string. Drives the Copilot result card.</summary>
        public string Data { get; set; }
    }

    public class CompilationException : Exception
    {
        public CompilationException(string message) : base(message) { }
    }
}
