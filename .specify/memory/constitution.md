# Revit Add-in Sync Constitution

Draft v0.1 (2026-11-28), written from the README and repo layout. Team lead to review and amend.

## Core Principles

### I. Spec before code
Every non-trivial change starts with `/speckit-specify` → `/speckit-plan` → `/speckit-tasks`.
Code is written only against an approved tasks.md. Reviewers check the code against the spec.

### II. Revit API on the Revit thread
All Revit API calls and transactions run in Revit's API context (commands or ExternalEvent handlers).
UI and background work never touch the Revit document directly.

### III. Engine logic stays headless
CAD-to-BIM geometry and classification logic lives in code that runs without Revit (`Cad2Bim.Headless`),
so it can be tested on any machine. Revit-specific creation is a thin layer on top.

### IV. Verified in Revit
A feature is done only after it is tested in a real Revit session (AI-driven Windows VM or a dev PC),
with steps and results recorded in the PR.

### V. Measure before optimising
Performance changes come with before/after timings on a real drawing (e.g. via `Cad2Bim.Headless`).

## Constraints

- C# / .NET 8, Revit 2025 and 2026 (`RevitPath`, `RevitVersion` env vars).
- Secrets (APS, web app keys) never committed; use env vars or local config.
- Prefer O(1) lookups (HashSet / Dictionary) in per-segment / per-element loops.

## Workflow

Spec (Analyst / captain) → Plan (Architect) → Tasks (Groomer) → Implement (Builder) → Review against spec (PR review / Watchdog) → Test in Revit (Tester).

## Governance

This constitution overrides ad-hoc prompts. Amend it by PR; bump the version line above.

**Version**: 0.1 | **Ratified**: 2026-11-28 | **Last Amended**: 2026-11-28
