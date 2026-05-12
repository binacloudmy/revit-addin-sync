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

namespace RevitWebAppSync.Services
{
    public class CodeExecutor
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly View _activeView;

        // Number of generated wrapper lines that precede the first line of user
        // code — set by WrapCode, used to map compile errors back to user lines.
        private int _userCodeLineOffset;

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
                string fullCode = WrapCode(code);

                // Compile
                var assembly = CompileCode(fullCode, out loadContext);

                // Execute
                var type = assembly.GetType("RevitWebAppSync.Dynamic.AIGeneratedCode");
                var method = type.GetMethod("Execute");
                var instance = Activator.CreateInstance(type);

                var result = method.Invoke(instance, new object[] { _doc, _uidoc, _activeView });
                string message = result?.ToString() ?? "Executed successfully";

                return new ExecutionResult
                {
                    Success = true,
                    Message = message
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
        /// Wrap user code in a class structure
        /// </summary>
        private string WrapCode(string userCode)
        {
            var sb = new StringBuilder();
            var code = userCode ?? string.Empty;

            // Only pull ClosedXML into the wrapper when the snippet actually needs
            // it — keeps non-Excel snippets free of that dependency (and its
            // compile-time failure surface).
            bool needsExcel = code.Contains("ReadExcel") || code.Contains("WriteExcel")
                              || code.Contains("FindExcelFile") || code.Contains("XLWorkbook");

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
            sb.AppendLine("        private void OpenView(View view)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (view != null && this.uidoc != null) this.uidoc.RequestViewChange(view);");
            sb.AppendLine("        }");
            sb.AppendLine();
            if (needsExcel) AppendExcelHelpers(sb);
            sb.AppendLine("        public object Execute(Document doc, UIDocument uidoc, View activeView)");
            sb.AppendLine("        {");
            sb.AppendLine("            this.doc = doc; this.uidoc = uidoc; this.activeView = activeView;");

            // If the generated code doesn't manage its own transaction, wrap the
            // whole body in one so model edits actually commit (and get a named
            // undo entry) on the first attempt — instead of failing with
            // "there is no open transaction" and only succeeding on the retry.
            // Heuristic: if the code mentions "Transaction" at all, assume it
            // handles its own and don't double-wrap (nested Transactions are an
            // error in the Revit API).
            bool selfManagesTransaction = code.Contains("Transaction");
            string bodyIndent = selfManagesTransaction ? "            " : "                ";

            if (!selfManagesTransaction)
            {
                sb.AppendLine("            using (var __tx = new Transaction(doc, \"AI Assistant\"))");
                sb.AppendLine("            {");
                sb.AppendLine("                __tx.Start();");
            }

            // Count preamble lines so CompileCode can map errors to user lines.
            _userCodeLineOffset = sb.ToString().Split('\n').Length - 1;

            var lines = code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                sb.AppendLine(bodyIndent + line);
            }

            if (!selfManagesTransaction)
            {
                sb.AppendLine("                __tx.Commit();");
                sb.AppendLine("            }");
            }

            sb.AppendLine();
            sb.AppendLine("            return __aiOutput.Length > 0 ? __aiOutput.ToString().TrimEnd() : \"Done\";");
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
            sb.AppendLine("                wb.SaveAs(filePath);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// Compile code using Roslyn
        /// </summary>
        private Assembly CompileCode(string code, out AssemblyLoadContext loadContext)
        {
            // Force ClosedXML to load so it appears in GetAssemblies() below — the
            // generated wrapper has `using ClosedXML.Excel;` and the Excel helpers,
            // so its assembly must be referenced even if nothing else touched it yet.
            _ = typeof(ClosedXML.Excel.XLWorkbook);

            var syntaxTree = CSharpSyntaxTree.ParseText(code);

            // Collect all necessary references
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

            // Ensure Revit API is included
            var revitApiAssembly = typeof(Document).Assembly;
            var revitApiUiAssembly = typeof(UIDocument).Assembly;

            if (!references.Any(r => r.Display?.Contains("RevitAPI") == true))
            {
                references.Add(MetadataReference.CreateFromFile(revitApiAssembly.Location));
            }
            if (!references.Any(r => r.Display?.Contains("RevitAPIUI") == true))
            {
                references.Add(MetadataReference.CreateFromFile(revitApiUiAssembly.Location));
            }

            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false);

            var compilation = CSharpCompilation.Create(
                $"AIGenerated_{Guid.NewGuid():N}",
                new[] { syntaxTree },
                references,
                compilationOptions);

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            if (!emitResult.Success)
            {
                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d =>
                    {
                        var lineSpan = d.Location.GetLineSpan();
                        // Map back to the user's line numbering (WrapCode counted the preamble).
                        var adjustedLine = Math.Max(1, lineSpan.StartLinePosition.Line - _userCodeLineOffset + 1);
                        return $"Line {adjustedLine}: {d.GetMessage()}";
                    });

                throw new CompilationException("Compilation failed:\n" + string.Join("\n", errors));
            }

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
    }

    public class CompilationException : Exception
    {
        public CompilationException(string message) : base(message) { }
    }
}
