# OTA Update Size Reduction — Design

**Date:** 2026-07-24
**Status:** Approved (phases A→B→C, implemented in order; each phase ships alone)
**Problem:** The OTA zip is 132MB (v0.0.29) and every machine downloads all of
it on every release. Disk also grows without bound: each update leaves a full
~328MB `versions\<ver>\` folder behind forever.

## Measured composition (v0.0.29, 328MB uncompressed → 132MB zip)

| Piece | Size | Verdict |
|---|---|---|
| `runtimes/` × 3 TFMs | 72MB each (216MB) | QuestPDF/qpdf natives for 7 platforms; only `win-x64` (~16MB) is ever loaded — Revit is Windows x64 only |
| Roslyn (`Microsoft.CodeAnalysis*`) × 3 | 9–12MB each | Live feature (copilot code exec, `Services/CodeExecutor.cs`) — keep |
| Satellite language folders × 3 | ~6.5MB each (13 langs) | Roslyn localization — nobody reads Czech compiler errors here |
| `LatoFont/` × 2 (net8.0, net10.0) | 11MB each (19 files) | QuestPDF default PDF font; ships 18 weights |
| OpenXml + ClosedXML × 3 | ~7MB each | Excel export — keep |
| Everything else | ~25MB/TFM | Real dependencies — keep |

Each machine uses exactly ONE of the three TFM folders (`net48` → Revit
2023/24, `net8.0` → 2025/26, `net10.0` → 2027).

Dead ends checked: XML docs (zero), PDBs (already stripped), 7z/zstd (updater
extracts with System.IO.Compression — plain zip required), IL trimming
(unsafe with WPF + Roslyn reflection), dropping net48 (2023/24 fleet is live).

## Industry alignment

Phase A/B ≈ Android per-ABI splits (ship only what the device runs). Phase C ≈
Docker layer caching / Squirrel.Windows (content-hashed stable layer, tiny
changing layer). Byte-level diffing (Chrome Courgette, Steam chunking) is
deliberately out of scope — it pays off at millions-of-users scale and would
save ~1MB over Phase C here.

---

## Phase A — build-side prune (132MB → ~50MB zip)

Build scripts + one csproj line. No updater/loader/feed change. Fleet gets the
smaller zip at the next tag automatically.

1. **Prune non-Windows natives.** After each per-TFM `dotnet publish`, delete
   every `runtimes/<rid>/` subfolder except `win-x64` (and bare `win` if
   present). Drops linux/osx/arm/musl/win-x86 (~56MB uncompressed per TFM).
   Safe: the host resolves RID assets at load time and never touches other
   RIDs' folders; net48 likewise only loads win-x64 natives. Implemented
   identically in `release.yml` (publish step) and
   `installer/build-installer.ps1` (they already share the pdb-strip pattern).
2. **Kill satellite languages.**
   `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` in
   `RevitWebAppSync.csproj`.
3. **Prune Lato weights.** Keep `Lato-Regular`, `Lato-Bold`, `Lato-Italic`,
   `Lato-BoldItalic` + `OFL.txt`. Before implementing: grep report/PDF code
   for other weight usage (Light/SemiBold/etc.); any weight found in use stays.
   QuestPDF falls back silently on a missing weight — the guard against a
   styling regression is the grep plus the Windows PDF smoke test.
4. **Build guard** (pattern of the existing ≥1MB installer guard): after
   pruning, every TFM folder must still contain
   `runtimes/win-x64/native/qpdf.dll` and `QuestPdfSkia.dll`, else the build
   fails. Never ship a payload that cannot render PDF.

**Test gate (Windows, pre-tag):** normal smoke + generate one PDF report and
one Excel export from a pruned build.

---

## Phase B — per-TFM download (~50MB → ~18MB per machine)

1. **CI** emits four payload zips: `RevitWebAppSync-<v>-net48.zip`,
   `-net8.0.zip`, `-net10.0.zip` (~18MB each) **and** the legacy combined
   `RevitWebAppSync-<v>.zip`. The combined zip is dropped one release cycle
   after the fleet is on a Phase-B updater.
2. **Feed schema** (`version.json`) gains, alongside the legacy `url`/`sha256`:
   ```json
   "files": {
     "net48":   { "url": "...", "sha256": "..." },
     "net8.0":  { "url": "...", "sha256": "..." },
     "net10.0": { "url": "...", "sha256": "..." }
   }
   ```
   The root `manifest.json` targets map (year → TFM subfolder) already tells
   the updater which TFM serves which year.
3. **`Services/UpdateService.cs`** (ships inside the addin — OTA-updatable,
   loader untouched): detect installed Revit years by scanning
   `%APPDATA%\Autodesk\Revit\Addins\<year>\BinaSync.addin`, map years → TFMs,
   download only the needed zips, stage them under one
   `versions\<ver>\<tfm>\` layout, and write a root `manifest.json` whose
   targets map lists **only the years whose subfolders were actually staged**
   (a map entry pointing at a missing folder makes the loader skip the whole
   version for that year — verified `BinaLoader/LoaderApp.cs:246`).
   `.complete` is written only after every needed TFM is verified.
4. **Fallback:** feed without `files` (or an old updater reading a new feed) →
   legacy combined-zip path. Both directions stay compatible during the
   transition release.
5. **Multi-generation machines** (e.g. Revit 2024 + 2026 installed) download
   the union of needed TFM zips — still far below today's 132MB.
6. **`sign-release.ps1`** produces and uploads the same four zips + feed.

**Test gate:** single-year machine, multi-year machine, and an old-updater
client against the new feed (legacy path).

---

## Phase C — deps cache + disk prune (typical update ~2MB)

1. **Split each per-TFM zip in two:**
   - `deps-<tfm>-<hash>.zip` (~16MB): everything except the app set. `<hash>`
     = sha256 over the sorted (path, file-sha256) list — same deps → same
     hash across releases.
   - `app-<tfm>-<v>.zip` (~2MB): `RevitWebAppSync.dll`,
     `RevitWebAppSync.deps.json`, `RevitWebAppSync.dll.config`,
     `manifest.json`, `bina-defaults.json` (when present).
2. **Feed** per-TFM entries grow `depsUrl`/`depsSha256`/`depsId` +
   `appUrl`/`appSha256`.
3. **Updater:** cache at `%LocalAppData%\Bina\RevitSync\depscache\<depsId>\`.
   Staging = (cache hit ? copy : download+verify+extract deps) + download
   app zip + extract over it. Deps re-download only on a package bump
   (~1 in 10 releases). Sha-verify both zips; `.complete` last, as today.
4. **Disk prune:** after a successful stage, delete `versions\<ver>\` folders
   older than the previous version (keep newly staged + one rollback), and
   `depscache` entries not referenced by either. The loader never prunes
   (unchanged); the updater is the only writer.
5. Legacy per-TFM zips (Phase B) keep being published one release cycle, then
   drop to app+deps only.

**Test gate:** update with cache hit (2MB path), update with deps change
(full path), rollback to previous version still loads, disk prune leaves
exactly two versions.

---

## Rollout order

1. Phase A → next regular release. Watch: PDF/Excel telemetry errors.
2. Phase B → its own release; keep combined zip until fleet ≥ that version.
3. Phase C → after B settles; keep per-TFM zips one cycle.

Each phase is a separate feature branch → PR → develop → tag, per repo
convention. The "layout-transition releases go installer-first" gotcha
(DEPLOYMENT.md) does **not** apply: `versions\<ver>\<tfm>\` layout and loader
manifest contract are unchanged throughout.

## Risks

| Risk | Phase | Mitigation |
|---|---|---|
| PDF natives missing after prune | A | build guard + Windows PDF smoke |
| Report uses pruned Lato weight | A | pre-implementation grep + PDF visual check |
| Old updater meets new feed | B | legacy `url` kept during transition |
| Half-staged multi-TFM version | B | `.complete` only after all TFMs verified |
| Cache corruption | C | sha verify on every compose; corrupt → re-download |
| Rollback needs deleted version | C | always keep current + previous |
