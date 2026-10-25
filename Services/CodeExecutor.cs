using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Legacy entry point kept so its old callers (Handlers/CodeExecutionHandler.cs,
    /// UI/Copilot/RevitCopilotExecutor.cs) still compile until the lean-harness
    /// cleanup deletes them; the wrapper itself goes at integration.
    ///
    /// The Roslyn compile, the reference set and the wrapper now live in
    /// BinaVibe/Copilot/Code (run_revit_code's gate), so this path gets the same
    /// deny-by-default policy, loop budget and error mapping. The old wrapper's
    /// helpers are gone: no Excel (ReadExcel*/WriteExcel/FindExcelFile, spreadsheet
    /// I/O moves to the pane), no SetResult/ShowMessage/OpenView (use Print(obj) and
    /// uidoc.RequestViewChange), and the code may not open transactions: reads run
    /// in a rolled-back group, writes in one "BINA: AI code" group.
    /// </summary>
    public class CodeExecutor
    {
        private readonly UIDocument _uidoc;

        public CodeExecutor(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        /// <summary>Compile, check and run AI-generated code on Revit's thread.</summary>
        public ExecutionResult Execute(string code)
        {
            try
            {
                var doc = _uidoc.Document;
                var args = RunRevitCodeArgs.Create(code, "run code");
                var prepared = RevitCodeHost.Prepare(args.Code);
                if (prepared.Failure != null)
                    return new ExecutionResult { Success = false, Error = prepared.Failure.Message };

                Envelope envelope;
                if (CodePlans.ModeOf(args, prepared) == CodeMode.Read)
                {
                    envelope = RevitCodeHost.Read(doc, _uidoc, args, prepared);
                }
                else
                {
                    // This legacy path had its own approval before calling here; it commits directly.
                    using (CallbackScope.Enter(doc, CallbackKind.Commit))
                    using (FailureLog.Begin())
                    using (var group = new TransactionGroup(doc, "BINA: AI code"))
                    {
                        group.Start();
                        envelope = RevitCodeHost.Write(doc, _uidoc, null, args, prepared, WritePhase.Commit);
                        if (envelope.Ok) group.Assimilate();
                        else group.RollBack();
                    }
                }
                return ToResult(envelope);
            }
            catch (Exception ex)
            {
                return new ExecutionResult { Success = false, Error = "Error: " + ex.Message };
            }
        }

        /// <summary>
        /// Compile-and-policy check only: never runs the code or opens a transaction, so it is
        /// safe on a worker thread. Returns null when the code would be allowed to run, else the
        /// error text (compile errors with the code's own line numbers, or the policy rejection).
        /// </summary>
        public static string CompileCheck(string userCode)
        {
            try
            {
                return RevitCodeHost.Prepare(userCode ?? "").Failure?.Message;
            }
            catch (Exception ex)
            {
                // A check that itself throws must not block execution: the run path reports it.
                System.Diagnostics.Debug.WriteLine($"[BinaVibe] CompileCheck threw, skipping gate: {ex.Message}");
                return null;
            }
        }

        private static ExecutionResult ToResult(Envelope envelope)
        {
            var output = "";
            if (envelope.Data is JsonElement data && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String)
                output = o.GetString() ?? "";

            if (!envelope.Ok)
                return new ExecutionResult { Success = false, Error = envelope.Error?.Message ?? envelope.Summary };

            var lines = new List<string> { envelope.Summary };
            if (output.Length > 0) lines.Add(output);
            return new ExecutionResult
            {
                Success = true,
                Message = string.Join("\n", lines),
                Data = envelope.Data?.GetRawText(),
            };
        }
    }

    // ExecutionResult lives in ExecutionResult.cs — it is a dependency-free DTO.

    public class CompilationException : Exception
    {
        public CompilationException(string message) : base(message) { }
    }
}
