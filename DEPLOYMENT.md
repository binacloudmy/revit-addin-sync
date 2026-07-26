# Deployment Guide — revit-addin-sync

How the BINA Revit add-in (and its colocated engine) is built per
environment, released to the fleet, and moved between backends. The cloud
backend's own deploy guide lives in `bina-ai/DEPLOYMENT.md` — this file
covers everything that runs on a drafter's machine.

---

## Architecture at a glance

```
 Drafter machine (Windows)                        Azure
 ┌──────────────────────────────────┐    ┌───────────────────────────────┐
 │ Revit + BinaLoader               │    │ App Service: bina-ai-prod     │
 │  └─ add-in (versions\<ver>\)     │    │  /gateway/* (engine's cloud)  │
 │      ├─ Copilot pane ── AIBaseUrl ───► │  /auth, /credits, JKR, cost   │
 │      │   (cloud mode: backend;   │    │  /telemetry/events            │
 │      │    engine mode: localhost)│    └───────────────────────────────┘
 │      └─ EngineManager ─ spawns ─┐│
 │ engine\<ver>\ (embedded Python) ││    ┌───────────────────────────────┐
 │  └─ bina-ai app/engine ◄────────┘│    │ GitHub Releases (this repo)   │
 │      cloud contact = GatewayUrl ─┼──► │  version.json  ◄─ UpdateService│
 └──────────────────────────────────┘    │  OTA zip + installer + engine │
                                         └───────────────────────────────┘
```

Two deployables ship from this repo:

1. **The add-in** — WPF/Revit plugin, delivered by installer (fresh
   machines) and OTA self-update (fleet).
2. **The engine bundle** (colocate) — embeddable-Python build of the
   bina-ai backend that runs on `localhost`; delivered inside the installer
   and/or over the OTA engine channel. Built separately by
   `bina-ai/scripts/build-engine-bundle.ps1`.

---

## Release channels (build = environment)

The backend an installed add-in talks to is **compiled in**, not
configured. Each MSBuild configuration embeds one `.env` file as a
resource; `BinaConfig.LoadEnv` selects it with `#if` at compile time:

| Configuration | Embeds | Backend | Used for |
|---|---|---|---|
| `Debug` | `.env.local` | dev ngrok/localhost | day-to-day development |
| `Staging` | `.env.staging` | `bina-ai-staging.azurewebsites.net` | UAT builds |
| `Release` | `.env.production` | `bina-ai-prod.azurewebsites.net` | the fleet |

```powershell
dotnet build -c Staging                       # UAT build against staging
installer\build-installer.ps1 -Version 0.0.20-uat -Configuration Staging
                                              # installable UAT build
# CI (release.yml) is hardcoded -c Release — fleet always gets prod.
```

Keys per env file: `BASE_URL` (AI + API + auth + cloud, one host),
`UPDATE_FEED_URL` (OTA feed; **empty in `.env.staging`** so a UAT build
never self-updates onto the fleet channel), `LOGIN_WEB_URL`, `LOGIN_PATH`.

### Env-first URL resolution (how backend cutovers work)

`Services/UrlResolution.cs` (pure, unit-tested in
`Tests/BinaConfigResolutionTests.cs`) applies one rule to every URL the
add-in resolves:

> A `config.json` override pointing at one of OUR `*.azurewebsites.net`
> hosts is an environment pin from an old install, not a customization —
> the embedded `.env` wins. Genuinely custom values (self-hosted gateway,
> localhost engine, opt-in ngrok) are honored.

Consequences:

- **A backend cutover is just a build.** Ship a release whose
  `.env.production` names the new host; every machine — including
  colocate machines with a stale `GatewayUrl` persisted in `config.json`
  — follows it on update. No per-machine cleanup, no config migration.
- `config.json` is never rewritten; stale values simply stop mattering.
- Guards that predate this refactor still apply: stale ngrok `AIBaseUrl`
  is ignored without `AllowNgrokAIBaseUrl=true`; loopback `ApiBaseUrl` /
  `LoginWebUrl` dev leftovers are ignored.
- **UAT escape hatch:** `"AllowBackendOverride": true` in `config.json`
  lets that one machine honor azurewebsites overrides again (e.g. steer a
  Release build at staging). Loopback/ngrok guards still apply.

`config.json` lives at `%AppData%\RevitWebAppSync\config.json`.

---

## OTA update (fleet delivery)

`Services/UpdateService.cs` polls `ResolvedUpdateFeedUrl` — default is the
**GitHub Releases feed of this repo**
(`releases/latest/download/version.json`), independent of any backend:

- `version` newer than the running build → download `url` zip, verify
  optional `sha256`, stage to `%LocalAppData%\Bina\RevitSync\versions\<ver>\`.
  Applied by BinaLoader at the **next Revit start** (nothing running is
  overwritten).
- `mandatory` (default **true** when omitted) gates every ribbon command
  until the update is staged — publishing a mandatory release effectively
  write-freezes outdated clients.
- Rollback = ship a **higher-numbered** fixed version. Newest-on-disk wins;
  pointing the feed at an older version is a deliberate no-op.
- The bina-ai backend also serves `/addin/version.json` — it is a cached
  proxy of the same GitHub feed (rate-limit shield / future rollout
  control), not a separate source of truth.

---

## Colocate deployment (engine mode)

Engine mode moves the agent loop onto the drafter's machine: the add-in
spawns a bundled Python engine on `localhost`, and the engine's **only**
cloud contact is the `/gateway/*` surface of the deployed backend
(retrieve, signals, metered inference). Provider keys never leave the
cloud.

### Configuration surface

| Value | Where it lives | How it gets there |
|---|---|---|
| `GatewayUrl` | `config.json` | seeded once from installer-dropped `bina-defaults.json` (`build-installer.ps1 -GatewayUrl`); **resolved env-first** at read time via `ResolvedGatewayUrl`, so cutovers follow the build |
| `DeviceToken` | `config.json` | minted at login (`POST {gateway}/auth/device-token`, 14-day, revocable); proactively re-minted within 3 days of expiry |
| `EngineSecret` / ports | `config.json` / spawn env | generated on first run; per-boot values passed as env vars, never files |
| `AIBaseUrl` | `config.json` | auto-set to `http://localhost:<EngineHostPort>` when engine mode activates |

`EngineManager` passes the env contract at spawn: `BINA_ENGINE=1`,
`BINA_ENGINE_SECRET`, `BINA_ENGINE_PORT`, `BINA_GATEWAY_URL`
(= `ResolvedGatewayUrl`), `BINA_ENGINE_TOKEN`. The engine refuses to start
if a provider key is present on a gateway-configured machine (poison-pill,
`bina-ai/app/engine/config.py`).

### Engine bundle lifecycle

1. **Build** (Windows, in bina-ai):
   `pwsh scripts/build-engine-bundle.ps1 -Version <v> -Smoke` →
   `bina-engine-<v>.zip` (embeddable CPython + engine deps + `app/` source
   + `engine-version.json` + `run-engine.cmd`).
2. **Deliver — two channels:**
   - **Installer:** `build-installer.ps1 -EngineZip <path>` seeds it at
     `%LocalAppData%\Bina\RevitSync\engine\<ver>\`. In CI the same seeding
     happens when the `ENGINE_ZIP_URL` repo variable is set.
   - **OTA engine channel:** optional flat `engineVersion` / `engineUrl` /
     `engineSha256` fields in `version.json` (emitted by CI from
     `ENGINE_ZIP_URL`). Newer → download, sha256-verify, extract; picked up
     at next Revit start. Best-effort, never blocks the add-in update.
3. **Run:** `EngineManager` spawns the newest-semver `engine\<ver>\`,
   health-gates readiness (≤20 s), respawns on crash (≤3, then
   `error:crash-loop`). `min_addin_version` in the bundle manifest blocks
   spawn under a too-old add-in (`error:addin-too-old`).
4. **Rollback:** delete the newest `engine\<ver>\` dir (machine) or ship a
   higher-numbered fixed bundle through the feed (fleet). The updater never
   prunes old versions.

---

## Release procedure (one tag)

1. Windows box: `installer\build-installer.ps1 -Version <v>` + run the
   relevant `COPILOT-TESTING.md` sections (§18 smoke, §20 engine, §21
   multi-year payloads). CI has no Revit — this is the gate.
2. Merge to the release branch (`develop` lineage; `main` is stale — do
   not use it).
3. Engine riding along? Build the bundle, upload the zip, set/refresh the
   `ENGINE_ZIP_URL` repo variable.
4. `git tag v<ver> && git push --tags` → CI publishes per-TFM payloads
   (net48 / net8.0 / net10.0 + targets-map manifest), both loader shims,
   OTA zip, `version.json`, installer EXE, GitHub Release. Feed is live
   immediately.
5. **Sign the installer** locally (Certum SimplySign — CI cannot sign) and
   swap it onto the release:
   `build-installer.ps1 -Version <v> -SignCert <thumbprint> -TimestampUrl
   http://time.certum.pl`, then `gh release upload v<ver> ... --clobber`.
6. Fleet updates at next Revit start (mandatory gate); fresh machines get
   `releases/latest/download/RevitCopilotSetup.exe`.

### Backend cutover checklist (e.g. staging → prod)

- [ ] New backend live, DB migrated/copied, health-checked
      (`bina-ai/DEPLOYMENT.md`).
- [ ] `.env.production` `BASE_URL` → new host (this is the entire cutover
      on the add-in side; env-first resolution carries colocate machines).
- [ ] Windows gate (step 1 above) against the new backend.
- [ ] Quiet hour: re-copy DB if it drifted since the first copy, then tag.
- [ ] Watch: backend logs, `/telemetry/events` ingest, Langfuse traces.
- [ ] Old backend stays up as the staging/test environment.

### Gotchas

- **Layout-transition releases must go installer-first, not OTA-only** —
  pre-multi-year loaders can't read subfolder payloads and `mandatory`
  would nag-loop them. Loader shims only ever update via installer (Revit
  file-locks them in `Addins\`).
- CI builds are **unsigned**; unsigned spawning binaries trip AV
  (RAV Endpoint false-positive, 2026-07-13). Sign before drafters install.
- A `Staging`-configuration build must never reach the fleet: its updater
  is disabled by design and its data lands in the staging DB. UAT builds
  get non-release version numbers (e.g. `0.0.20-uat`).
- **The OTA payload is pruned to Windows x64** (`installer/prune-payload.ps1`,
  called by CI and `build-installer.ps1`): non-`win-x64` RID natives and
  unused Lato weights are deleted after publish, and the build fails if
  `qpdf.dll`/`QuestPdfSkia.dll` go missing. Adding a dependency with native
  assets or a new PDF font weight? Check the keep-lists in that script.
