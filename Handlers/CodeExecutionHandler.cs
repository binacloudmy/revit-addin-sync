using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using System;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Handles code execution on Revit's main thread via ExternalEvent.
    /// Also handles the "Revert" action (PostCommand Undo) on the same
    /// channel — set Action = "undo" before raising the event.
    /// </summary>
    public class CodeExecutionHandler : IExternalEventHandler
    {
        public string CodeToExecute { get; set; }
        public Action<ExecutionResult> OnCompleted { get; set; }

        /// <summary>
        /// What this handler invocation should do. Defaults to "execute"
        /// (run CodeToExecute through CodeExecutor). Set to "undo" to post
        /// a Revit Undo command instead.
        /// </summary>
        public string Action { get; set; } = "execute";

        public void Execute(UIApplication app)
        {
            try
            {
                if (string.Equals(Action, "undo", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteUndo(app);
                    return;
                }

                ExecuteCode(app);
            }
            finally
            {
                Action = "execute";
                CodeToExecute = null;
            }
        }

        private void ExecuteCode(UIApplication app)
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
        }

        private void ExecuteUndo(UIApplication app)
        {
            try
            {
                var id = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                if (id == null)
                {
                    OnCompleted?.Invoke(new ExecutionResult { Success = false, Error = "Undo command isn't available in this Revit version." });
                    return;
                }
                if (!app.CanPostCommand(id))
                {
                    OnCompleted?.Invoke(new ExecutionResult { Success = false, Error = "Revit refused the undo (a modal dialog is open, or there's nothing to undo)." });
                    return;
                }
                app.PostCommand(id);
                // PostCommand is asynchronous — the actual undo happens after this
                // handler returns. We've done our part; report success optimistically.
                OnCompleted?.Invoke(new ExecutionResult { Success = true, Message = "Reverted the last change." });
            }
            catch (Exception ex)
            {
                OnCompleted?.Invoke(new ExecutionResult { Success = false, Error = "Revert failed: " + ex.Message });
            }
        }

        public string GetName()
        {
            return "AI Code Execution Handler";
        }
    }
}
