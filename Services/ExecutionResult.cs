namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Outcome of running a generated code snippet. Extracted from CodeExecutor.cs
    /// so the pure-logic consumers (SelfHeal.RunWithRetries) stay unit-testable —
    /// CodeExecutor itself references Revit and Roslyn, which the test project
    /// deliberately does not pull in.
    /// </summary>
    public class ExecutionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        /// <summary>JSON of the snippet's structured return value (real model data), when it
        /// returned an object/array rather than a status string. Drives the Copilot result card.</summary>
        public string Data { get; set; }
    }
}
