# BINA Copilot Engine — Phase 4 (packaging & lifecycle) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: subagent-driven-development or executing-plans. Checkbox steps. Most of this phase is Windows/installer work — code is staged on macOS, built and verified on Windows.

**Goal:** Ship the engine to drafters as one product: `bina-engine.exe` bundled with the add-in installer, auto-spawned by the add-in, health-managed, updated over the existing OTA feed. To the drafter it's a single install.

**Architecture:** PyInstaller bundles the bina-ai engine (`app/engine/main.py`) into `bina-engine.exe`. The add-in gains an `EngineManager` that spawns it (with a per-boot secret), health-checks it, and kills orphans. The OTA `version.json` gains a `sidecar`/`engine` block; `UpdateService` stages engine versions the same hot-safe way it stages the add-in. The Inno-Setup installer carries the engine payload.

**Tech Stack:** PyInstaller (bina-ai build); C# .NET (revit-addin-sync); Inno-Setup; GitHub Actions. Depends on Phases 1-3. Spec: Phase 4 section of the colocate design.

## Global Constraints

- Branch `feat/copilot-engine`. Stage-only. revit-addin-sync does not compile on macOS — C# ends at staged + self-reviewed; build + run on Windows.
- The per-boot secret replaces the Phase-1 interim shared-config secret: the add-in generates a random secret at startup, passes it to the engine on spawn (`--secret`), and uses it for both the `X-Bina-Secret` tool calls and the pane→engine turn API.
- Engine versions live under `%LocalAppData%\Bina\RevitSync\engine\<ver>\` — newest-semver-wins, never overwrite a running version dir (mirror `BinaLoader`).
- Pin engine↔add-in compatibility in `version.json` (update both or neither).

---

### Task 1: PyInstaller build for `bina-engine.exe` (bina-ai)

**Files:** Create `packaging/engine.spec`, `packaging/build-engine.ps1` (bina-ai).

- [ ] Author a PyInstaller spec that bundles `app/engine/main.py` + its imports (agno, fastapi, uvicorn, httpx) into a one-folder `bina-engine` with an embedded CPython, entry runs `uvicorn app.engine.main:app --host 127.0.0.1 --port <arg>`. Exclude the cloud-only deps the lean engine root never imports (verify via the Phase-1 lean-root import graph).
- [ ] `build-engine.ps1`: runs PyInstaller, emits `bina-engine/` + a `version.json` `engine` block `{version, url, sha256}`.
- [ ] Verify on Windows: `bina-engine.exe --port 48810` boots, `/health` → `{"engine":true}`. Stage the spec + script (the built exe is a CI artifact, not committed).

### Task 2: `EngineManager.cs` — spawn, health, secret, lifecycle (addin)

**Files:** Create `Services/EngineManager.cs`; modify `App.cs` (start it in engine mode).

**Interfaces:** `EngineManager.EnsureRunning(secret, port)` → spawns the newest engine version if `/health` is not already answering; `Stop()` on shutdown; heartbeat so the engine idle-exits if the add-in goes away.

- [ ] Implement: locate newest `%LocalAppData%\Bina\RevitSync\engine\<ver>\bina-engine.exe`; if `GET 127.0.0.1:{port}/health` fails, `Process.Start` it with `--port {port} --secret {secret}`; pidfile for orphan-kill; dispose on `OnShutdown`. Generate the per-boot secret here and hand it to both `EngineManager` and `McpServer` (replaces the Phase-1 config secret).
- [ ] Wire into `App.cs` engine-mode branch: `EngineManager.EnsureRunning(secret, cfg.EnginePort/enginePort)` before/after `McpServer` start; feed the same secret into `new McpServer(port, secret)`.
- [ ] Self-review: no zombie processes (pidfile + idle-exit), `127.0.0.1` only, secret never logged. Stage. (Build + run on Windows.)

### Task 3: OTA — engine block in `version.json` + `UpdateService` (addin)

**Files:** Modify `Services/UpdateService.cs`.

- [ ] Extend the `version.json` model with an optional `engine {version, url, sha256}`; in `CheckAsync`, when present and newer, stage the engine zip into `engine\<ver>\` via the existing `StageCoreAsync` hot-safe pattern (sha256-verified, atomic, never overwrite a running dir). Pin: refuse an engine update whose `minAddinVersion` exceeds the running add-in (and vice-versa).
- [ ] Self-review + stage.

### Task 4: Installer + CI (addin + bina-ai)

**Files:** Modify `installer/RevitCopilot.iss` (+ `.wxs` if used), `.github/workflows/release.yml`.

- [ ] Installer: add the `bina-engine` payload into the engine versions dir; code-sign the exe (the `feat/ci-code-signing` lineage exists). Firewall note: loopback-only, no inbound rule needed.
- [ ] CI: `release.yml` builds BOTH artifacts (add-in zip + `bina-engine`), emits one `version.json` with both blocks. Stage.

## Self-review
- Coverage: build (T1), spawn/lifecycle/secret (T2), OTA (T3), installer/CI (T4). Per-boot secret replaces the interim one. Newest-semver hot-safe staging reused for the engine.
- Nearly all Windows/build-verified; macOS staging only. The gate is a 2-3 office pilot fleet running stably (per the colocate spec).
