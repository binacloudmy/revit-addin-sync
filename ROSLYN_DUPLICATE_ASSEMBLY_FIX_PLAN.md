# Fix: Roslyn "same simple name has already been imported" — codegen never compiles

## Context

On machines with a co-installed Revit add-in (observed: **IssuesManagement** on **Revit 2027**), every BINA Copilot code request fails to compile on **line 1** with:

```
Compilation failed:
Line 1: An assembly with the same simple name 'Autodesk.Http.JsonApi' has already been imported.
        Try removing one of the references (e.g. 'D:\WORK\Autodesk\Revit 2027\AddIns\IssuesManagement\Autodesk.Http.JsonApi.dll') ...
Line 1: An assembly with the same simple name 'Autodesk.Http.DevPortal' has already been imported. ...
```

Because it fails at line 1 (reference resolution), the generated C# never runs — the user just sees "that didn't run" and nothing is built. This blocks **all** prompts on the affected machine, not just the gazebo. It is a **separate bug** from the backend API-hallucination bug (ClickUp 86ey3udbm, fixed in `bina-ai`); this one is entirely in the Revit add-in.

## Root cause

`revit-addin-sync/Services/CodeExecutor.cs` → `BuildReferencesUncached()` (line 517) collects Roslyn metadata references from every loaded assembly and already tries to de-duplicate by simple name (`seenSimpleNames`, line 529). **But it de-dups on the wrong key.**

- Line 538 uses `assembly.GetName().Name` — the assembly's **runtime** identity.
- Roslyn rejects duplicates based on the name stored in the **file manifest**, which it reads via `MetadataReference.CreateFromFile(location)`.

These two names are usually equal, but **not always**: a co-installed add-in (IssuesManagement) ships its own `Autodesk.Http.JsonApi.dll` / `Autodesk.Http.DevPortal.dll` whose file-manifest name collides with Revit's copy, while the runtime `GetName().Name` differs enough that the `HashSet` lets both through. Roslyn then sees two references with the same manifest name and throws.

De-duping on the runtime name can never catch a manifest-name collision — the dedup key must match the identity Roslyn actually uses.

## The fix

Key the dedup on the **file manifest name** (`AssemblyName.GetAssemblyName(location).Name`), which is exactly what Roslyn reads. Fall back to `GetName().Name` only if reading the file throws.

### Change 1 — extract a shared dedup-key helper
In `CodeExecutor.cs`, add a small helper so both call sites agree on the key:

```csharp
// The name Roslyn uses to detect duplicate references is the one baked into the
// file manifest (what CreateFromFile reads), NOT the runtime Assembly.GetName().
// Co-installed add-ins (e.g. IssuesManagement) ship a same-manifest-name copy of
// Autodesk.Http.JsonApi/DevPortal, so we must de-dup on the manifest name.
private static string ManifestSimpleName(Assembly assembly)
{
    try { return AssemblyName.GetAssemblyName(assembly.Location).Name; }
    catch { return assembly.GetName().Name; }
}
```

### Change 2 — use it in the loop (replaces line 538-542)
```csharp
var simpleName = ManifestSimpleName(assembly);
if (seenSimpleNames.Add(simpleName))
{
    references.Add(MetadataReference.CreateFromFile(assembly.Location));
}
else
{
    // Dropped a duplicate manifest name — log so the collision is visible on
    // the affected machine (e.g. IssuesManagement's Autodesk.Http.JsonApi.dll).
    Log($"CodeExecutor: skipped duplicate reference '{simpleName}' at {assembly.Location}");
}
```
Use whatever the file's existing logging mechanism is (match the surrounding code; if there is none, a `System.Diagnostics.Debug.WriteLine` is fine — do not invent a new logger).

### Change 3 — use it in `EnsureRef` (line 552-561)
```csharp
void EnsureRef(Assembly asm, string nameHint)
{
    try
    {
        if (asm == null || string.IsNullOrEmpty(asm.Location)) return;
        if (!seenSimpleNames.Add(ManifestSimpleName(asm))) return;
        references.Add(MetadataReference.CreateFromFile(asm.Location));
    }
    catch { }
}
```

Ensure `using System.Reflection;` is present (it already is — `Assembly`/`MetadataReference` are in use).

## Tests

Add a unit test under `revit-addin-sync/Tests/` (there is already a `BatchRefResolverTests.cs` covering reference resolution — mirror its style). Because `AppDomain.CurrentDomain.GetAssemblies()` and real DLLs are hard to fake, test the **dedup key logic** directly:
- Refactor the dedup so the collection step takes an injectable "list of (simpleName, location)" and returns a de-duped reference set keyed on simpleName — then assert that two entries with the same simpleName but different locations yield exactly one reference (first wins).
- If refactoring for testability is too invasive for this pass, at minimum add a test asserting `ManifestSimpleName` returns the manifest name for a known on-disk DLL (e.g. the RevitAPI assembly the tests already load), documenting the intent.

## Verification

1. Build the add-in: `dotnet build revit-addin-sync/RevitWebAppSync.csproj -c Release` (or the existing build script).
2. Run the add-in tests: `dotnet test revit-addin-sync` (or the project's test runner).
3. **On an affected Revit 2027 machine** (one with the IssuesManagement add-in installed):
   - Replace the installed BINA add-in DLL(s) with the rebuilt ones.
   - **Fully restart Revit** — `_cachedReferences` is a process-lifetime static cache (line 504), so a running Revit will keep the old reference set.
   - Run any Copilot prompt (e.g. the gazebo). Confirm compilation no longer fails on line 1 with "already been imported", and check the log shows the duplicate `Autodesk.Http.JsonApi` / `Autodesk.Http.DevPortal` was skipped.

## Deployment note

Unlike the backend fix, this is a **client-side C# change**. It must be rebuilt and redeployed to every affected Revit machine (the DLL conflict is local to each install's `AddIns` folder), and each Revit instance restarted. No backend/bina-ai change or restart is required for this bug.

## Out of scope

- The backend API-hallucination fix (ClickUp 86ey3udbm) — already done in `bina-ai`.
- Removing/relocating the IssuesManagement add-in — not our component; the dedup fix makes BINA robust regardless of what else is co-installed.
