# Revit Web App Sync Add-in

A Revit add-in that syncs Revit files to a web application through Autodesk APS integration.

## Prerequisites

- Visual Studio 2022
- .NET 8 SDK
- Revit (configurable via environment variables)

## Setup

### 1. Configure Environment Variables (Required for different PCs)

**Method 1: System Environment Variables (Recommended)**
1. Right-click "This PC" → Properties
2. Advanced System Settings → Environment Variables
3. Add new system variables:
   - Name: `RevitPath`, Value: `C:\Program Files\Autodesk\Revit 2025`
   - Name: `RevitVersion`, Value: `2025`

**Method 2: Command Line (Temporary)**
```cmd
set RevitPath=C:\Program Files\Autodesk\Revit 2025
set RevitVersion=2025
```

**Default Values:**
- `RevitPath`: `D:\Autodesk\Revit2026\Revit 2026`
- `RevitVersion`: `2026`

### 2. Build and Deploy

```bash
git clone <your-repository-url>
cd RevitWebAppSync
dotnet build --configuration Release --platform x64
```

Files are automatically copied to: `%APPDATA%\Autodesk\Revit\Addins\{RevitVersion}\`

### 3. Configuration

Configure your APS credentials and web app settings in `App.config` or environment variables:

```xml
<add key="APS_CLIENT_ID" value="your-client-id" />
<add key="APS_CLIENT_SECRET" value="your-client-secret" />
<add key="WebApp_BaseUrl" value="https://your-webapp.com/api" />
<add key="WebApp_ApiKey" value="your-api-key" />
```

## Usage

1. Open Revit and load a project file
2. Click "Sync to Web" button in the ribbon
3. Authenticate with Autodesk account (first time)
4. Select target project and monitor progress

## Development

Set Visual Studio debugger to start Revit:
- Path: `{RevitPath}\Revit.exe`
- Example: `C:\Program Files\Autodesk\Revit 2025\Revit.exe`

## Troubleshooting

- Check Revit Add-in Manager for errors
- Verify environment variables are set correctly
- Ensure all dependencies are available
- Check logs in `%TEMP%\RevitWebAppSync\logs\`