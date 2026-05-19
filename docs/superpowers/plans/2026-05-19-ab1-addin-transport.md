# AB1 — Addin Transport Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route every `AIService` HTTP call through a single `/agents/revit-ai` prefix (instead of the wrong `/api/revit-ai`) via one constant + helper, so the addin can reach the bina-ai backend.

**Architecture:** Add `RevitAiPath` const + `internal static BuildAiUrl(baseUrl, endpoint)` to `Services/AIService.cs`; convert all 12 call sites + the doc-comment; expose internals to the `Tests` assembly; cover with xUnit + a cross-platform source guard. Base-URL resolution unchanged.

**Tech Stack:** C# / .NET (`net10.0-windows`, Revit addin), xUnit 2.9.2. **Build/test target is Windows-only and `dotnet` is not available in this controller environment** — `dotnet build`/`dotnet test` are operator (Windows) steps; the in-session gate is a `grep`-based source guard (cross-platform).

**Spec:** `docs/superpowers/specs/2026-05-19-ab1-addin-transport-design.md`

---

### Task 0: Confirm branch

**Files:** none (git only)

- [ ] **Step 1: Verify the working branch**

Run:
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git branch --show-current
```
Expected: `feat/sp3b-addin-backend-alignment` (already created; do NOT create a new branch). Confirm `Services/AIService.cs` and `Tests/Tests.csproj` exist.

---

### Task 1: Add `RevitAiPath` + `BuildAiUrl` + InternalsVisibleTo + tests

**Files:**
- Modify: `Services/AIService.cs` (add using, assembly attr, const, helper)
- Create: `Tests/AiServiceUrlTests.cs`

- [ ] **Step 1: Write the test file**

Create `Tests/AiServiceUrlTests.cs`:

```csharp
using System.IO;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class AiServiceUrlTests
    {
        [Fact]
        public void BuildAiUrl_uses_agents_prefix()
        {
            Assert.Equal(
                "http://x/agents/revit-ai/route",
                AIService.BuildAiUrl("http://x", "route"));
        }

        [Fact]
        public void BuildAiUrl_keeps_subpath()
        {
            Assert.Equal(
                "https://h/agents/revit-ai/commands/abc",
                AIService.BuildAiUrl("https://h", "commands/abc"));
        }

        [Fact]
        public void AIService_source_has_no_old_api_prefix()
        {
            var here = Path.GetDirectoryName(
                typeof(AiServiceUrlTests).Assembly.Location);
            // repo layout: <repo>/Tests/bin/.../Tests.dll ; src at <repo>/Services
            var src = Path.GetFullPath(Path.Combine(
                here, "..", "..", "..", "..", "Services", "AIService.cs"));
            var text = File.ReadAllText(src);
            Assert.DoesNotContain("/api/revit-ai", text);
            Assert.Contains("/agents/revit-ai", text);
        }
    }
}
```

(The third test is a belt-and-suspenders guard; the authoritative in-session
guard is the `grep` in Task 3 Step 2, which does not depend on the .NET test
runner or the bin path.)

- [ ] **Step 2: Add the using + assembly attribute**

In `Services/AIService.cs`, the current `using` block is lines 1–10 ending with
`using System.Threading.Tasks;`. Immediately after line 10 add:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Tests")]
```

(If a `grep -rn 'InternalsVisibleTo("Tests")' --include=*.cs .` shows it already
exists elsewhere in the addin assembly, SKIP adding the attribute here — keep
only the `using` if needed; do not duplicate the attribute.)

- [ ] **Step 3: Add the constant + helper**

In `Services/AIService.cs`, directly below `private readonly string _baseUrl;`
(line 30), add:

```csharp

        internal const string RevitAiPath = "/agents/revit-ai";

        internal static string BuildAiUrl(string baseUrl, string endpoint) =>
            $"{baseUrl}{RevitAiPath}/{endpoint}";
```

- [ ] **Step 4: In-session structural check**

Run:
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -n 'RevitAiPath = "/agents/revit-ai"\|internal static string BuildAiUrl\|InternalsVisibleTo("Tests")' Services/AIService.cs
```
Expected: the const, the helper, and the attribute all present.

- [ ] **Step 5: Commit**

```bash
git add Services/AIService.cs Tests/AiServiceUrlTests.cs
git commit -m "feat(ab1): add RevitAiPath + BuildAiUrl helper + url tests"
```

---

### Task 2: Convert all 12 call sites + the doc-comment

**Files:**
- Modify: `Services/AIService.cs`

Make these exact replacements (the right-hand side preserves any existing
`+ query`/`+ "?..."` concatenation that already follows on the next line —
only the interpolated literal changes).

- [ ] **Step 1: generate (line 53)**

Replace `$"{_baseUrl}/api/revit-ai/generate"` with `BuildAiUrl(_baseUrl, "generate")`

- [ ] **Step 2: doc-comment (line ~129)**

Replace the comment text `POST /api/revit-ai/route.` with `POST /agents/revit-ai/route.`

- [ ] **Step 3: route (line 139)**

Replace `$"{_baseUrl}/api/revit-ai/route"` with `BuildAiUrl(_baseUrl, "route")`

- [ ] **Step 4: retry (line 196)**

Replace `$"{_baseUrl}/api/revit-ai/retry"` with `BuildAiUrl(_baseUrl, "retry")`

- [ ] **Step 5: explain-error (line 252)**

Replace `$"{_baseUrl}/api/revit-ai/explain-error"` with `BuildAiUrl(_baseUrl, "explain-error")`

- [ ] **Step 6: record-fix (line 290)**

Replace `$"{_baseUrl}/api/revit-ai/record-fix"` with `BuildAiUrl(_baseUrl, "record-fix")`

- [ ] **Step 7: health (line 311)**

Replace `$"{_baseUrl}/api/revit-ai/health"` with `BuildAiUrl(_baseUrl, "health")`
(stays inside `_httpClient.GetAsync(..., cancellationToken)`)

- [ ] **Step 8: commands GET (line ~335)**

Replace `$"{_baseUrl}/api/revit-ai/commands"` with `BuildAiUrl(_baseUrl, "commands")`
(the following line `+ (query.Count > 0 ? "?" + string.Join("&", query) : "")` stays unchanged)

- [ ] **Step 9: commands POST (line ~367)**

Replace `$"{_baseUrl}/api/revit-ai/commands"` with `BuildAiUrl(_baseUrl, "commands")`

- [ ] **Step 10: commands PUT (line ~405)**

Replace `$"{_baseUrl}/api/revit-ai/commands/{templateId}"` with `BuildAiUrl(_baseUrl, $"commands/{templateId}")`

- [ ] **Step 11: commands PUT (line ~429)**

Replace `$"{_baseUrl}/api/revit-ai/commands/{templateId}"` with `BuildAiUrl(_baseUrl, $"commands/{templateId}")`

- [ ] **Step 12: commands DELETE (line ~453)**

Replace `$"{_baseUrl}/api/revit-ai/commands/{templateId}"` with `BuildAiUrl(_baseUrl, $"commands/{templateId}")`
(the following `+ (userId.HasValue ? $"?userId={userId.Value}" : "")` stays unchanged)

- [ ] **Step 13: commands/export GET (line ~480)**

Replace `$"{_baseUrl}/api/revit-ai/commands/export"` with `BuildAiUrl(_baseUrl, "commands/export")`
(the following `+ (query.Count > 0 ? ... )` stays unchanged)

- [ ] **Step 14: commands/import POST (line ~515)**

Replace `$"{_baseUrl}/api/revit-ai/commands/import"` with `BuildAiUrl(_baseUrl, "commands/import")`

- [ ] **Step 15: Commit**

```bash
git add Services/AIService.cs
git commit -m "feat(ab1): route all AIService calls through /agents/revit-ai"
```

---

### Task 3: Verify completeness + operator build/test runbook

**Files:** none (verification)

- [ ] **Step 1: Authoritative source guard (in-session, cross-platform)**

Run:
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -n '/api/revit-ai' Services/AIService.cs && echo "FAIL: stale prefix remains" || echo "OK: no /api/revit-ai literal"
grep -c '/agents/revit-ai' Services/AIService.cs
```
Expected: first line prints `OK: no /api/revit-ai literal` (grep finds nothing → non-zero exit → `||` branch). Second prints a count ≥ 1 (the `RevitAiPath` const). If any `/api/revit-ai` remains, return to Task 2 and convert the missed site, then re-run.

- [ ] **Step 2: Confirm every call site uses the helper**

Run:
```bash
grep -c 'BuildAiUrl(_baseUrl' Services/AIService.cs
```
Expected: `12` (the 12 converted call sites). If fewer, a site was missed in Task 2.

- [ ] **Step 3: Commit any guard-driven fix (only if Task 2 was revisited)**

```bash
git add Services/AIService.cs
git commit -m "fix(ab1): convert missed AIService call site to BuildAiUrl"
```
(Skip if Steps 1–2 passed first time.)

- [ ] **Step 4: Operator build + test (Windows / .NET — performed by a human)**

This addin targets `net10.0-windows`; it cannot build on the controller host.
On a Windows machine with the .NET SDK:
```bash
dotnet build revit-addin-sync.sln -c Release
dotnet test Tests/Tests.csproj
```
Expected: build succeeds; `AiServiceUrlTests` (3 tests) pass —
`BuildAiUrl_uses_agents_prefix`, `BuildAiUrl_keeps_subpath`,
`AIService_source_has_no_old_api_prefix`. If the source-guard test's relative
path to `AIService.cs` does not resolve in the CI/bin layout, rely on the
Task 3 Step 1 `grep` guard (authoritative) and adjust the test's path
constant to the actual `Tests` output layout.

---

## Self-Review

**1. Spec coverage:**
- Single `RevitAiPath` const + `BuildAiUrl` helper → Task 1 Steps 3. ✓
- All 12 call sites + doc-comment converted → Task 2 Steps 1–14. ✓
- `InternalsVisibleTo("Tests")` so builder is unit-testable → Task 1 Step 2. ✓
- Base-URL resolution unchanged (no `DEFAULT_AI_BASE_URL` edit) → no task touches it; only the prefix literal changes. ✓
- Unit tests (prefix + subpath) + source guard → Task 1 Step 1 + Task 3 Steps 1–2. ✓
- Build gate, marked operator because Windows-only/no dotnet here → Task 3 Step 4. ✓
- Only `AIService.cs` + one test file + the assembly attribute; no base-URL/dispatch/aux/other-repo change → Tasks scoped to those files; AB2/AB3 explicitly untouched. ✓

**2. Placeholder scan:** No TBD/TODO. Every code step shows exact old→new text; every command shows expected output. Task 3 Step 4 is an explicit operator runbook (Windows-only build is a real constraint, not a placeholder), with the cross-platform `grep` guard as the authoritative in-session gate.

**3. Type consistency:** `internal const string RevitAiPath`, `internal static string BuildAiUrl(string baseUrl, string endpoint)` defined in Task 1, used identically in every Task 2 replacement (`BuildAiUrl(_baseUrl, "<endpoint>")` / `BuildAiUrl(_baseUrl, $"commands/{templateId}")`) and asserted in Task 1's tests + Task 3's grep (`BuildAiUrl(_baseUrl`, count 12). `InternalsVisibleTo("Tests")` matches the test assembly name (`Tests.csproj` → assembly `Tests`). Consistent.

No gaps found.
