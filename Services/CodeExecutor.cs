using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
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
                // Unwrap the inner exception (actual error from generated code)
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

            // Add using statements
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using Autodesk.Revit.DB;");
            sb.AppendLine("using Autodesk.Revit.DB.Architecture;");
            sb.AppendLine("using Autodesk.Revit.UI;");
            sb.AppendLine();

            // Namespace and class
            sb.AppendLine("namespace RevitWebAppSync.Dynamic");
            sb.AppendLine("{");
            sb.AppendLine("    public class AIGeneratedCode");
            sb.AppendLine("    {");

            // Helper method for safe parameter access
            sb.AppendLine("        private string GetParameterValue(Element elem, BuiltInParameter param)");
            sb.AppendLine("        {");
            sb.AppendLine("            var p = elem.get_Parameter(param);");
            sb.AppendLine("            if (p == null) return \"N/A\";");
            sb.AppendLine("            return p.AsString() ?? p.AsValueString() ?? \"N/A\";");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Main execute method
            sb.AppendLine("        public object Execute(Document doc, UIDocument uidoc, View activeView)");
            sb.AppendLine("        {");

            // Indent user code
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
        /// Compile code using Roslyn to in-memory assembly
        /// </summary>
        private Assembly CompileCode(string code)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(code);

            // Gather references
            var references = new List<MetadataReference>();

            // Add core runtime references
            var runtimePath = Path.GetDirectoryName(typeof(object).Assembly.Location);
            references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimePath, "System.Runtime.dll")));
            references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimePath, "System.Private.CoreLib.dll")));
            references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimePath, "System.Collections.dll")));
            references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimePath, "System.Linq.dll")));
            references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimePath, "netstandard.dll")));

            // Add Revit API references from the loaded assemblies
            var revitApiAssembly = typeof(Document).Assembly;
            var revitApiUiAssembly = typeof(UIDocument).Assembly;
            references.Add(MetadataReference.CreateFromFile(revitApiAssembly.Location));
            references.Add(MetadataReference.CreateFromFile(revitApiUiAssembly.Location));

            // Also add any additional system assemblies that might be needed
            references.Add(MetadataReference.CreateFromFile(typeof(System.Text.StringBuilder).Assembly.Location));

            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false);

            var compilation = CSharpCompilation.Create(
                $"AIGeneratedAssembly_{Guid.NewGuid():N}",
                new[] { syntaxTree },
                references,
                compilationOptions);

            using var ms = new MemoryStream();
            EmitResult emitResult = compilation.Emit(ms);

            if (!emitResult.Success)
            {
                var errors = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d =>
                    {
                        var lineSpan = d.Location.GetLineSpan();
                        // Adjust line number to account for wrapper code (approximately 25 lines)
                        var adjustedLine = lineSpan.StartLinePosition.Line - 25;
                        return $"Line {adjustedLine}: {d.GetMessage()}";
                    });

                throw new CompilationException("Compilation failed:\n" + string.Join("\n", errors));
            }

            ms.Seek(0, SeekOrigin.Begin);

            // Load assembly in a separate context to avoid conflicts
            var assemblyLoadContext = new AssemblyLoadContext(null, isCollectible: true);
            return assemblyLoadContext.LoadFromStream(ms);
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
