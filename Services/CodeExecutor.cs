using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CSharp;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        /// Compile code using legacy CSharpCodeProvider - no Roslyn needed
        /// </summary>
        private Assembly CompileCode(string code)
        {
            var providerOptions = new Dictionary<string, string>
            {
                { "CompilerVersion", "v4.0" }
            };

            var provider = new CSharpCodeProvider(providerOptions);

            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TreatWarningsAsErrors = false,
                CompilerOptions = "/optimize"
            };

            // Add system references
            parameters.ReferencedAssemblies.Add("System.dll");
            parameters.ReferencedAssemblies.Add("System.Core.dll");
            parameters.ReferencedAssemblies.Add("mscorlib.dll");

            // Add Revit API references
            string revitPath = GetRevitInstallPath();
            parameters.ReferencedAssemblies.Add(Path.Combine(revitPath, "RevitAPI.dll"));
            parameters.ReferencedAssemblies.Add(Path.Combine(revitPath, "RevitAPIUI.dll"));

            var results = provider.CompileAssemblyFromSource(parameters, code);

            if (results.Errors.HasErrors)
            {
                var errors = new StringBuilder("Compilation failed:\n");
                foreach (CompilerError error in results.Errors)
                {
                    if (!error.IsWarning)
                    {
                        // Adjust line number for wrapper code offset
                        int adjustedLine = error.Line - 17;
                        errors.AppendLine($"Line {adjustedLine}: {error.ErrorText}");
                    }
                }
                throw new CompilationException(errors.ToString());
            }

            return results.CompiledAssembly;
        }

        /// <summary>
        /// Get Revit installation path from loaded assembly
        /// </summary>
        private string GetRevitInstallPath()
        {
            var revitAssembly = typeof(Document).Assembly;
            return Path.GetDirectoryName(revitAssembly.Location);
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
