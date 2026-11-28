# Revit Add-in Sync Constitution

v0.2 (2026-11-28). Written from the csproj, CI workflows, HANDOFF.md and the Cad2Bim layout. Amend by PR.

## Core Principles

### I. Spec before code
Every non-trivial change starts with `/speckit-specify` → `/speckit-plan` → `/speckit-tasks`.
Code is written only against an approved tasks.md. Reviewers check the code against the spec.
Small fixes and one-line bugs skip this.

### II. One codebase, every Revit version
The add-in multi-targets `net48` (Revit 2023–2024), `net8.0-windows` (2025–2026) and `net10.0-windows` (2027).
Every change compiles on all three. Version-specific code goes behind the existing defines
(e.g. `REVIT2023_24`), never a fork of the file. A plan names which Revit versions it was tested on.

### III. Never block Revit
Revit API calls and transactions run only in Revit's API context (commands or `ExternalEvent` handlers).
No `.GetAwaiter().GetResult()`, `.Result` or unbounded waits on the Revit UI thread; network and file work is
async with a timeout and cancellation (see HANDOFF.md: login froze Revit with no timeout).

### IV. Engine logic stays headless
CAD-to-BIM reading, geometry and classification live in code that runs without Revit
(`Cad2Bim/`, `Cad2Bim.Headless`) so it can be tested and timed on any machine.
Revit element creation is a thin layer on top. DWG reading goes through one reader interface,
so the backend (ACadSharp, ODA Drawings.NET) can change without touching the engine.

### V. Tested, then verified in Revit
- Logic changes ship with tests in `Tests/` that pass with
  `dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true` (the CI `tests` job).
- Anything a user sees or clicks in Revit is done only after a real Revit session (Tester on the
  Windows VM or a dev PC), with steps and results in the PR.
- CAD-to-BIM changes report counts on the real reference drawings (e.g. ASWARA plans) before and after.

### VI. Measure before optimising
Performance changes come with before/after timings on a real drawing via `Cad2Bim.Headless`.
Per-segment / per-element loops use O(1) lookups (HashSet / Dictionary), never `List.Contains`.

## Constraints

- Build configs: Debug, Staging (`STAGING` define), Release. CI: `build`, `tests`, `installer-scripts` must pass.
- Secrets (APS, BINA tokens, API keys) are never committed and never written in plaintext when a secure store exists.
- The add-in talks to bina-ai; a change to a bina-ai tool contract is planned as a paired change in both repos.

## Workflow

Spec + plan (Architect) → Tasks (Groomer) → Implement (Builder) → Review against spec (PR review / Watchdog) → Test in Revit (Tester).

## Governance

This constitution overrides ad-hoc prompts. Amend it by PR and bump the version line.

**Version**: 0.2 | **Ratified**: 2026-11-28 | **Last Amended**: 2026-11-28
