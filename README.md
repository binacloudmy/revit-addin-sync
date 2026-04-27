# BINA Platform Connector for Autodesk Revit

Free connector that uploads Revit models to BINA Cloud (BIMCloudX), packaged
for the Autodesk App Store under publisher **BINA CLOUDTECH SDN BHD**.

Supported Revit versions: **2025, 2026** (Windows x64). Revit 2024 support is
deferred to v1.1 — see `docs/pre-submission-checklist.md` Section K.

## Repository layout

```
.
├── App.cs, Commands/, *.xaml(.cs), *Service.cs, ...   # Source code
├── Resources/                                          # Embedded ribbon icons (placeholder)
├── BinaConnector.csproj                                # Multi-target net48 + net8.0-windows
├── BinaConnector.sln
├── bundle-templates/                                   # Static bundle assets (manifests, EULA, help, icons)
├── build-bundle.ps1                                    # Builds + assembles BinaConnector.bundle.zip
└── docs/
    ├── app-store-audit.md                              # Pre-refactor audit report
    └── pre-submission-checklist.md                     # Walk through this before every submission
```

The build script produces `BinaConnector.bundle/` and `BinaConnector.bundle.zip`
at the repo root. Both are gitignored.

## Building (Windows)

Requirements:
- Windows 10/11 x64
- .NET SDK 8.x
- A local install of Revit 2026 (used only for its `RevitAPI.dll` reference
  assemblies). Override the path via the `RevitPath2026` environment variable
  if installed in a non-default location.

```powershell
pwsh ./build-bundle.ps1
```

The script:
1. Builds `net8.0-windows` (used by both Revit 2025 and Revit 2026 — same
   binary, different per-version `.addin` manifest).
2. Assembles `BinaConnector.bundle/` from build outputs + `bundle-templates/`.
3. Validates structure and warns on unfilled `[PLACEHOLDER]` strings.
4. Zips to `BinaConnector.bundle.zip` ready for App Store submission.

For local dev (copy DLL to `%APPDATA%\Autodesk\Revit\Addins\<year>\` so Revit
loads it on next launch):

```powershell
dotnet build BinaConnector.csproj -c Debug -f net8.0-windows /p:DeployToRevit=true /p:DeployRevitYear=2026
```

## Submitting to the App Store

1. Walk through every item in [`docs/pre-submission-checklist.md`](docs/pre-submission-checklist.md).
2. Replace placeholders (production API URL, support contact, branded icons,
   legal-reviewed EULA).
3. Upload `BinaConnector.bundle.zip` at
   <https://aps.autodesk.com/app-store/publisher-center/revit>.
4. Autodesk's ADN team builds the final MSI from the bundle.

Do **not** build an MSI yourself. Do **not** modify Revit support paths.

## Configuration

User-facing config lives under `%APPDATA%\BINA\BinaConnector\`:

| File                  | Contents                                                      |
|-----------------------|---------------------------------------------------------------|
| `config.json`         | Persisted session (userId, projectId, DPAPI-encrypted refresh token). No password, no plaintext access token. |
| `settings.json`       | User preferences (default discipline, confirm-before-upload). |
| `eula-accepted.json`  | EULA acceptance record (version + timestamp).                 |
| `logs/`               | API request logs (`bina_api.log`, `autodesk_api.log`, `startup.log`). |

API endpoint can be overridden at runtime via the `BINA_API_BASE_URL` and
`BINA_WEB_APP_URL` environment variables, useful for staging/dev backends.

## Architecture

- `App.cs` — `IExternalApplication`. Creates the **BINA** ribbon tab with three
  buttons. Defensive try/catch — addin never returns `Result.Failed` to Revit.
- `Commands/UploadCommand.cs` — Upload to BINA. Gates on EULA acceptance,
  sign-in status, and (optional) per-upload confirmation.
- `Commands/ProjectSettingsCommand.cs` — Active project + upload preferences.
- `Commands/AccountCommand.cs` — Sign in / view account / sign out.
- `BinaApiService.cs` / `AutodeskApiService.cs` — Backend HTTP clients.
- `BinaConfig.cs` — Session persistence with DPAPI-encrypted refresh tokens.
- `EulaService.cs`, `EulaWindow.xaml(.cs)` — First-run EULA gate.
- `NetworkErrors.cs` — Network exceptions → user-friendly messages.

## Known limitations

- **Revit 2024 support is deferred.** Building net48 + WPF on a CLI-only
  Windows box (no VS Build Tools) hits a known WPF MarkupCompilePass1 issue
  against net48 Facade assemblies. Add it back in v1.1 with VS Build Tools
  installed — see checklist Section K for the recipe and the reserved GUID.
- **Token refresh is not yet wired up.** When the in-memory access token
  expires (or after a Revit restart), users are re-prompted for password.
  Implementing silent refresh requires a backend refresh endpoint.
- **Icons are placeholders.** Replace `Resources/*.png` and
  `bundle-templates/icons/*.png` with branded artwork before submission.
