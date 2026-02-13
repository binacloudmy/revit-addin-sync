using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RevitWebAppSync.Models;
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

        public CodeExecutor(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
            _activeView = _doc.ActiveView;
        }

        /// <summary>
        /// Preview execution - runs code in a transaction that gets rolled back
        /// Returns what would change without actually committing
        /// </summary>
        public ExecutionPreview PreviewExecute(string code, string explanation = null)
        {
            var preview = new ExecutionPreview
            {
                Code = code,
                Explanation = explanation
            };

            try
            {
                // Wrap and compile code first (fail fast if compilation error)
                string fullCode = WrapCode(code);
                var assembly = CompileCode(fullCode);

                var type = assembly.GetType("RevitWebAppSync.Dynamic.AIGeneratedCode");
                var method = type.GetMethod("Execute");
                var instance = Activator.CreateInstance(type);

                // Create state tracker to detect changes
                var stateTracker = new ModelStateTracker(_doc, _uidoc);

                // Start a transaction group to allow rollback
                using (var transGroup = new TransactionGroup(_doc, "AI Preview"))
                {
                    transGroup.Start();

                    // Capture state before execution
                    stateTracker.CaptureBeforeState();

                    // Execute code in a transaction
                    using (var transaction = new Transaction(_doc, "AI Code Execution"))
                    {
                        transaction.Start();

                        try
                        {
                            var result = method.Invoke(instance, new object[] { _doc, _uidoc, _activeView });
                            preview.ExecutionMessage = result?.ToString();
                            transaction.Commit();
                        }
                        catch (TargetInvocationException ex)
                        {
                            transaction.RollBack();
                            var innerEx = ex.InnerException ?? ex;
                            preview.Success = false;
                            preview.Error = $"Execution error: {innerEx.Message}";
                            return preview;
                        }
                        catch (Exception ex)
                        {
                            transaction.RollBack();
                            preview.Success = false;
                            preview.Error = $"Error: {ex.Message}";
                            return preview;
                        }
                    }

                    // Detect what changed
                    preview.Changes = stateTracker.DetectChanges();
                    preview.Success = true;

                    // ROLLBACK - don't commit the changes yet
                    transGroup.RollBack();
                }

                return preview;
            }
            catch (CompilationException ex)
            {
                preview.Success = false;
                preview.Error = ex.Message;
                return preview;
            }
            catch (Exception ex)
            {
                preview.Success = false;
                preview.Error = $"Preview error: {ex.Message}";
                return preview;
            }
        }

        /// <summary>
        /// Validate code by compiling without executing
        /// Returns null if valid, error message if invalid
        /// </summary>
        public string ValidateCode(string code)
        {
            try
            {
                string fullCode = WrapCode(code);
                CompileCode(fullCode);
                return null; // Code is valid
            }
            catch (CompilationException ex)
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                return $"Validation error: {ex.Message}";
            }
        }

        /// <summary>
        /// Compile and execute AI-generated code (commits changes)
        /// </summary>
        public ExecutionResult Execute(string code)
        {
            try
            {
                // Wrap code in executable class
                string fullCode = WrapCode(code);

                // Compile
                var assembly = CompileCode(fullCode);

                // Execute within a transaction
                var type = assembly.GetType("RevitWebAppSync.Dynamic.AIGeneratedCode");
                var method = type.GetMethod("Execute");
                var instance = Activator.CreateInstance(type);

                // Get the PendingViewToActivate property
                var pendingViewProperty = type.GetProperty("PendingViewToActivate");

                object result = null;

                using (var transaction = new Transaction(_doc, "AI Code Execution"))
                {
                    transaction.Start();

                    try
                    {
                        result = method.Invoke(instance, new object[] { _doc, _uidoc, _activeView });
                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.RollBack();
                        throw;
                    }
                }

                // After transaction commits, activate pending view if any
                if (pendingViewProperty != null)
                {
                    var pendingView = pendingViewProperty.GetValue(instance) as View;
                    if (pendingView != null)
                    {
                        _uidoc.ActiveView = pendingView;
                    }
                }

                return new ExecutionResult
                {
                    Success = true,
                    Message = result?.ToString() ?? "Executed successfully"
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
        }

        /// <summary>
        /// Sanitize AI-generated code by removing transaction blocks, confirmation dialogs, and return statements
        /// </summary>
        private string SanitizeCode(string code)
        {
            // Remove using statements (we add our own)
            code = Regex.Replace(code, @"^\s*using\s+[\w\.]+;\s*$", "", RegexOptions.Multiline);

            // Remove transaction blocks but keep the inner code
            // Pattern: using (Transaction tx = new Transaction(...)) { tx.Start(); ... tx.Commit(); }
            var transactionPattern = @"using\s*\(\s*Transaction\s+\w+\s*=\s*new\s+Transaction\s*\([^)]+\)\s*\)\s*\{\s*\w+\.Start\(\);\s*([\s\S]*?)\s*\w+\.Commit\(\);\s*\}";
            code = Regex.Replace(code, transactionPattern, "$1", RegexOptions.Multiline);

            // Remove standalone tx.Start() and tx.Commit() calls
            code = Regex.Replace(code, @"^\s*\w+\.(Start|Commit|RollBack)\(\);\s*$", "", RegexOptions.Multiline);

            // Remove Transaction variable declarations
            code = Regex.Replace(code, @"^\s*(using\s+)?(var\s+)?\w+\s*=\s*new\s+Transaction\s*\([^)]+\);\s*$", "", RegexOptions.Multiline);

            // Remove TaskDialog confirmation dialogs (but keep info dialogs for results)
            // Remove: TaskDialog.Show("Confirm", ...) or if (TaskDialog.Show(...) == ...)
            code = Regex.Replace(code, @"if\s*\(\s*TaskDialog\.Show\s*\([^)]+\)\s*[!=]=\s*TaskDialogResult\.\w+\s*\)\s*\{[^}]*return[^}]*\}", "", RegexOptions.Multiline);
            code = Regex.Replace(code, @"if\s*\(\s*TaskDialog\.Show\s*\([^)]+\)\s*[!=]=\s*TaskDialogResult\.\w+\s*\)\s*return;", "", RegexOptions.Multiline);

            // Remove simple return statements only (we add our own "return Success" at the end)
            // Only remove: return; or return "string"; or return true/false;
            // Do NOT remove: return doc.Delete(...) or other method calls
            code = Regex.Replace(code, @"^\s*return\s*;\s*$", "", RegexOptions.Multiline);
            code = Regex.Replace(code, @"^\s*return\s+""[^""]*""\s*;\s*$", "", RegexOptions.Multiline);
            code = Regex.Replace(code, @"^\s*return\s+(true|false|null)\s*;\s*$", "", RegexOptions.Multiline);

            // Remove empty lines created by removals
            code = Regex.Replace(code, @"(\r?\n){3,}", "\n\n");

            return code.Trim();
        }

        /// <summary>
        /// Wrap user code in a class structure
        /// </summary>
        private string WrapCode(string userCode)
        {
            // Sanitize the code first
            userCode = SanitizeCode(userCode);

            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using Autodesk.Revit.DB;");
            sb.AppendLine("using Autodesk.Revit.DB.Architecture;");
            sb.AppendLine("using Autodesk.Revit.UI;");
            sb.AppendLine("using ClosedXML.Excel;");
            sb.AppendLine();
            sb.AppendLine("namespace RevitWebAppSync.Dynamic");
            sb.AppendLine("{");
            sb.AppendLine("    public class AIGeneratedCode");
            sb.AppendLine("    {");
            sb.AppendLine("        // Property to store view to activate after transaction completes");
            sb.AppendLine("        public View PendingViewToActivate { get; private set; }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Get desktop path for file exports");
            sb.AppendLine("        private string DesktopPath => Environment.GetFolderPath(Environment.SpecialFolder.Desktop);");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Safe parameter value extraction");
            sb.AppendLine("        private string GetParameterValue(Element elem, BuiltInParameter param)");
            sb.AppendLine("        {");
            sb.AppendLine("            var p = elem.get_Parameter(param);");
            sb.AppendLine("            if (p == null) return \"N/A\";");
            sb.AppendLine("            return p.AsString() ?? p.AsValueString() ?? \"N/A\";");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Get parameter by name");
            sb.AppendLine("        private string GetParamByName(Element elem, string paramName)");
            sb.AppendLine("        {");
            sb.AppendLine("            var p = elem.LookupParameter(paramName);");
            sb.AppendLine("            if (p == null) return \"N/A\";");
            sb.AppendLine("            return p.AsString() ?? p.AsValueString() ?? \"N/A\";");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Show message to user");
            sb.AppendLine("        private void ShowMessage(string title, string message)");
            sb.AppendLine("        {");
            sb.AppendLine("            TaskDialog.Show(title, message);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Open a view after execution (deferred, outside transaction)");
            sb.AppendLine("        private void OpenView(View view)");
            sb.AppendLine("        {");
            sb.AppendLine("            PendingViewToActivate = view;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Read Excel file and return as dictionary (key column -> value column)");
            sb.AppendLine("        private Dictionary<string, string> ReadExcelAsDictionary(string filePath, int keyColumn = 1, int valueColumn = 2, int startRow = 2)");
            sb.AppendLine("        {");
            sb.AppendLine("            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);");
            sb.AppendLine("            using (var workbook = new XLWorkbook(filePath))");
            sb.AppendLine("            {");
            sb.AppendLine("                var worksheet = workbook.Worksheet(1);");
            sb.AppendLine("                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;");
            sb.AppendLine("                for (int row = startRow; row <= lastRow; row++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var key = worksheet.Cell(row, keyColumn).GetString()?.Trim();");
            sb.AppendLine("                    var value = worksheet.Cell(row, valueColumn).GetString()?.Trim();");
            sb.AppendLine("                    if (!string.IsNullOrEmpty(key) && !result.ContainsKey(key))");
            sb.AppendLine("                        result[key] = value ?? \"\";");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Read Excel file and return all rows as list of dictionaries");
            sb.AppendLine("        private List<Dictionary<string, string>> ReadExcelAsRows(string filePath, int headerRow = 1)");
            sb.AppendLine("        {");
            sb.AppendLine("            var result = new List<Dictionary<string, string>>();");
            sb.AppendLine("            using (var workbook = new XLWorkbook(filePath))");
            sb.AppendLine("            {");
            sb.AppendLine("                var worksheet = workbook.Worksheet(1);");
            sb.AppendLine("                var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;");
            sb.AppendLine("                var lastCol = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;");
            sb.AppendLine("                var headers = new List<string>();");
            sb.AppendLine("                for (int col = 1; col <= lastCol; col++)");
            sb.AppendLine("                    headers.Add(worksheet.Cell(headerRow, col).GetString()?.Trim() ?? $\"Column{col}\");");
            sb.AppendLine("                for (int row = headerRow + 1; row <= lastRow; row++)");
            sb.AppendLine("                {");
            sb.AppendLine("                    var rowData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);");
            sb.AppendLine("                    for (int col = 1; col <= lastCol; col++)");
            sb.AppendLine("                        rowData[headers[col - 1]] = worksheet.Cell(row, col).GetString()?.Trim() ?? \"\";");
            sb.AppendLine("                    result.Add(rowData);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Write data to Excel file");
            sb.AppendLine("        private void WriteExcel(string filePath, List<string> headers, List<List<string>> rows)");
            sb.AppendLine("        {");
            sb.AppendLine("            using (var workbook = new XLWorkbook())");
            sb.AppendLine("            {");
            sb.AppendLine("                var worksheet = workbook.Worksheets.Add(\"Data\");");
            sb.AppendLine("                for (int col = 0; col < headers.Count; col++)");
            sb.AppendLine("                    worksheet.Cell(1, col + 1).Value = headers[col];");
            sb.AppendLine("                for (int row = 0; row < rows.Count; row++)");
            sb.AppendLine("                    for (int col = 0; col < rows[row].Count; col++)");
            sb.AppendLine("                        worksheet.Cell(row + 2, col + 1).Value = rows[row][col];");
            sb.AppendLine("                worksheet.Columns().AdjustToContents();");
            sb.AppendLine("                workbook.SaveAs(filePath);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Helper: Find Excel file by name in folder (with or without extension)");
            sb.AppendLine("        private string FindExcelFile(string folderPath, string fileName)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!Directory.Exists(folderPath)) return null;");
            sb.AppendLine("            var extensions = new[] { \".xlsx\", \".xls\", \".xlsm\" };");
            sb.AppendLine("            foreach (var ext in extensions)");
            sb.AppendLine("            {");
            sb.AppendLine("                var path = Path.Combine(folderPath, fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ext);");
            sb.AppendLine("                if (File.Exists(path)) return path;");
            sb.AppendLine("            }");
            sb.AppendLine("            // Try to find partial match");
            sb.AppendLine("            var files = Directory.GetFiles(folderPath, \"*.xls*\");");
            sb.AppendLine("            return files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public object Execute(Document doc, UIDocument uidoc, View activeView)")
            sb.AppendLine("        {");

            var lines = userCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                sb.AppendLine("            " + line);
            }

            sb.AppendLine();
            sb.AppendLine("            return \"Success\";");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Compile code using Roslyn
        /// </summary>
        private Assembly CompileCode(string code)
        {
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
                        var adjustedLine = Math.Max(1, lineSpan.StartLinePosition.Line - 16);
                        return $"Line {adjustedLine}: {d.GetMessage()}";
                    });

                throw new CompilationException("Compilation failed:\n" + string.Join("\n", errors));
            }

            ms.Seek(0, SeekOrigin.Begin);

            // Load into collectible context to allow unloading
            var context = new AssemblyLoadContext(null, isCollectible: true);
            return context.LoadFromStream(ms);
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
