# BINA Platform Connector — App Store Submission Audit

**Audit date:** 2026-04-27
**Repo:** `/Volumes/AmmarSSD/revit-addin-sync`
**Branch:** `main` (HEAD: `38e4785 feat(AI): Incorporated AI in Revit`)
**Target:** Autodesk App Store submission (Revit 2024, 2025, 2026)
**Publisher:** BINA CLOUDTECH SDN BHD

---

## TL;DR

The codebase is a working internal/dev tool, **not** App Store ready. To submit, we need to:

1. **Remove or fundamentally rework the AI Assistant feature** — it dynamically compiles and executes C# code returned from a remote LLM endpoint. This is a hard blocker for App Store review (arbitrary code execution, sandbox bypass, network dependency on a personal ngrok tunnel).
2. **Add Revit 2024 support** — current build targets only `net8.0-windows`, which means Revit 2025+. Revit 2024 needs a separate `net48` target.
3. **Replace placeholder GUIDs, URLs, and credentials** committed to source.
4. **Strip the redundant `Type="Command"` entries** from the .addin manifest — they create duplicate entries under External Tools, which Autodesk disallows for ribbon-based apps.
5. **Build out the entire bundle/PackageContents/EULA/help layer** — none of it exists today.
6. **Stop bundling Newtonsoft.Json** with the addin — it conflicts with Revit's bundled copy.

Several questions need your decision before I move past the audit (see **Open questions** at the end).

---

## 1. Project structure

### Source layout
```
/
├── App.cs                         # IExternalApplication (ribbon)
├── SyncCommand.cs                 # Upload to BINA (full upload flow, not browser open)
├── LoginCommand.cs                # Open login window
├── BimDisciplineCommand.cs        # Download discipline files
├── FederateDisciplinesCommand.cs  # Link disciplines (UI hidden)
├── BinaApiService.cs              # BINA Cloud REST client
├── AutodeskApiService.cs          # Autodesk APS client (OAuth, OSS upload)
├── BinaConfig.cs                  # %APPDATA% config persistence
├── CredentialsDialog.cs / *Window.xaml(.cs)   # WPF UI
├── Commands/OpenAssistantCommand.cs           # AI assistant launcher
├── Handlers/CodeExecutionHandler.cs           # Revit ExternalEvent for AI codegen
├── Services/AIService.cs                      # Calls LLM backend
├── Services/CodeExecutor.cs                   # Roslyn compile + execute (!)
├── Models/AIRequest.cs, AIResponse.cs
├── Resources/{revitSync, revitSave, microchip}.png
├── RevitWebAppSync.csproj
├── RevitWebAppSync.addin
├── revit-addin-sync.sln
├── App.config                     # Leftover .NET Framework template
└── README.md
```

### .csproj
- **TargetFramework:** `net8.0-windows` (single target)
- **OutputType:** `Library`
- **AssemblyName:** `RevitWebAppSync`
- **PlatformTarget:** `x64`
- **UseWPF:** true
- **CopyLocalLockFileAssemblies:** true → copies *all* NuGet deps to output
- **RevitPath / RevitVersion:** parameterized via MSBuild properties; defaults to `D:\Autodesk\Revit2026\Revit 2026` and `2026` (developer machine path)
- **Post-build target:** copies the .addin and every output DLL to `%APPDATA%\Autodesk\Revit\Addins\{RevitVersion}\` if that folder exists. Useful for local dev, irrelevant for the bundle.

### .sln
- References two projects: `RevitWebAppSync.csproj` (exists) and `RevitWebAppSyncMinimal.csproj` (**does not exist on disk**, only `obj/` artifacts remain). The solution will fail to load cleanly. Recommend deleting the stale reference.

### App.config
- This file is essentially a leftover .NET Framework template. It contains:
  - `<startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup>` — meaningless for a `net8.0-windows` library.
  - Placeholder values committed to source: `your-aps-client-id-here`, `your-aps-client-secret-here`, `your-web-app-api-key`, `WebApp_BaseUrl=https://your-webapp.com/api`. None are read by current code.
  - Many TODO comments.
- **Action:** delete or rewrite. App.config is not the right config mechanism for a .NET 8 Revit addin anyway.

---

## 2. UI / Ribbon

`App.cs` implements `IExternalApplication` and creates one tab + one panel:

| Tab    | Panel       | Buttons (in order)                                                                          |
|--------|-------------|---------------------------------------------------------------------------------------------|
| `Sync` | `Sync Tools`| Sync to BINA, Login, Download BIM Disciplines, AI Assistant (+ Federate Disciplines hidden) |

- Buttons use 16/32 PNG embedded resources (`revitSync.png`, `revitSave.png`, `microchip.png`).
- Reuses the same icons across multiple buttons (Sync + Federate share `revitSave`; Sync + Discipline share `revitSync`).
- `LoadImage()` returns `null` on failure — the Revit ribbon will silently render a blank button rather than fail. OK for now.
- **No `SetContextualHelp()` calls** anywhere — required by App Store guidelines.
- `App` also creates an `ExternalEvent` and `CodeExecutionHandler` at startup — needed only for the AI feature.

For the App Store version, tab name should change from `Sync` to `BINA` per Step 5 of the prompt.

---

## 3. IExternalCommand classes

| Class                                                | Purpose                                                                            | Notes                                                                                                |
|------------------------------------------------------|------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| `RevitWebAppSync.SyncCommand`                        | Full upload flow: discipline picker → ensure logged in → APS upload → BINA cloud   | Despite the App.cs tooltip saying "Opens BINA Cloud in your default browser", it actually uploads.   |
| `RevitWebAppSync.LoginCommand`                       | Opens `LoginWindow` for BINA credentials                                           |                                                                                                      |
| `RevitWebAppSync.BimDisciplineCommand`               | Downloads Architecture/Structure/HVAC/Electrical files from BINA                   |                                                                                                      |
| `RevitWebAppSync.FederateDisciplinesCommand`         | Links downloaded discipline files into current doc                                 | Defined but **not added to ribbon** in `App.cs:113`.                                                 |
| `RevitWebAppSync.Commands.OpenAssistantCommand`      | Opens the AI Assistant WPF window                                                  | See AI section below — blocker.                                                                      |

All commands use `[Transaction(TransactionMode.Manual)]`.

---

## 4. .addin manifest

Single file: `RevitWebAppSync.addin`. Contains:

- 1× `<AddIn Type="Application">` for `RevitWebAppSync.App` — correct, this drives the ribbon.
- 3× `<AddIn Type="Command">` for `SyncCommand`, `LoginCommand` (NOT present, see below), `BimDisciplineCommand` — **these are redundant and harmful**. Because the ribbon already exposes them, the `Type="Command"` entries also surface them under **Add-Ins → External Tools**, which Autodesk's review explicitly flags. Per Step 5 of the prompt and Autodesk's published guidance, ribbon-based apps should not also register external-tool commands.
  - (Correction: actually present are `SyncCommand`, `BimDisciplineCommand`, and a commented-out `FederateDisciplinesCommand`. Login is *not* registered as a Command, only via ribbon.)
- All `<AddInId>` GUIDs are template placeholders (`12345678-1234-1234-1234-123456789ABC`, `87654321-4321-4321-4321-CBA987654321`, `11111111-2222-3333-4444-555555555555`). **These will collide with any other addin built from the same template** and must be regenerated.
- `<VendorId>` = `BINA_CLOUD` (the prompt asks for `BINA`).
- `<VendorDescription>` = `Bina Cloud, app.bina.cloud` (prompt expects publisher legal name `BINA CLOUDTECH SDN BHD`).
- No per-Revit-version manifest — there's only one file, today copied into `%APPDATA%\Autodesk\Revit\Addins\{year}\` by the post-build step.

---

## 5. Icon / resource files

| File                          | Dimensions  | Format                  | Used for                                  |
|-------------------------------|-------------|-------------------------|-------------------------------------------|
| `Resources/revitSync.png`     | 512×512     | 4-bit colormap PNG      | Sync to BINA, Download BIM Disciplines    |
| `Resources/revitSave.png`     | 360×360     | 4-bit colormap PNG      | Login, Federate Disciplines (hidden)      |
| `Resources/microchip.png`     | 512×512     | 8-bit RGBA PNG          | AI Assistant                              |

- Icons are embedded resources; ribbon downscales via `BitmapImage.DecodePixelWidth/Height` to 16/32. Works but is wasteful and produces fuzzy results vs. proper 16/32 source PNGs per Autodesk's icon guidelines.
- **No 16×16 or 32×32 source files exist.** App Store reviewers want crisp icons at the actual displayed size.
- No company / app icon for App Store listing (typically 64×64 + various store sizes).
- Need to verify all icons are owned/licensed by BINA — `microchip.png` looks generic, must confirm provenance.

---

## 6. Multi-version build strategy

**There isn't one.** Today: single `net8.0-windows` build, one `RevitVersion` MSBuild property defaulting to `2026`, one .addin.

Implications:

- **Revit 2026** — works (`net8.0-windows`).
- **Revit 2025** — works (`net8.0-windows`, Revit 2025 is the first .NET 8 release).
- **Revit 2024** — **does not work**. Revit 2024 runs on .NET Framework 4.8. Need a `net48` target with a separate API reference set (`RevitAPI.dll` from the 2024 install) and likely a separate output assembly to avoid runtime conflicts.

Recommended approach (simplest): convert `RevitWebAppSync.csproj` to `<TargetFrameworks>net48;net8.0-windows</TargetFrameworks>` with conditional `RevitPath` per target, and a small shim for any `net48`-incompatible code. The build script then produces three Contents/{2024,2025,2026} folders. The 2025 and 2026 outputs are identical .NET 8 binaries with different .addin GUIDs.

Alternative: three separate .csproj files. More boilerplate, but clearer separation if the API surfaces diverge.

I noticed the AI feature uses `System.Runtime.Loader.AssemblyLoadContext` (`CodeExecutor.cs:11`) which doesn't exist on .NET Framework 4.8 — yet another reason to drop the AI feature for the App Store build (or `#if NET8_0_OR_GREATER` it).

---

## 7. Dependencies

From `RevitWebAppSync.csproj`:

| Package                          | Version | Concern                                                                                                                           |
|----------------------------------|---------|-----------------------------------------------------------------------------------------------------------------------------------|
| `Microsoft.CodeAnalysis.CSharp`  | 4.8.0   | **Drag** — pulls ~25 MB of Roslyn DLLs into the addin folder. Only used by the AI code-execution feature. Drop with the AI feature. |
| `Newtonsoft.Json`                | 13.0.3  | **Conflict risk** — Revit 2024/2025/2026 ship Newtonsoft.Json themselves. Bundling our own copy can cause assembly-load conflicts when another addin also loads a different version. Set `<Private>False</Private>` and rely on Revit's shipped copy, or migrate to `System.Text.Json` for our own code. |
| Revit API (`RevitAPI`, `RevitAPIUI`) | local refs | Already correctly marked `Private=False`.                                                                                     |

Combined with `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>`, the current build copies the whole transitive graph into the addin folder. For an App Store submission this needs to be tightened.

---

## 8. Network / configuration code

- `BinaApiService` and `AutodeskApiService` make real HTTP calls. I haven't read every method, but at a glance both use `HttpClient`. Need to verify all calls have explicit timeouts and surface user-friendly errors.
- `Services/AIService.cs:14` hardcodes a public ngrok URL: `https://632012de7dc1.ngrok-free.app`. Ngrok URLs rotate; this is dev infrastructure, not production. **Must not ship.**
- `BinaConfig.cs` persists `Email` and `Password` *in plaintext* to `%APPDATA%\RevitWebAppSync\config.json`. This will:
  1. fail App Store security review;
  2. expose users' passwords if their machine is compromised.
  Should switch to refresh-token-only persistence with DPAPI (`ProtectedData.Protect`) at minimum.
- `App.config` references `APS_CLIENT_SECRET`. Embedding an OAuth client secret in a desktop app is anti-pattern (anyone can extract it). If we use APS, it should be PKCE flow with no secret, or a backend-mediated token exchange.

---

## 9. AI Assistant feature — **App Store blocker**

`Services/CodeExecutor.cs` does this:

1. Send a natural-language prompt to a remote LLM endpoint (`AIService`).
2. Receive C# source code in the response.
3. **Compile it at runtime** with Roslyn.
4. **Load the resulting assembly** into the Revit process (`AssemblyLoadContext`).
5. **Reflect to find a method, instantiate it, invoke it** with handles to the live Revit `Document`, `UIDocument`, and `View`.

This will not pass App Store review. It is, by definition, arbitrary remote code execution against the user's Revit document, gated only by trust in our backend. Reviewers test exactly this kind of thing, and Autodesk's policy explicitly prohibits it.

**Recommended path for App Store:**

- Conditionally compile the AI feature out of the App Store build (`#if !APP_STORE`) or split into a separate non-Store distribution.
- Or, redesign as a curated set of pre-built operations (`HideAllFurniture`, `CountDoorsOnLevel`, etc.) where the LLM picks one of N parameterized actions but never delivers code.

This is the single biggest decision blocking submission — please confirm direction (see Open questions below).

---

## 10. What's missing entirely (vs. App Store requirements)

- [ ] `BinaConnector.bundle/` directory layout
- [ ] `PackageContents.xml`
- [ ] Per-version `.addin` files with unique GUIDs
- [ ] HTML help page (`Contents/Resources/help/index.html`)
- [ ] EULA HTML + first-run acceptance dialog + persistence
- [ ] `SetContextualHelp()` wired to local help on every ribbon item
- [ ] Properly sized (16/32) icon source files
- [ ] App Store listing artwork (publisher logo, 64×64 product icon, marketing screenshots)
- [ ] Build script that produces `BinaConnector.bundle.zip`
- [ ] Pre-submission smoke-test checklist
- [ ] Network-down resilience verification

---

## 11. Other observations

- `bin/` is checked into the repo (per `git status`-clean state and the `ls` listing). Build output should not be tracked. Recommend `.gitignore` for `bin/` and `obj/`.
- `README.md` documents APS_CLIENT_ID/SECRET in App.config — outdated guidance; rewrite for App Store distribution model (no manual config required).
- The Federate Disciplines command is fully implemented (28 KB) but hidden from the ribbon. Either ship it or delete it — leaving dead UI behind invites questions during review.
- Empty `catch` blocks in `BinaConfig.Load`/`Save` (`BinaConfig.cs:38, 58`) silently swallow errors. Should at least log.

---

## Open questions — need your call before I proceed

1. **AI Assistant** — for the App Store build, do we (a) cut the feature entirely, (b) ship a curated/parameterized version that does not execute remote code, or (c) keep AI in a parallel non-Store build only? My recommendation: **(c)** — keep the existing repo as the internal dev build, produce a slimmed-down App Store build via a build flag.
2. **Naming** — rename namespace + assembly from `RevitWebAppSync` → `BinaConnector` everywhere, or keep internal name and only brand the output bundle as `BinaConnector`? Renaming is cleaner long-term but touches every file.
3. **Revit 2024 support** — confirm we want it. It requires a `net48` target alongside `net8.0-windows`, plus dropping the AI feature on the 2024 build (Roslyn AssemblyLoadContext is .NET Core only). If 2024 isn't critical for launch, dropping it cuts significant work.
4. **Sync to BINA tooltip mismatch** — current tooltip says "Opens BINA Cloud" but the command actually uploads. Want me to fix the tooltip as part of this work?
5. **Other commands** — keep all four ribbon buttons (Sync, Login, Download Disciplines, AI) in the App Store build, or trim to just Upload + Sign In + Settings as Step 5 of the prompt suggests?
6. **Icon source** — do you have branded 16/32 PNGs to swap in, or should I generate placeholders and flag them for replacement?
7. **Support URL / email** — the prompt mentions `binacloud.com.my`. Confirm the support email and whether the URL should be `binacloud.com.my` or `app.bina.cloud` (used elsewhere in the repo).

---

**Status:** Awaiting your direction on the open questions before starting Step 2 (bundle structure).
