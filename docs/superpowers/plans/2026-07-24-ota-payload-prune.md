# OTA Payload Prune (Phase A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shrink the OTA update zip from 132MB to ~50MB by pruning files no Windows x64 Revit machine ever loads — no updater, loader, or feed changes.

**Architecture:** One new shared PowerShell script (`installer/prune-payload.ps1`) prunes the published payload tree and guards that PDF natives survived; both build paths (CI `release.yml` and local `installer/build-installer.ps1`) call it after publishing. A one-line csproj change stops satellite-language folders from being published at all.

**Tech Stack:** MSBuild (csproj), PowerShell (5.1 + 7 compatible), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-07-24-ota-size-reduction-design.md` (Phase A section).

## Global Constraints

- Work on branch `feat/ota-size-reduction` (exists; holds the spec).
- RIDs kept in `runtimes/`: exactly `win-x64` and `win`. Everything else (linux*, osx*, *arm*, win-x86, …) is deleted.
- Lato files kept: exactly `Lato-Regular.ttf`, `Lato-Bold.ttf`, `Lato-Italic.ttf`, `Lato-BoldItalic.ttf`, `OFL.txt` (grep-verified: `Services/ReportExporter.cs` uses only `.Bold()` / `.Italic()`; WPF `FontWeights.*` uses system fonts, not Lato).
- Guard files that MUST exist per TFM subfolder after pruning: `runtimes/win-x64/native/qpdf.dll` and `runtimes/win-x64/native/QuestPdfSkia.dll` — missing ⇒ build fails.
- `prune-payload.ps1` must run under both Windows PowerShell 5.1 (`build-installer.ps1` callers) and PowerShell 7 (CI `shell: pwsh`): use nested `Join-Path` (no multi-arg form), no PS7-only syntax.
- This dev machine is macOS: `pwsh` is NOT installed here. Task 1 verifies locally (dotnet works via the official SDK); Tasks 2–4 script tests run on the Windows gate box — commit the code now, carry the listed test commands into the Windows gate checklist (Task 5).
- Commit after every task. Do not create version-suffixed file copies; edit files in place.

---

### Task 1: Stop publishing satellite languages

**Files:**
- Modify: `RevitWebAppSync.csproj:37` (first `<PropertyGroup>`, after `<CopyLocalLockFileAssemblies>`)

**Interfaces:**
- Produces: publish output with no `cs/`, `de/`, `ru/`, … folders (≈6.5MB less per TFM). Tasks 2–4 assume language folders are already absent (the prune script does not handle them).

- [ ] **Step 1: Add the property**

In `RevitWebAppSync.csproj`, directly under `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`:

```xml
    <!-- Roslyn ships resource DLLs for 13 UI languages (~6.5MB per TFM in the
         OTA payload); the addin surfaces compiler messages in English only. -->
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
```

- [ ] **Step 2: Verify by publishing one TFM locally**

```bash
dotnet publish RevitWebAppSync.csproj -c Release -f net8.0-windows \
  -o /tmp/prune-check-net8 -p:Version=0.0.0 && \
ls /tmp/prune-check-net8 | grep -E '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)$' \
  ; echo "exit=$? (1 = no language folders = PASS)"
```

Expected: publish succeeds; grep finds nothing, prints `exit=1 (…PASS)`.

- [ ] **Step 3: Confirm payload contents otherwise intact**

```bash
ls /tmp/prune-check-net8/runtimes && ls /tmp/prune-check-net8/LatoFont | head -3 && \
  ls /tmp/prune-check-net8/Microsoft.CodeAnalysis.CSharp.dll
```

Expected: `runtimes` still lists all RIDs (pruning them is Task 2's job), LatoFont and Roslyn still present.

- [ ] **Step 4: Commit**

```bash
git add RevitWebAppSync.csproj
git commit -m "build: publish English-only satellite resources

Roslyn's 13 language folders added ~6.5MB per TFM to the OTA payload;
compiler messages are only ever shown in English."
```

---

### Task 2: Create the prune + guard script

**Files:**
- Create: `installer/prune-payload.ps1`

**Interfaces:**
- Produces: `prune-payload.ps1 -PluginDir <path>` — `<path>` is the payload root containing TFM subfolders (`net48/`, `net8.0/`, `net10.0/`). Deletes non-Windows RIDs and unused Lato weights in every TFM subfolder; throws (non-zero exit) if a TFM subfolder is left without `runtimes/win-x64/native/qpdf.dll` or `QuestPdfSkia.dll`, or if `<path>` has no subfolders. Tasks 3 and 4 call it with exactly this contract.

- [ ] **Step 1: Write the script**

```powershell
# Prunes a published plugin payload tree to what a Windows x64 Revit actually
# loads, then guards that the PDF natives survived.
#
#   installer\prune-payload.ps1 -PluginDir artifacts\plugin
#
# Per TFM subfolder (net48\, net8.0\, net10.0\):
#   - runtimes\<rid>\  deleted for every RID except win-x64 / win (QuestPDF +
#     qpdf ship 7 platforms; Revit is Windows x64 only — 56MB dead per TFM)
#   - LatoFont\        pruned to the weights QuestPDF reports use (Regular,
#     Bold, Italic, BoldItalic; see Services\ReportExporter.cs) + OFL.txt
#
# Guard: runtimes\win-x64\native\qpdf.dll + QuestPdfSkia.dll must remain in
# every TFM subfolder — fail the build rather than ship a payload that cannot
# render PDF. Must stay PowerShell 5.1-compatible (build-installer.ps1 runs
# under powershell.exe): nested Join-Path only.

param(
    [Parameter(Mandatory = $true)][string]$PluginDir
)

$ErrorActionPreference = "Stop"

$keepRids  = @("win-x64", "win")
$keepLato  = @("Lato-Regular.ttf", "Lato-Bold.ttf", "Lato-Italic.ttf",
               "Lato-BoldItalic.ttf", "OFL.txt")
$guardDlls = @("qpdf.dll", "QuestPdfSkia.dll")

$tfmDirs = Get-ChildItem $PluginDir -Directory
if (-not $tfmDirs) { throw "prune-payload: no TFM subfolders under '$PluginDir'" }

foreach ($tfm in $tfmDirs) {
    $runtimes = Join-Path $tfm.FullName "runtimes"
    if (Test-Path $runtimes) {
        Get-ChildItem $runtimes -Directory |
            Where-Object { $keepRids -notcontains $_.Name } |
            ForEach-Object {
                Write-Host "prune-payload: $($tfm.Name)/runtimes/$($_.Name) deleted"
                Remove-Item $_.FullName -Recurse -Force
            }
    }

    $lato = Join-Path $tfm.FullName "LatoFont"
    if (Test-Path $lato) {
        Get-ChildItem $lato -File |
            Where-Object { $keepLato -notcontains $_.Name } |
            ForEach-Object {
                Write-Host "prune-payload: $($tfm.Name)/LatoFont/$($_.Name) deleted"
                Remove-Item $_.FullName -Force
            }
    }

    $native = Join-Path (Join-Path $runtimes "win-x64") "native"
    foreach ($dll in $guardDlls) {
        if (-not (Test-Path (Join-Path $native $dll))) {
            throw "prune-payload: $($tfm.Name) lost runtimes/win-x64/native/$dll — refusing to ship a payload that cannot render PDF"
        }
    }
}

Write-Host "prune-payload: OK ($($tfmDirs.Count) TFM folders, win-x64 natives verified)"
```

- [ ] **Step 2: Synthetic-tree test — happy path (Windows box / any pwsh)**

No pwsh on this Mac — run at the Windows gate (also listed in Task 5's checklist). Expected outcomes are stated so the runner needs no context:

```powershell
$t = Join-Path ([IO.Path]::GetTempPath()) "prune-test"
Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue
foreach ($tfm in @("net8.0", "net48")) {
    foreach ($rid in @("win-x64", "linux-x64", "osx-arm64", "win-x86")) {
        $d = Join-Path (Join-Path (Join-Path (Join-Path $t $tfm) "runtimes") $rid) "native"
        New-Item -ItemType Directory -Force $d | Out-Null
        Set-Content (Join-Path $d "dummy.dll") "x"
    }
    $n = Join-Path (Join-Path (Join-Path (Join-Path $t $tfm) "runtimes") "win-x64") "native"
    foreach ($f in @("qpdf.dll", "QuestPdfSkia.dll")) { Set-Content (Join-Path $n $f) "x" }
}
$lato = Join-Path (Join-Path $t "net8.0") "LatoFont"
New-Item -ItemType Directory -Force $lato | Out-Null
foreach ($f in @("Lato-Regular.ttf", "Lato-Thin.ttf", "Lato-SemiBold.ttf", "Lato-Bold.ttf", "OFL.txt")) {
    Set-Content (Join-Path $lato $f) "x"
}
& installer\prune-payload.ps1 -PluginDir $t
"win-x64 kept:    $(Test-Path (Join-Path $t 'net8.0\runtimes\win-x64'))       (want True)"
"linux deleted:   $(-not (Test-Path (Join-Path $t 'net8.0\runtimes\linux-x64')))  (want True)"
"x86 deleted:     $(-not (Test-Path (Join-Path $t 'net48\runtimes\win-x86')))     (want True)"
"Thin deleted:    $(-not (Test-Path (Join-Path $lato 'Lato-Thin.ttf')))           (want True)"
"Regular kept:    $(Test-Path (Join-Path $lato 'Lato-Regular.ttf'))               (want True)"
"OFL kept:        $(Test-Path (Join-Path $lato 'OFL.txt'))                        (want True)"
```

Expected: script prints per-deletion lines then `prune-payload: OK (2 TFM folders, win-x64 natives verified)`; all six checks print `True`.

- [ ] **Step 3: Synthetic-tree test — guard fires (same box)**

```powershell
Remove-Item (Join-Path $t "net48\runtimes\win-x64\native\qpdf.dll")
& installer\prune-payload.ps1 -PluginDir $t; "exit=$LASTEXITCODE (want non-zero)"
```

Expected: throws `prune-payload: net48 lost runtimes/win-x64/native/qpdf.dll — refusing to ship a payload that cannot render PDF`.

- [ ] **Step 4: Commit**

```bash
git add installer/prune-payload.ps1
git commit -m "build: add payload prune script (Windows-x64-only natives, used Lato weights)

QuestPDF/qpdf ship natives for 7 platforms; Revit loads only win-x64.
Guards that qpdf.dll + QuestPdfSkia.dll survive so a bad prune fails the
build instead of shipping a payload that cannot render PDF."
```

---

### Task 3: Wire prune into the local installer build

**Files:**
- Modify: `installer/build-installer.ps1:113` (after the `.complete` marker line, before the `-EngineZip` staging block)

**Interfaces:**
- Consumes: `prune-payload.ps1 -PluginDir <path>` from Task 2.
- Produces: every local/signed build (`build-installer.ps1` and therefore `sign-release.ps1`, which delegates to it) emits a pruned payload; nothing downstream changes shape.

- [ ] **Step 1: Add the call**

In `installer/build-installer.ps1`, directly after `Set-Content (Join-Path $pluginDir ".complete") $Version`:

```powershell
# Prune to what a Windows x64 Revit loads (non-win RIDs, unused Lato weights)
# + guard the PDF natives. Before signing: fewer files to sign, and a broken
# prune fails here rather than after a cert round-trip.
Write-Host "==> Pruning payload..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "prune-payload.ps1") -PluginDir $pluginDir
```

- [ ] **Step 2: Confirm exactly one call site with the right helper name**

```bash
grep -c "prune-payload.ps1" installer/build-installer.ps1
```

Expected: `1`.

- [ ] **Step 3: Commit**

```bash
git add installer/build-installer.ps1
git commit -m "build: prune payload in local installer builds"
```

Full-build verification happens on the Windows gate (Task 5 checklist): run `installer\build-installer.ps1 -Version 0.0.0` and confirm `artifacts\plugin\net8.0\runtimes\` contains only `win-x64` (and `win` if the restore produced one) and `LatoFont` has 5 files.

---

### Task 4: Wire prune into CI

**Files:**
- Modify: `.github/workflows/release.yml:98` ("Publish plugin" step — after the root-manifest write, replacing nothing; the pdb-strip line stays last)

**Interfaces:**
- Consumes: `prune-payload.ps1 -PluginDir <path>` from Task 2 (present in the same checkout).
- Produces: CI OTA zip (`RevitWebAppSync-<v>.zip`) built from the pruned tree (~50MB); installer seed folder pruned identically.

- [ ] **Step 1: Add the call**

In the `Publish plugin (OTA payload, per-target subfolders)` step, between the root-manifest `Set-Content artifacts/plugin/manifest.json` line and the `Get-ChildItem ... *.pdb | Remove-Item` line:

```powershell
          & installer/prune-payload.ps1 -PluginDir artifacts/plugin
          if ($LASTEXITCODE -ne 0) { exit 1 }
```

(Note: the script `throw`s on guard failure, which already fails the pwsh step; the explicit exit-code check matches the file's house style.)

- [ ] **Step 2: Validate workflow YAML parses**

```bash
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml')); print('yaml OK')"
```

Expected: `yaml OK`.

- [ ] **Step 3: Commit + push, then dry-run CI without tagging**

```bash
git add .github/workflows/release.yml
git commit -m "ci: prune OTA payload before zipping"
git push -u origin feat/ota-size-reduction
gh workflow run release.yml --ref feat/ota-size-reduction -f version=0.0.0
gh run list --workflow=release.yml --limit 1
```

`workflow_dispatch` builds artifacts but creates **no tag, no release, no feed change** (`release.yml` only creates a GitHub Release on `push`) — safe dry run.

- [ ] **Step 4: Verify the dry-run artifact size**

When the run finishes (`gh run watch <run-id>`):

```bash
gh run download <run-id> --name release-0.0.0 --dir /tmp/prune-ci-check
ls -lh /tmp/prune-ci-check/RevitWebAppSync-0.0.0.zip
unzip -l /tmp/prune-ci-check/RevitWebAppSync-0.0.0.zip | grep -E "linux|osx|arm64|win-x86|/(cs|de|ru)/" | head -5
```

Expected: zip ≈ 40–55MB (was 132MB); the grep finds nothing.

---

### Task 5: Document + Windows gate checklist

**Files:**
- Modify: `DEPLOYMENT.md:190` ("Gotchas" section)
- Modify: `docs/superpowers/plans/2026-07-24-ota-payload-prune.md` (this file — tick the deferred Windows steps when run)

**Interfaces:**
- Consumes: everything above; produces the release-readiness record.

- [ ] **Step 1: Add a Gotchas bullet to DEPLOYMENT.md**

Append to the `### Gotchas` list:

```markdown
- **The OTA payload is pruned to Windows x64** (`installer/prune-payload.ps1`,
  called by CI and `build-installer.ps1`): non-`win-x64` RID natives and
  unused Lato weights are deleted after publish, and the build fails if
  `qpdf.dll`/`QuestPdfSkia.dll` go missing. Adding a dependency with native
  assets or a new PDF font weight? Check the keep-lists in that script.
```

- [ ] **Step 2: Commit**

```bash
git add DEPLOYMENT.md
git commit -m "docs: note payload pruning in deployment gotchas"
```

- [ ] **Step 3: Windows gate checklist (before the next release tag)**

Run on the Windows box and tick here:

- [ ] Task 2 Step 2 (synthetic happy path) — six `True`s
- [ ] Task 2 Step 3 (guard fires) — non-zero exit
- [ ] `installer\build-installer.ps1 -Version 0.0.0` → `artifacts\plugin\net8.0\runtimes\` = `win-x64` only; `LatoFont` = 5 files; installer EXE builds
- [ ] Install the 0.0.0 build into a test Revit: generate one PDF report (fonts + natives) and one Excel export (`COPILOT-TESTING.md` smoke)
- [ ] PDF visual check: bold + italic text renders in Lato, not a fallback font

- [ ] **Step 4: Open PR**

```bash
gh pr create --base develop --head feat/ota-size-reduction \
  --title "Phase A: prune OTA payload to Windows x64 (132MB -> ~50MB)" \
  --body "$(cat <<'EOF'
Implements Phase A of docs/superpowers/specs/2026-07-24-ota-size-reduction-design.md.

- English-only satellite resources (csproj)
- installer/prune-payload.ps1: drops non-win-x64 RID natives + unused Lato weights, guards qpdf.dll/QuestPdfSkia.dll
- Wired into release.yml and build-installer.ps1
- CI dry run: zip 132MB -> ~50MB (workflow_dispatch, no release created)

Windows gate checklist lives in docs/superpowers/plans/2026-07-24-ota-payload-prune.md Task 5.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-Review Notes

- Spec coverage: Phase A items 1 (RID prune → Tasks 2–4), 2 (satellite langs → Task 1), 3 (Lato + pre-grep → done in research, keep-list in Task 2), 4 (guard → Task 2), test gate (→ Task 5). Phases B/C intentionally out of scope — separate plans.
- No placeholders; all code complete.
- Contract consistency: `-PluginDir` param name identical in Tasks 2/3/4; guard filenames identical in Task 2 script, Task 5 docs, and spec.
- macOS limitation honestly encoded: pwsh-dependent verifications are deferred to the Windows gate and tracked as unticked checklist items, not skipped silently.
