# Fix: runtime errors ("Value cannot be null") are information-starved — self-heal can't fix them and the user sees no detail

## Context

Latest gazebo trace (`Chat/1782899613001-lf-events-export-...json`) shows real progress: the code now **compiles and runs** (the backend API-hallucination fix is live — the generated code correctly uses `Floor.Create` and `ROOF_LEVEL_OFFSET_PARAM`, and there is no assembly-conflict compile error on this run). It now fails at **runtime**:

```
Sorry — that didn't run. Execution error: Value cannot be null.
```

`Value cannot be null` is a .NET `ArgumentNullException` — the generated C# passed `null` into a Revit API call while executing. Two problems compound:

1. **The error is stripped of everything actionable.** `CodeExecutor.cs:114` returns only `innerEx.Message` — no exception type, no parameter name, no line number, no stack frame. For an `ArgumentNullException` the user (and the self-heal model) sees just "Value cannot be null." with no clue *which* value or *where*.
2. **So the self-heal retry loop can't repair it.** `RevitCopilotExecutor.cs:102` runs `SelfHeal.RunWithRetries`, feeding the error back to the backend via `Ai.RetryCodeAsync(prompt, failedCode, error, ...)`. Compile errors self-heal well because they carry line numbers (`"Line 58: ..."`). A bare "Value cannot be null" gives the model nothing to change, so every retry regenerates the same class of code and, after `MaxAttempts`, the user gets "Sorry — that didn't run."

The Langfuse export **cannot** currently tell us which argument was null either — the exception is thrown client-side in the Revit addin and only the bare `.Message` is sent back. Fixing the diagnostics is therefore the prerequisite to fixing the null itself.

Most likely culprit in the gazebo code (needs confirmation once diagnostics land): the raised-elevation roof footprint passed to `doc.Create.NewFootPrintRoof(footprint, level, roofType, out mapping)`, or a type/level lookup that resolved to `null` on this specific model. The code guards `level`, `wallType`, `floorType`, and `roofType`, so the null is in an argument that *isn't* guarded — exactly what a parameter name would reveal.

## The fix (revit-addin-sync — C#)

### Change 1 — enrich the runtime error in `CodeExecutor.cs` (lines 108-134)
Replace the bare `innerEx.Message` with type + parameter name + first user-code stack frame. Add a small formatter and use it in the `TargetInvocationException` and generic `Exception` catches:

```csharp
private static string DescribeRuntimeError(Exception ex, int userCodeLineOffset)
{
    var parts = new List<string> { ex.GetType().Name + ": " + ex.Message };

    // ArgumentNullException / ArgumentException expose the offending parameter.
    if (ex is ArgumentException ae && !string.IsNullOrEmpty(ae.ParamName))
        parts.Add($"parameter: {ae.ParamName}");

    // First stack frame that maps to the user's snippet (has a line number),
    // adjusted by the same offset used for compile diagnostics.
    var st = new System.Diagnostics.StackTrace(ex, true);
    foreach (var f in st.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
    {
        int line = f.GetFileLineNumber();
        if (line > 0)
        {
            parts.Add($"at user code line {Math.Max(1, line - userCodeLineOffset)}");
            break;
        }
    }
    return string.Join(" | ", parts);
}
```

Then at line 108-115:
```csharp
catch (TargetInvocationException ex)
{
    var innerEx = ex.InnerException ?? ex;
    return new ExecutionResult
    {
        Success = false,
        Error = "Execution error: " + DescribeRuntimeError(innerEx, userCodeLineOffset)
    };
}
```
(`userCodeLineOffset` is already threaded through `CompileCode`/`FormatErrors`; pass it into the executing method or capture it where the snippet is invoked. If it is not in scope at the catch, fall back to the raw line number without the offset — still far better than nothing.)

Do the same in the generic `catch (Exception ex)` at line 127-133.

### Change 2 — keep the user message short, send the detail to self-heal
The Copilot card should still show a short "that didn't run" to the drafter (per the prompt's "never narrate internal mechanics" rule), but the **enriched** `Error` must reach `Ai.RetryCodeAsync` so the backend model can see the parameter name + line. That already happens — `RunWithSelfHeal` passes `result.Error` straight through — so once Change 1 lands, self-heal automatically gets the richer signal. No change needed in `RevitCopilotExecutor.cs`; just verify the enriched string is what flows into `retryFn`'s `error` argument.

### Change 3 (optional) — log the full stack for offline diagnosis
In the catch, also `System.Diagnostics.Debug.WriteLine($"[BinaVibe] runtime error: {innerEx}")` (full `ToString()` includes the stack) so the affected machine's log has the complete trace even if the wire message stays short.

## Tests

Mirror `revit-addin-sync/Tests/SelfHealLoopTests.cs`:
- Unit-test `DescribeRuntimeError` directly (it's pure): pass an `ArgumentNullException("footprint")` and assert the output contains `ArgumentNullException`, `parameter: footprint`. Pass a plain `InvalidOperationException("boom")` and assert it contains the type + message and does not crash when there is no stack line.
- If `DescribeRuntimeError` is private, expose it `internal` + `[assembly: InternalsVisibleTo(...)]` (the test project already uses this pattern) or test it via the public `Execute` path with a snippet that throws.

## Verification

1. Build: `dotnet build revit-addin-sync/RevitWebAppSync.csproj -c Release`.
2. Test: `dotnet test revit-addin-sync`.
3. On a Revit machine, run the gazebo prompt again. The "Execution error" should now read e.g. `Execution error: ArgumentNullException: Value cannot be null. | parameter: roofType | at user code line 63`, and the self-heal "Fixing… (attempt N)" pass should now be able to add the missing guard and succeed — or, if it still fails, the trace/log will finally name the null so we can fix the codegen.

## Deployment note

Client-side C# change → rebuild the addin, redeploy to the Revit machines, and restart Revit. No bina-ai change required for this diagnostic fix (though the backend self-heal will benefit automatically from the richer error).

## Relationship to the other two fixes

This is the **third** distinct issue in the gazebo saga, each in a different place:
1. Backend API hallucination (`NewFloor` / `ROOF_BASE_OFFSET_PARAM`) — fixed in `bina-ai`. ✅
2. Addin Roslyn duplicate-assembly compile crash — see `ROSLYN_DUPLICATE_ASSEMBLY_FIX_PLAN.md` (not yet applied).
3. Addin runtime-error diagnostics (this doc) — makes the remaining runtime `null` fixable instead of a dead-end "Sorry".
```
