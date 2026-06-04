using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Bounded compile-feedback self-heal loop. Pure logic (no Revit dependency)
    /// so the retry policy is unit-testable on any .NET host.
    ///
    /// The loop runs the generated code, and on failure feeds the REAL
    /// compile/runtime error back to a regenerate function, re-executing until
    /// success or <paramref name="maxAttempts"/> retries are exhausted. On
    /// success the verified (error, working_code) pair is recorded via
    /// <c>recordFn</c> so recipe auto-grow can gate on a real run result.
    ///
    /// Delegate contracts:
    ///   executeFn(code)               -> ExecutionResult   (compile + run a snippet)
    ///   retryFn(code, error, attempt) -> string?           (regenerate; null = give up)
    ///   recordFn(error, workingCode)  -> void              (report the verified fix)
    /// </summary>
    public static class SelfHeal
    {
        public static ExecutionResult RunWithRetries(
            string initialCode,
            Func<string, ExecutionResult> executeFn,
            Func<string, string, int, string> retryFn,
            Action<string, string> recordFn,
            int maxAttempts = 2)
        {
            if (executeFn == null) throw new ArgumentNullException(nameof(executeFn));

            string currentCode = initialCode;
            var result = executeFn(currentCode) ?? new ExecutionResult { Success = false, Error = "No result." };

            // Remember the error that the LAST failed execution produced, so a
            // recordFn after a successful heal reports the error it actually fixed.
            string lastError = result.Error;

            int attempt = 1;
            while (!result.Success && attempt <= maxAttempts)
            {
                string newCode = retryFn != null ? retryFn(currentCode, result.Error, attempt) : null;
                if (string.IsNullOrEmpty(newCode))
                    break;   // backend couldn't produce a fix — stop, surface the last error

                currentCode = newCode;
                result = executeFn(currentCode) ?? new ExecutionResult { Success = false, Error = "No result." };
                if (!result.Success) lastError = result.Error;
                attempt++;
            }

            // Only record a fix that actually ran. Never record on exhaustion.
            if (result.Success)
                recordFn?.Invoke(lastError, currentCode);

            return result;
        }
    }
}
