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
            sb.AppendLine("        public object Execute(Document doc, UIDocument uidoc, View activeView)");
            sb.AppendLine("        {");
            sb.AppendLine("            this.doc = doc; this.uidoc = uidoc; this.activeView = activeView;");

            var lines = userCode.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                sb.AppendLine("            " + line);
            }

            sb.AppendLine();
            sb.AppendLine("            return __aiOutput.Length > 0 ? __aiOutput.ToString().TrimEnd() : \"Done\";");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Compile code using Roslyn
        /// </summary>
        private Assembly CompileCode(string code, out AssemblyLoadContext loadContext)
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
                        // 41 wrapper lines precede the first line of user code (see WrapCode).
                        var adjustedLine = Math.Max(1, lineSpan.StartLinePosition.Line - 40);
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
