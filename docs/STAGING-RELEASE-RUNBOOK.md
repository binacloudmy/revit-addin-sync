# Staging release runbook (Revit, Navisworks, Civil 3D)

Handover doc. Covers how a **signed staging** build of each BINA add-in is cut,
published and verified. Prod is out of scope (see `DEPLOYMENT.md` and the
prod-signing notes: operator-only on the Windows signing box).

Last verified: 2026-11-04 (Navis v0.0.15-staging, Civil 3D v0.0.9-staging).

## 1. The three add-ins

| Add-in | Repo | Tag format | TM One prefix (bucket `bina-dev-storage`) | Staging feed |
|---|---|---|---|---|
| Revit Copilot | `binacloudmy/revit-addin-sync` | `vX.Y.Z-staging` | `revit-copilot/releases-staging/` | `https://bina-ai-stg-onebistro.binacloud.ai/addin/version.json` |
| Navisworks | `binacloudmy/navis-addin-sync` | `vX.Y.Z-staging` | `navis-sync/releases-staging/` | `.../addin/navisworks/version.json` |
| Civil 3D | `binacloudmy/civil3d-addin-sync` | `vX.Y.Z-staging` | `civil3d/releases-staging/` | `.../addin/civil3d/version.json` |

Each prefix holds `installers/*-setup.exe`, `ota/*.zip` and a `latest.json`
pointer. **Flipping `latest.json` is go-live.** The bina-ai backend reads it and
serves it from the feed URL above (cached up to ~10 min, so the feed can lag).

GitHub Releases is NOT where staging lives (only old drafts/pre-releases from
before 0.0.29). Do not look there for the latest staging build.

## 2. Two publish paths: use the signed one

Pushing a `vX.Y.Z-staging` tag starts CI:

- **Revit / Civil 3D (GitHub Actions `release.yml`)**: `release` job builds and
  tests, then `publish-staging-unsigned` uploads an UNSIGNED zip + EXE and flips
  `latest.json`. 
- **Navis**: no GitHub Actions. The `Jenkinsfile` builds tag builds
  (multibranch job with tag discovery). I could not see Jenkins from my session,
  so I never confirmed it started.

We release staging **signed, from the VMPal Windows VM**, not via the unsigned
CI path. Consequences:

- Revit: the unsigned CI publish WILL run on tag push. Cancel it immediately
  (`gh run cancel <id>`) before `publish-staging-unsigned` starts, or do not push
  the tag until the VM is ready. Otherwise it overwrites the pointer with an
  unsigned build.
- Civil 3D: `publish-staging-unsigned` currently fails harmlessly
  (`TMONE_SERVER` repo secret is empty: "Bad value for --endpoint-url"), so it
  publishes nothing. Do not rely on that; it is an accident, not a design.
- A tag push is still required: the VM script checks out the tag from a git
  bundle, and the tag is the version source.

## 3. Prerequisites

1. **VMPal "Windows VM 1"** (id `CA168120-98E1-48E4-BC1B-2F87EFE41022`, Windows
   11 ARM64, user `ashraf`). Must be running. It has Git, gh, PowerShell 7,
   .NET 8 + 10 SDK, AWS CLI, Inno Setup 6, x64 signtool.
2. **SimplySign connected** (Certum cert, BINA CLOUDTECH SDN BHD, thumbprint
   `1F01271A39453D964AB61D482B57B7E2E580A649`, valid to 2027-06-24). It needs a
   human: tray icon -> Connect -> email + 6-digit one-time code from the phone
   app. Codes live ~30 s. A wrong/expired code gives "Invalid user name or
   token"; do not hammer it. The session drops after VM suspend, so reconnect
   before every release. Release scripts abort unless exactly one valid
   code-signing cert is in `Cert:\CurrentUser\My`.
3. **TM One staging keys**, saved DPAPI-encrypted at
   `C:\bina-test\secrets\tmone-staging.clixml`, loaded by
   `C:\bina-test\tools\tmone-creds.ps1` (`Use-TmoneCreds staging`,
   `Clear-TmoneEnv`). Prod keys are deliberately not saved.
4. **VM clock** must be correct (SigV4 rejects skew). It drifts after suspend;
   compare with `Get-Date` first.

## 4. Procedure (same for all three; example values from 2026-11-04)

Let `$REPO`, `$TAG` (e.g. `v0.0.15-staging`), `$NAME` (`navis` | `civil3d` |
`revit`).

**A. Pick the version and tag on the Mac**

1. Staging branch is the source. Latest tag: `git tag --sort=-creatordate | grep staging | head`.
   Next = patch+1. Check what is new: `git log <last-tag>..origin/staging`.
2. `git tag $TAG origin/staging && git push origin $TAG`.
   (Revit: have the VM ready first, see section 2.)

**B. Ship the source to the VM** (the VM clones from a bundle; its `origin`
is not the real remote)

```sh
git bundle create /tmp/rel/srv/$NAME.bundle $TAG
cd /tmp/rel/srv && python3 -m http.server 8765 --bind 0.0.0.0 &
```

In the VM (reaches the Mac at `10.0.2.2`):

```powershell
$d = "C:\bina-test\release\$NAME-stg-v0.0.X"; mkdir $d -Force
Invoke-WebRequest "http://10.0.2.2:8765/$NAME.bundle" -OutFile "$d\$NAME.bundle"
```

**C. Release script.** Copy the previous one
(`C:\bina-test\release\<name>-stg-v<prev>\release.ps1`) and replace the version
and folder. It: puts Git/AWS/dotnet/signtool(x64) on PATH, loads staging keys,
asserts one valid cert, clones the bundle to `repo\`, `git checkout --detach $TAG`,
then runs `installer\sign-release.ps1`:

- Navis / Revit: `-Tag $TAG -Thumbprint <sha1>`
- Civil 3D: `-Tag $TAG -SignCert <sha1>` (different parameter name)
- Navis: add `-MinLoaderVersion X.Y.Z` ONLY if the loader (`BinaLoader/`)
  changed in this release. `sign-release.ps1` does not carry
  `min_loader_version` forward. Check with
  `git diff --stat <prev-tag> $TAG -- BinaLoader`.

`sign-release.ps1` builds (plugin, per-year loaders, OTA zip, setup EXE), signs,
uploads to TM One and flips `latest.json`. Revit staging additionally needs the
engine bundle (see gotchas).

**D. Run it as the exec job** (not as a spawned window; spawned windows die when
the exec job ends):

```
vmpal_exec: pwsh.exe -NoProfile -File <release.ps1>, timeoutSeconds 5400, waitSeconds 0
```

Then poll `vmpal_exec_result`, or just read
`C:\bina-test\release\<name>-stg-v<ver>\release.log` (a transcript). Success
ends with `RESULT=OK` and the "now serving X.Y.Z" line.

**E. Signing prompt.** If SimplySign has dropped, a "SimplySign Desktop /
Login" dialog pops up mid-run (the signtool probe triggers it) and the job
sits at "Signing artifacts...". Enter the one-time code in the VM window, or
via `vmpal_act` (click the code field, type, Enter). Look for "SimplySign
connected / Amount of certificates available: 1".

**F. Verify**

```sh
curl -s https://bina-ai-stg-onebistro.binacloud.ai/addin/<product>/version.json | jq '.version,.installer_url'
```

`installer_url` is a presigned OBS link (1 h). The feed can show the old version
for up to ~10 min. To get a link before then, presign on the VM with the staging
keys: `aws s3 presign s3://bina-dev-storage/<prefix>/installers/<file> --endpoint-url $env:TMONE_SERVER --expires-in 21600`
(set `AWS_DEFAULT_REGION=my-kualalumpur-1`, map `TMONE_*` to `AWS_*`).

**G. Cleanup:** stop the Mac http.server (`pkill -f "http.server 8765"`);
`Clear-TmoneEnv` already ran in the script's `finally`.

## 5. How the Mac drives the VM

The session's MCP server may point at a deleted VM id (`12A68500-...`), in
which case `vmpal_status` says "no private control socket". Workaround: a
Python bridge that holds `VMPalMachine --mcp <current id>` over stdio and
exposes a unix socket (`/tmp/vmb/bridge.py`, called with `/tmp/vmb/call.py
<tool> '<json>'`). The new id is in
`~/Library/Application Support/VMPal/Library.json`. Never run
`VMPal.app/Contents/MacOS/VMPal help` (hangs). `vmpal_exec` runs at most two
commands at once; `waitSeconds` max 30. If `/tmp` was wiped, recreate the
bridge from this description.

## 6. Gotchas

- Staging is pointer-based and unsigned-capable; do not run the CI path by
  accident (section 2).
- `min_loader_version` (Navis) is dropped unless passed explicitly.
- VM network truncates long `aws s3 cp` downloads (~2 MB). Revit staging engine
  zip: `aws s3 presign` + `curl.exe -C - --retry`, then pass
  `fetch-engine-bundle.ps1 -ZipFile` (still sha-checked). Uploads are fine.
- Windows: `winget` needs `--source winget`.
- Web UI harness (net8.0-windows WPF) on the ARM VM: build
  `-p:PlatformTarget=AnyCPU -p:UseAppHost=false`.
- Mac build: .NET SDK 10 in `~/.dotnet`; all TFMs build, tests only run via
  the scratch harness.
- PRs target `develop`, never `main`; feature branches only; the user merges.
- Do not touch prod feed/zips/telemetry when debugging. A blank
  `UPDATE_FEED_URL` falls back to the prod GitHub feed.

## 7. Open items / unknowns

- Navis Jenkins tag job: unconfirmed it fires; check the multibranch job.
- Civil 3D `TMONE_*` repo secrets are empty; Revit's are set (kept in repo
  secrets). Decide whether CI unsigned publish should exist at all for Civil.
- The Navis feed path was found by trial (`/addin/navisworks/`); `/addin/navis/`
  returns nothing.
- The repo has no release script checked in; the VM per-release `release.ps1`
  lives only on the VM under `C:\bina-test\release\`. Worth committing a
  parameterised copy.
