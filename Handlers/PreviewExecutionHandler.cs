using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using System;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Handles preview execution on Revit's main thread via ExternalEvent
    /// Executes code and captures changes without committing
    /// </summary>
    public class PreviewExecutionHandler : IExternalEventHandler
    {
        /// <summary>
        /// Code to preview
        /// </summary>
        public string CodeToPreview { get; set; }

        /// <summary>
        /// AI explanation of what the code does
        /// </summary>
        public string Explanation { get; set; }

        /// <summary>
        /// Callback when preview is complete
        /// </summary>
        public Action<ExecutionPreview> OnCompleted { get; set; }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(CodeToPreview))
            {
                OnCompleted?.Invoke(new ExecutionPreview
                {
                    Success = false,
                    Error = "No code to preview"
                });
                return;
            }

            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null)
                {
                    OnCompleted?.Invoke(new ExecutionPreview
                    {
                        Success = false,
                        Error = "No active document. Please open a Revit project first."
                    });
                    return;
                }

                var executor = new CodeExecutor(uidoc);
                var preview = executor.PreviewExecute(CodeToPreview, Explanation);

                OnCompleted?.Invoke(preview);
            }
            catch (Exception ex)
            {
                OnCompleted?.Invoke(new ExecutionPreview
                {
                    Success = false,
                    Error = $"Preview failed: {ex.Message}"
                });
            }
            finally
            {
                CodeToPreview = null;
                Explanation = null;
            }
        }

        public string GetName()
        {
            return "AI Preview Execution Handler";
        }
    }
}
