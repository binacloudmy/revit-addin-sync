# AB1 — Addin Transport Alignment (/api → /agents)

**Date:** 2026-05-19
**Status:** Approved design (pre-implementation)
**Repo:** `revit-addin-sync` (branch `feat/sp3b-addin-backend-alignment`,
off `feat/copilot-saved-commands`).
**Scope:** First decomposed slice of the addin↔backend alignment rework.
AB1 is transport only. AB2 (router dispatch rework: switch on `Tool`, vetted-tool
handlers, param alignment, `unvetted_code` fallback) and AB3 (graceful
degradation for backend-unserved aux endpoints) are separate sub-projects with
their own spec → plan → build cycles.

## Problem

`Services/AIService.cs` posts every Copilot/AI request to
`{_baseUrl}/api/revit-ai/*` (13 call sites). The bina-ai backend serves these
under `/agents/revit-ai/*` (SP1 `generate`, SP3a `route`, plus `retry`). The
deployment has **no proxy** rewriting `/api/*` → `/agents/*` (confirmed: the
addin calls bina-ai directly). So today the addin cannot reach any of the new
backend endpoints — the path prefix is wrong. Nothing in AB2/AB3 is
end-to-end testable until the addin actually reaches the backend.

## Goal & Success Bar

Addin-only. Every `AIService` request targets `/agents/revit-ai/*` via a single
shared prefix constant + URL builder. Base-URL resolution is unchanged
(config-driven via `BinaConfig.ResolvedAIBaseUrl`; the operator sets the host at
deploy). Bodies, headers, HTTP methods, auth, and per-call error handling are
byte-unchanged. Done = `dotnet build` green, unit tests for the URL builder
pass, and a guard test proves no `/api/revit-ai` literal remains in
`AIService.cs`.

## Decisions (locked during brainstorming)

- **Transport topology:** addin calls bina-ai directly; no path-rewriting
  proxy. Therefore the addin must use `/agents/revit-ai/*` (what bina-ai
  serves).
- **No base-URL hardcode:** `DEFAULT_AI_BASE_URL` is left as-is; the operator
  sets `AIBaseUrl` (→ `ResolvedAIBaseUrl`) at deploy time. AB1 does not bake a
  URL.
- **Single prefix constant + builder (approach A):** one
  `const RevitAiPath = "/agents/revit-ai"` and an
  `internal static BuildAiUrl(baseUrl, endpoint)`; all 13 call sites route
  through it. Rejected: in-place literal replace (perpetuates the scattered-
  literal smell), and a configurable prefix knob (YAGNI — topology is settled).
- **Uniform application:** the builder is used for *all* `/api/revit-ai/*`
  sites including ones bina-ai does not serve yet (explain-error, record-fix,
  health, commands*). Those simply move to the `/agents/...` prefix and still
  404 — their non-existence is AB3's concern, independent of the prefix.

## Non-Goals

- No base-URL / `DEFAULT_AI_BASE_URL` change.
- No dispatch/handler/param changes (AB2).
- No fix for backend-unserved endpoints (AB3) — they keep failing, just at the
  new prefix.
- No change to request bodies, headers, methods, auth, retry/error handling.
- No change to any file other than `AIService.cs`, one test file, and the
  assembly `InternalsVisibleTo` attribute.
- No other repo (`bina-ai` untouched).

## The 13 Call Sites (in `Services/AIService.cs`)

`generate` (POST), `route` (POST), `retry` (POST), `explain-error` (POST),
`record-fix` (POST), `health` (GET), `commands` (GET), `commands` (POST),
`commands/{templateId}` (PUT ×2), `commands/{templateId}` (GET),
`commands/export` (GET), `commands/import` (POST). Plus a doc-comment
(`POST /api/revit-ai/route`) updated to `/agents/...` for accuracy.

## Architecture

Single file `Services/AIService.cs`:

- Add `private const string RevitAiPath = "/agents/revit-ai";`
- Add `internal static string BuildAiUrl(string baseUrl, string endpoint) =>
  $"{baseUrl}{RevitAiPath}/{endpoint}";`
- Each call site changes from `$"{_baseUrl}/api/revit-ai/<rest>"` to
  `BuildAiUrl(_baseUrl, "<rest>")`, where `<rest>` is the part after
  `/api/revit-ai/` (e.g. `"route"`, `"commands/export"`,
  `$"commands/{templateId}"`). Any existing query string (e.g.
  `?include=...`) is appended to the `BuildAiUrl(...)` result exactly as it is
  appended to the literal today (the builder returns only the path; callers
  concatenate query strings unchanged).
- Add, at the top of `AIService.cs`,
  `using System.Runtime.CompilerServices;` and the assembly attribute
  `[assembly: InternalsVisibleTo("Tests")]`. C# assembly attributes are
  file-agnostic, so this needs no new `Properties/AssemblyInfo.cs` and keeps
  AB1 to a single source file. (If `InternalsVisibleTo("Tests")` is already
  present elsewhere in the assembly, the plan reuses it instead of duplicating.)

## Data Flow

`AIService.<method>` → `BuildAiUrl(_baseUrl, endpoint)` →
`{ResolvedAIBaseUrl}/agents/revit-ai/{endpoint}` → bina-ai. `route`/`generate`/
`retry` now reach real handlers; the rest 404 at the new prefix (AB3). No other
flow change.

## Error Handling

No behavior change. Every `AIService` method keeps its current try/catch and
returns its existing failure shape. AB1 changes only the URL string.

## Testing

Project `Tests/Tests.csproj`. Add `AiServiceUrlTests.cs`:

- `BuildAiUrl("http://x", "route")` == `"http://x/agents/revit-ai/route"`.
- `BuildAiUrl("https://h", "commands/abc")` ==
  `"https://h/agents/revit-ai/commands/abc"`.
- Guard: read the `AIService.cs` source file; assert it contains no
  `"/api/revit-ai"` substring and does contain `"/agents/revit-ai"` (fails if a
  call site or the doc-comment was missed).

Build gate: `dotnet build revit-addin-sync.sln -c Release` succeeds (the plan
will confirm the exact solution/test invocation; this is a Windows/.NET addin —
if the controller's environment cannot run `dotnet`, the build/test steps are
operator-run and the plan marks them as such, with the source-guard test still
runnable wherever `dotnet test` works).

## Out of Scope (deferred)

| Item | Disposition |
|---|---|
| Switch dispatch from `action.Type` to `action.Tool`; 5 vetted-tool handlers; `unvetted_code`→CodeExecutor; param-key alignment | AB2 |
| Graceful degradation when bina-ai lacks explain-error/record-fix/health/commands | AB3 |
| Hardcoding a real base URL | Excluded (operator sets at deploy) |
| Any `bina-ai` change | Out of repo |
