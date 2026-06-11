# AB3 — Aux-Endpoint Graceful Degradation (Health Reachability Fix)

**Date:** 2026-05-19
**Status:** Approved design (pre-implementation)
**Repo:** `revit-addin-sync` (branch `feat/sp3b-addin-backend-alignment`,
which already contains AB1 + AB2).
**Scope:** Final slice of the addin↔backend alignment. AB1 (transport) and
AB2 (dispatch) are done. AB3 covers graceful behaviour when bina-ai does not
serve the addin's auxiliary endpoints.

## Problem

bina-ai serves only `retry`, SP1 `generate`, and SP3a `route`. The addin's
`AIService` also calls `explain-error`, `record-fix`, `health`, and `commands*`
— now at `/agents/revit-ai/*` (post-AB1). Grounding the actual code shows three
of these already degrade acceptably; only one is a real defect:

- `ExplainErrorAsync` → `try/catch` returns `null`; caller
  (`AIAssistantWindow.xaml.cs` ~1683) already falls back to `AddError(error)`
  (raw error shown). **Graceful.**
- `RecordFixAsync` → fire-and-forget, exception swallowed. **Graceful.**
- `GetCommandsAsync` → "empty list on any failure"; the saved-commands panel
  just shows empty. **Degraded, not broken.**
- `HealthCheckAsync` → `return response.IsSuccessStatusCode;` → **`false` on
  the unserved `/health` 404**. Its single caller `CheckBackendConnection`
  then sets the status banner to red **"Backend not available"** even though
  `/route` and `/generate` work. It does not disable input (cosmetic), but it
  is a false-negative that makes users distrust a working backend. **This is
  the only real defect; it is AB3's sole code change.**

## Goal & Success Bar

Addin-only, single method. The backend-availability indicator must reflect
**reachability** (can the host answer at all → `/route`/`/generate` will work),
not whether a `/health` route exists. A reachable host (any HTTP response,
including 404) → "Connected". Only a genuine transport failure (host
down/unreachable/timeout) → "Backend not available". The other three aux
endpoints keep their existing acceptable degradation — no code, accepted and
documented here. Done = source-guard grep confirms the one-method change;
operator Windows/Revit smoke confirms the banner is correct.

## Decision (locked during brainstorming)

- **Approach A — reachability semantics in `HealthCheckAsync`.** Change the
  return so any received HTTP response means reachable; only a transport
  exception means not. Rejected: a new `IsReachableAsync` (leaves
  `HealthCheckAsync` dead, churn) and fixing in `CheckBackendConnection` (puts
  logic in the Revit-coupled, untestable window).
- **No test/DI infra.** `HealthCheckAsync` is `HttpClient` I/O; unit-testing it
  would need an injected handler the `Tests` project deliberately avoids
  (Revit-free pure files only). A one-line semantic change is verified by
  source-guard grep + operator smoke — consistent with AB1/AB2's no-dotnet
  pattern. Adding DI here is YAGNI.
- **Other three endpoints unchanged.** `explain-error`/`record-fix`/`commands`
  already degrade acceptably; touching them is scope creep (YAGNI).

## Non-Goals

- No change to `ExplainErrorAsync`, `RecordFixAsync`, `GetCommandsAsync` or any
  of the `commands*` methods, or their callers.
- No new endpoint, no alternate probe target (still GET `/health`; any answer
  = reachable).
- No backend / `bina-ai` change. No other repo.
- No DI / test infrastructure, no new file.
- No change to AB1 (`AiUrl`/transport) or AB2 (`VettedToolCode`/dispatch).
- Banner text strings ("Connected" / "Backend not available") unchanged — only
  *when* each shows changes.

## Architecture / Change

Single file `Services/AIService.cs`, single method `HealthCheckAsync`. Current:

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

Change:
- `return response.IsSuccessStatusCode;` → `return true;` (a received response —
  any status, including 404 for the unserved route — proves the host is
  reachable, so `/route`/`/generate` will work).
- Replace the doc comment with one stating it reports backend **reachability**
  (any HTTP response), not endpoint success; only a transport failure (host
  down / DNS / connection refused / timeout) returns `false`.
- `catch { return false; }` unchanged (no `HttpResponseMessage` ⇒ truly
  unreachable ⇒ "Backend not available").

Single caller `CheckBackendConnection` (`AIAssistantWindow.xaml.cs` ~141)
needs no change: it already maps `true`→"Connected" / `false`→"Backend not
available"; the mapping is now correct.

## Data Flow

`CheckBackendConnection` → `HealthCheckAsync` → `GET /health`:
- host answers (200 / 404 / 5xx) → `true` → "Connected" (green).
- no response (down / unreachable / timeout → exception) → `catch` → `false`
  → "Backend not available" (red).

## Error Handling

Transport exception (`HttpRequestException`, `TaskCanceledException`, etc.) →
existing `catch` → `false` → correct "Backend not available". Any HTTP status →
`true`. No exception escapes the method.

## Testing

In-session (cross-platform; `dotnet` unavailable here):
`grep` confirms within `HealthCheckAsync` the line
`return response.IsSuccessStatusCode;` is gone and `return true;` is present,
the `catch { return false; }` remains, and no other method/file changed.

Operator (Windows / Revit): point the addin at a running bina-ai that does
**not** serve `/health` → the status banner reads "Connected" (was "Backend
not available"); point it at an unreachable host → "Backend not available".

## Out of Scope (accepted as-is)

| Item | Disposition |
|---|---|
| `explain-error` 404 | Already → null → caller shows raw error. Accepted. |
| `record-fix` 404 | Already best-effort swallowed. Accepted. |
| `commands*` 404 | Already → empty list → empty saved-commands panel. Accepted. |
| Backend serving these endpoints | Out of repo (bina-ai); not AB3. |
| Pre-existing CS1587 `<summary>` nit on `UpdateCommandCodeAsync` | Not in AB3's changed region; untouched. |
