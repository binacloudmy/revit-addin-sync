using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using System;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Handles code execution on Revit's main thread via ExternalEvent
    /// </summary>
    public class CodeExecutionHandler : IExternalEventHandler
    {
        public string CodeToExecute { get; set; }
        public Action<ExecutionResult> OnCompleted { get; set; }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(CodeToExecute))
            {
                OnCompleted?.Invoke(new ExecutionResult
                {
                    Success = false,
                    Error = "No code to execute"
                });
                return;
            }

            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null)
                {
                    OnCompleted?.Invoke(new ExecutionResult
                    {
                        Success = false,
                        Error = "No active document. Please open a Revit project first."
                    });
                    return;
                }

                var executor = new CodeExecutor(uidoc);
                var result = executor.Execute(CodeToExecute);

                OnCompleted?.Invoke(result);
            }
            catch (Exception ex)
            {
                OnCompleted?.Invoke(new ExecutionResult
                {
                    Success = false,
                    Error = $"Execution failed: {ex.Message}"
                });
            }
            finally
            {
                CodeToExecute = null;
            }
        }

        public string GetName()
        {
            return "AI Code Execution Handler";
        }
    }
}
