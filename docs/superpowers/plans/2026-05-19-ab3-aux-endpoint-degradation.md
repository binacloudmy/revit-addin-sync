# AB3 — Aux-Endpoint Graceful Degradation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AIService.HealthCheckAsync` report backend *reachability* (any HTTP response → reachable) instead of `/health`-endpoint success, so a working bina-ai (which doesn't serve `/health`) no longer shows a false "Backend not available" banner.

**Architecture:** One method, one file (`Services/AIService.cs::HealthCheckAsync`): replace `return response.IsSuccessStatusCode;` with `return true;` (drop the now-unused local), update the doc comment; the `catch { return false; }` (true transport failure) stays. The three other aux endpoints already degrade acceptably and are not touched.

**Tech Stack:** C# / .NET (`net10.0-windows`, Revit addin). **Windows-only; `dotnet` unavailable here → build/test are operator steps; in-session gate = `grep` source guard.** Builds on `feat/sp3b-addin-backend-alignment` (AB1+AB2 already there).

**Spec:** `docs/superpowers/specs/2026-05-19-ab3-aux-endpoint-degradation-design.md`

---

### Task 0: Confirm branch

- [ ] **Step 1**

Run:
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git branch --show-current
```
Expected: `feat/sp3b-addin-backend-alignment` (AB1+AB2+AB3 spec already here; no new branch). Confirm `Services/AIService.cs` exists.

---

### Task 1: Reachability semantics in `HealthCheckAsync`

**Files:** Modify `Services/AIService.cs` (the `HealthCheckAsync` method only).

- [ ] **Step 1: Confirm the target is unambiguous**

Run:
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
grep -n 'public async Task<bool> HealthCheckAsync' Services/AIService.cs
```
Expected: exactly one line (~307). (Note: `return response.IsSuccessStatusCode;` also appears in another method ~460 — the edit below replaces the **whole HealthCheckAsync block** so only that method changes.)

- [ ] **Step 2: Apply the change**

In `Services/AIService.cs`, replace EXACTLY this block:

```csharp
        /// <summary>
        /// Check if backend is available.
        /// </summary>
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync(AiUrl.Build(_baseUrl, "health"), cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
```

with EXACTLY:

```csharp
        /// <summary>
        /// Reports backend *reachability*, not endpoint success. Any HTTP
        /// response (including a 404 for a route the backend doesn't serve,
        /// e.g. /health) proves the host is reachable, so /route and
        /// /generate will work. Only a transport failure (host down, DNS,
        /// connection refused, timeout) returns false.
        /// </summary>
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // We only care that the host answered at all — the status
                // code is irrelevant (the addin doesn't require /health to
                // exist). A thrown exception (no response) = unreachable.
                await _httpClient.GetAsync(AiUrl.Build(_baseUrl, "health"), cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }
```

(The unused `response` local is dropped — `await GetAsync(...)` still performs the request and throws on transport failure, which the `catch` turns into `false`. No other method, including the `~460` one, is touched.)

- [ ] **Step 3: In-session source guard (no dotnet)**

Run:
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
# Within HealthCheckAsync: success-status check gone, return true present.
sed -n '/public async Task<bool> HealthCheckAsync/,/^        }/p' Services/AIService.cs | grep -c 'return response.IsSuccessStatusCode;'   # 0
sed -n '/public async Task<bool> HealthCheckAsync/,/^        }/p' Services/AIService.cs | grep -c 'return true;'                          # 1
sed -n '/public async Task<bool> HealthCheckAsync/,/^        }/p' Services/AIService.cs | grep -c 'return false;'                         # 1 (catch unchanged)
sed -n '/public async Task<bool> HealthCheckAsync/,/^        }/p' Services/AIService.cs | grep -c 'GetAsync(AiUrl.Build(_baseUrl, "health")'  # 1 (still probes /health)
grep -c 'return response.IsSuccessStatusCode;' Services/AIService.cs                                                                      # 1 (the OTHER method ~460 untouched)
git diff --stat Services/AIService.cs                                                                                                    # only this file, small
```
Expected, in order: `0`, `1`, `1`, `1`, `1`, one-file small diff. If the 5th value is not `1` (i.e. the unrelated `~460` occurrence changed), STOP and report BLOCKED — the edit over-reached.

- [ ] **Step 4: Commit**

```bash
git add Services/AIService.cs
git commit -m "fix(ab3): HealthCheckAsync reports reachability, not /health 200"
```

---

### Task 2: Operator verification (Windows / Revit)

- [ ] **Step 1 (operator, Windows):**
```bash
dotnet build revit-addin-sync.sln -c Release
dotnet test Tests/Tests.csproj
```
Expected: build succeeds; existing test suite (AB1 `AiServiceUrlTests`, AB2 `VettedToolCodeTests`) still green — AB3 touches no tested pure code, only the I/O method.

- [ ] **Step 2 (operator, Revit smoke):** With the addin pointed at a running bina-ai that does **not** serve `/health` (i.e. the real SP1/SP3a backend): open the AI Assistant — the status banner reads **"Connected"** (green), not "Backend not available". Point the addin at an unreachable host/port → banner reads **"Backend not available"** (red). Confirms reachability semantics.

---

## Self-Review

**1. Spec coverage:**
- Reachability semantics: any HTTP response → reachable; only transport failure → not → Task 1 Step 2 (`return true;` + unchanged `catch { return false; }`). ✓
- Single method / single file, doc comment updated → Task 1 Step 2 (full-block replace; Step 3 guard confirms the unrelated `~460` occurrence is untouched). ✓
- Other three endpoints unchanged (explain-error/record-fix/commands) → no task touches them; spec documents them accepted-as-is. ✓
- No new endpoint/probe target (still GET `/health`) → Task 1 Step 2 keeps the same URL; Step 3 guard asserts it. ✓
- No backend/other-repo/DI/test-infra change → no task introduces any. ✓
- Caller `CheckBackendConnection` needs no change (mapping now correct) → not modified by any task. ✓
- Verification = source guard + operator smoke (no dotnet here) → Task 1 Step 3 + Task 2. ✓

**2. Placeholder scan:** No TBD/TODO. The single code step shows the exact full before/after block; every command has an expected value. Task 2 is an explicit operator runbook (Windows-only is a real constraint, mirrors AB1/AB2), with the in-session `grep` guard as the authoritative environment evidence.

**3. Type consistency:** Only `HealthCheckAsync` changes; signature `public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)` unchanged; return type still `Task<bool>`; the single caller `CheckBackendConnection` consumes a `bool` exactly as before. No cross-task symbol references. Consistent.

No gaps found.
