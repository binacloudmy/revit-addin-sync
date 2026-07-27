# PC Setup Playbook (for an AI agent running on this Windows PC)

You are an AI coding agent (e.g. Claude Code) running on a **Windows PC that has Autodesk Revit**.
Your job: build and install the `revit-addin-sync` Revit add-in, and point its AI calls at a
remote Mac through an ngrok tunnel. Work through the steps in order. After each step, run the
verification command and do not continue until it passes. Report what you did at the end.

---

## Context you need

- This PC runs **Revit** + the add-in. A separate **Mac** runs the `bina-ai` backend and an
  **ngrok** tunnel that exposes it at a public HTTPS URL.
- The add-in talks to **two** backends:
  - **Login / projects / sync** → the cloud (`bina-ai-staging.azurewebsites.net`). Leave this alone.
  - **AI features** (compliance, revit-ai code-gen, JKR) → the Mac, via the ngrok URL. You configure this.
- The tunnel URL to use is:
  ```
  AI_BASE_URL = https://loma-cachinnatory-unhypothetically.ngrok-free.dev
  ```
  This is the user's reserved ngrok domain (already filled in). It is stable and does not rotate.

> **IMPORTANT — you need the add-in SOURCE CODE, not just this file.**
> This `.md` may have been copied to the PC on its own. To build the add-in you need the full
> `revit-addin-sync` source folder (the one containing `RevitWebAppSync.csproj` and
> `revit-addin-sync.sln`) present on this PC. Step 2 checks for it and tells you what to do if it's
> missing. If only this `.md` is here, you cannot build — ask the user to copy the whole folder
> (or give you the git repo URL).

---

## Step 0 — Confirm you're on the right machine

```cmd
echo %OS%
ver
```
Expect Windows. If this is not Windows, STOP — this playbook is Windows-only.

---

## Step 1 — Check / install prerequisites

### 1a. .NET 8 SDK (required — add-in targets `net8.0-windows`)
```cmd
dotnet --list-sdks
```
- If you see an `8.0.xxx` entry → OK.
- If `dotnet` is missing or no 8.0 SDK → install it:
  ```cmd
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
  ```
  Then re-open the terminal and re-run `dotnet --list-sdks`.

### 1b. Git (to get the code, if not already present)
```cmd
git --version
```
If missing: `winget install --id Git.Git -e`

### 1c. Revit 2025 / 2026 / 2027 (must already be installed by the user)
```cmd
dir "C:\Program Files\Autodesk" /b
```
Expect a `Revit 20XX` folder where XX is 25, 26, or 27. If none exists, STOP and tell the user
Revit must be installed first — you cannot install Revit yourself.

> Note: you do **not** need Visual Studio. `dotnet build` from the terminal is enough.
> You do **not** need Python, ngrok, or the bina-ai backend on this PC — those run on the Mac.

---

## Step 2 — Locate the add-in source on the PC

You need the folder that contains `RevitWebAppSync.csproj`. First, check the folder this `.md` is in
(and its parent) — the user most likely copied the whole `revit-addin-sync` folder here:
```cmd
dir RevitWebAppSync.csproj revit-addin-sync.sln 2>nul
```
- **Found it** → you're in the right place. Go to Step 3.
- **Not found** → search common spots for it:
  ```cmd
  where /r C:\ RevitWebAppSync.csproj 2>nul
  ```
  `cd` into the folder that contains it.

If it exists nowhere on this PC, **STOP** and tell the user:
> "I only have PC-SETUP-AI.md, not the add-in source. Please copy the entire `revit-addin-sync`
> folder to this PC (or give me the git repo URL to clone)."

If the user gives a repo URL:
```cmd
git clone <REPO_URL> revit-addin-sync
cd revit-addin-sync
```

---

## Step 3 — Build the add-in (auto-installs into Revit)

```cmd
dotnet build -c Release
```

The build **auto-detects** the installed Revit version and **auto-copies** the compiled DLLs and the
`.addin` manifest to:
```
%APPDATA%\Autodesk\Revit\Addins\<RevitVersion>\
```

If Revit is installed in a non-default location, pass the path explicitly:
```cmd
dotnet build -c Release -p:RevitPath="C:\Program Files\Autodesk\Revit 2026"
```

**Verify the build succeeded and the files landed** (replace 2026 with the detected version):
```cmd
dir "%APPDATA%\Autodesk\Revit\Addins\2026\RevitWebAppSync.dll"
dir "%APPDATA%\Autodesk\Revit\Addins\2026\*.addin"
```
Both must exist. If the build reports `Build succeeded` but the files are missing, the Addins folder
for that version didn't exist yet — create it and re-run the build:
```cmd
mkdir "%APPDATA%\Autodesk\Revit\Addins\2026"
dotnet build -c Release
```

---

## Step 4 — Point AI calls at the Mac (no rebuild needed)

Create the config file that overrides ONLY the AI base URL. This is the critical step.

```cmd
mkdir "%APPDATA%\RevitWebAppSync" 2>nul
```

Write this file at `%APPDATA%\RevitWebAppSync\config.json`:
```json
{
  "AIBaseUrl": "https://loma-cachinnatory-unhypothetically.ngrok-free.dev",
  "AllowNgrokAIBaseUrl": true
}
```

> `AllowNgrokAIBaseUrl: true` is **MANDATORY**. Without it the add-in deliberately ignores any
> ngrok URL and silently falls back to the cloud (see `BinaConfig.cs`, `ResolvedAIBaseUrl`).
> Do **not** set `ApiBaseUrl` or `LoginWebUrl` — login and projects must keep using the cloud.

Verify the file is valid JSON and contains the flag:
```cmd
type "%APPDATA%\RevitWebAppSync\config.json"
powershell -Command "Get-Content \"$env:APPDATA\RevitWebAppSync\config.json\" | ConvertFrom-Json | Format-List"
```
The PowerShell command must print `AIBaseUrl` and `AllowNgrokAIBaseUrl : True` without error.

---

## Step 5 — Verify the tunnel to the Mac is reachable

The Mac must be running its backend + ngrok (the user starts that with `./run-stack.sh <domain>`).

### 5a. Reachability
From this PC:
```cmd
curl https://loma-cachinnatory-unhypothetically.ngrok-free.dev/health
```
- A 200 / JSON response → the PC can reach the Mac.
- `Could not resolve host` / timeout → the Mac side isn't running, or the domain is wrong. Tell the user.

### 5b. Real AI call (confirms the Mac's AI keys work, not just the tunnel)
The Mac's `revit-ai` agent is a code generator, so send it a Revit task and expect C# back:
```cmd
curl -X POST "https://loma-cachinnatory-unhypothetically.ngrok-free.dev/agents/revit-ai/runs" -F "message=Generate Revit API C# to create a new level at elevation 3000mm named L2." -F "stream=false"
```
- A 200 response whose JSON `content` contains a `code` field with C# (e.g. `Level.Create(doc, ...)`)
  → the full chain works: PC → Mac backend → Azure/Claude → generated code back. (~8s is normal.)
- HTTP 500 or an auth/model error → the tunnel is fine but the Mac's `.env` keys are missing or
  wrong. Tell the user to check `bina-ai/.env` on the Mac. This is NOT a PC-side problem.

---

## Step 6 — Final check inside Revit (user does this; you just instruct)

1. Launch Revit (the detected version).
2. If Revit shows a security prompt about loading the add-in, choose **Always Load**.
3. Look for the **BINA** ribbon tab. If it's missing, the DLL didn't load — re-check Step 3.
4. Sign in (this hits the cloud). Then run an **AI feature** (e.g. a compliance/JKR check) —
   that request goes to the Mac through the tunnel.

---

## Troubleshooting

- **BINA ribbon missing** → DLL/manifest not in the Addins folder, or Revit blocked it. Re-run Step 3,
  confirm Step 3's `dir` checks pass, restart Revit, choose "Always Load".
- **AI features hit the cloud instead of the Mac** → `config.json` missing, malformed, or
  `AllowNgrokAIBaseUrl` not `true`. Re-do Step 4.
- **Build error: RevitAPI.dll not found** → Revit not installed, or wrong path. Pass
  `-p:RevitPath="C:\Program Files\Autodesk\Revit <ver>"`.
- **`/health` curl fails** → Mac's `run-stack.sh` isn't running, or the reserved domain in
  `config.json` doesn't match the Mac's ngrok domain. They must be identical.
- **`/health` works but Step 5b AI call errors (500/auth)** → Mac-side `.env` keys problem, not a PC
  issue. The user must fix `bina-ai/.env` on the Mac and restart `run-stack.sh`.
- **Build succeeded but no files copied** → the version's Addins folder didn't exist at build time;
  `mkdir` it (Step 3) and rebuild.

## When done, report:
- .NET SDK version found/installed
- Revit version detected
- That `RevitWebAppSync.dll` + `.addin` are in the Addins folder
- That `config.json` exists with `AllowNgrokAIBaseUrl: true`
- Result of the `/health` curl (Step 5a) and the AI smoke-test (Step 5b)
