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
            try
            {
                // Wrap code in executable class
                string fullCode = WrapCode(code);

                // Compile
                var assembly = CompileCode(fullCode);

                // Execute
                var type = assembly.GetType("RevitWebAppSync.Dynamic.AIGeneratedCode");
                var method = type.GetMethod("Execute");
                var instance = Activator.CreateInstance(type);

                var result = method.Invoke(instance, new object[] { _doc, _uidoc, _activeView });

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
        /// Wrap user code in a class structure
        /// </summary>
        private string WrapCode(string userCode)
        {
            var sb = new StringBuilder();

            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using Autodesk.Revit.DB;");
            sb.AppendLine("using Autodesk.Revit.DB.Architecture;");
            sb.AppendLine("using Autodesk.Revit.UI;");
            sb.AppendLine();
            sb.AppendLine("namespace RevitWebAppSync.Dynamic");
            sb.AppendLine("{");
            sb.AppendLine("    public class AIGeneratedCode");
            sb.AppendLine("    {");
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
            sb.AppendLine("        public object Execute(Document doc, UIDocument uidoc, View activeView)");
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
