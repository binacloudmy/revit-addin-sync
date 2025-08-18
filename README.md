# Revit Web App Sync Add-in

A comprehensive Revit add-in that enables users to sync their Revit files to a web application through Autodesk APS (Platform Services) integration. The add-in handles OAuth authentication, file metadata extraction, intelligent project routing, and seamless file uploads to cloud storage.

## Features

- **OAuth 2.0 Authentication**: Secure authentication with Autodesk Platform Services
- **File Metadata Extraction**: Comprehensive extraction of Revit file information
- **Intelligent Project Routing**: Auto-detection and manual selection of target projects
- **Cloud Storage Integration**: Upload files to Autodesk Object Storage Service (OSS)
- **Progress Tracking**: Real-time progress monitoring with detailed logging
- **Change Detection**: File hash-based change detection to avoid unnecessary uploads
- **User-Friendly UI**: Modern WPF dialogs for project selection and progress tracking

## Prerequisites

### Development Environment

1. **Visual Studio 2022** (recommended) or Visual Studio Code
2. **.NET 8 SDK** (for Revit 2025+) or **.NET Framework 4.8** (for earlier versions)
3. **Revit 2025** (or your target Revit version)
4. **Git** for version control

### Autodesk Platform Services (APS) Setup

1. **APS Application**: Create an application at [APS Developer Portal](https://aps.autodesk.com/)
   - Note your Client ID and Client Secret
   - Configure redirect URI (e.g., `http://localhost:8080/callback`)
   - Enable required scopes: `data:read`, `data:write`, `data:create`, `bucket:read`, `bucket:create`

2. **Web Application API**: Ensure your web application has:
   - RESTful API endpoints for project management
   - API authentication (API key or OAuth)
   - File metadata storage capabilities

### NuGet Packages

The following packages are required (specified in the .csproj file):

```xml
<!-- Autodesk Platform Services SDK -->
<PackageReference Include="Autodesk.SDKManager" Version="1.0.0" />
<PackageReference Include="Autodesk.Authentication" Version="2.0.0" />
<PackageReference Include="Autodesk.OSS" Version="2.0.0" />

<!-- HTTP and JSON -->
<PackageReference Include="System.Net.Http" Version="4.3.4" />
<PackageReference Include="System.Text.Json" Version="8.0.0" />

<!-- Configuration -->
<PackageReference Include="System.Configuration.ConfigurationManager" Version="8.0.0" />
```

### Optional Packages

For enhanced OAuth experience, consider adding:

```xml
<!-- Browser Control for OAuth (choose one) -->
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.2210.55" />
<!-- OR -->
<PackageReference Include="CefSharp.WinForms" Version="118.6.80" />

<!-- Logging Framework (choose one) -->
<PackageReference Include="NLog" Version="5.2.0" />
<!-- OR -->
<PackageReference Include="Serilog" Version="3.1.0" />
```

## Installation and Setup

### 1. Clone and Build

```bash
git clone <your-repository-url>
cd RevitWebAppSync
dotnet build --configuration Release --platform x64
```

### 2. Configuration

Update the configuration in one of these locations (in order of priority):

#### Option A: Environment Variables (Recommended for sensitive data)
```bash
set APS_CLIENT_ID=your-aps-client-id
set APS_CLIENT_SECRET=your-aps-client-secret
set WebApp_BaseUrl=https://your-webapp.com/api
set WebApp_ApiKey=your-api-key
```

#### Option B: JSON Configuration File
Create `config.json` in the add-in directory:

```json
{
  "APS_CLIENT_ID": "your-aps-client-id",
  "APS_CLIENT_SECRET": "your-aps-client-secret",
  "APS_REDIRECT_URI": "http://localhost:8080/callback",
  "WebApp_BaseUrl": "https://your-webapp.com/api",
  "WebApp_ApiKey": "your-api-key",
  "OSS_BucketKey": "revit-sync-your-company",
  "ExportFormat": "RVT",
  "MaxFileSizeMB": "100"
}
```

#### Option C: App.config File
Update values in `App.config` (least secure, not recommended for production).

### 3. Deploy Add-in

The build process automatically copies files to:
```
%APPDATA%\Autodesk\Revit\Addins\2025\
```

Manual deployment:
1. Copy `RevitWebAppSync.dll` to the Revit Add-ins folder
2. Copy `RevitWebAppSync.addin` to the same folder
3. Copy any dependencies if required

### 4. Generate GUIDs

Before first use, generate unique GUIDs for your add-in:

1. **Application GUID**: Replace the GUID in `RevitWebAppSync.addin` for the Application AddInId
2. **Command GUID**: Replace the GUID in `RevitWebAppSync.addin` for the Command AddInId

You can generate GUIDs using:
- Visual Studio: Tools → Create GUID
- Online: [GUID Generator](https://www.guidgenerator.com/)
- PowerShell: `[System.Guid]::NewGuid()`

## Project Structure

```
RevitWebAppSync/
├── RevitWebAppSync/
│   ├── App.cs                          # Main application class (IExternalApplication)
│   ├── SyncCommand.cs                  # Sync command (IExternalCommand)
│   ├── Services/
│   │   ├── AuthenticationService.cs    # APS OAuth authentication
│   │   ├── FileMetadataService.cs      # Revit file metadata extraction
│   │   ├── ApiService.cs               # Web application API communication
│   │   └── AutodeskOSSService.cs       # Object storage service integration
│   ├── Models/
│   │   ├── FileMetadata.cs             # File metadata data model
│   │   ├── ProjectInfo.cs              # Project information data model
│   │   └── AuthToken.cs                # Authentication token model
│   ├── UI/
│   │   ├── ProjectSelectionDialog.xaml # Project selection dialog
│   │   ├── ProjectSelectionDialog.xaml.cs
│   │   ├── ProgressDialog.xaml         # Progress tracking dialog
│   │   └── ProgressDialog.xaml.cs
│   ├── Utils/
│   │   ├── FileHashCalculator.cs       # File change detection utilities
│   │   └── ConfigManager.cs            # Configuration management
│   └── Resources/
│       ├── sync_icon_32x32.png         # Large icon for ribbon
│       └── sync_icon_16x16.png         # Small icon for ribbon
├── RevitWebAppSync.addin               # Add-in manifest file
└── README.md                           # This file
```

## Usage

1. **Open Revit** and load a project file
2. **Click "Sync to Web"** button in the "Web App Sync" ribbon tab
3. **Authenticate** with your Autodesk account (first time only)
4. **Select target project** from the list or let the system auto-detect
5. **Monitor progress** in the progress dialog
6. **Review results** and check your web application

## Configuration Options

### File Export Settings

- **ExportFormat**: `RVT`, `IFC`, `DWG`, `NWC`
- **MaxFileSizeMB**: Maximum file size limit
- **AutoSync**: Enable automatic sync on file save

### Performance Settings

- **EnableFileHash**: Use file hashing for change detection
- **HashMethod**: `SHA256` (secure), `Fast` (performance), `MetadataOnly` (minimal)
- **CacheTokens**: Cache authentication tokens

### UI Settings

- **ShowProgressDialog**: Display progress dialog during sync
- **AutoCloseProgress**: Automatically close progress dialog on completion
- **ConfirmBeforeSync**: Ask for confirmation before starting sync

## Development

### Required Implementations

Several components require implementation for your specific environment:

1. **OAuth Browser Integration** (`AuthenticationService.cs`)
   - Implement browser control for OAuth flow
   - Choose between WebView2, CefSharp, or system browser

2. **File Export Logic** (`FileMetadataService.cs`)
   - Implement IFC, DWG, or other format exports
   - Customize metadata extraction for your needs

3. **Web API Integration** (`ApiService.cs`)
   - Implement your web application's specific API endpoints
   - Customize data structures for your project model

### Debugging

1. Set Visual Studio to start Revit for debugging:
   - Project Properties → Debug → Start external program
   - Path: `C:\Program Files\Autodesk\Revit 2025\Revit.exe`

2. Attach debugger to running Revit process
3. Use debug configuration with detailed logging

### Testing

1. **Unit Tests**: Create tests for utility functions and data models
2. **Integration Tests**: Test APS API integration with test credentials
3. **Manual Testing**: Test with various Revit file types and sizes

## Security Considerations

1. **Credential Storage**: Use environment variables or secure vaults for production
2. **API Keys**: Never commit sensitive credentials to version control
3. **Token Caching**: Consider encrypting cached authentication tokens
4. **Network Security**: Use HTTPS for all API communications
5. **File Access**: Validate file paths and implement proper error handling

## Troubleshooting

### Common Issues

1. **Add-in Not Loading**
   - Check Revit Add-in Manager for error messages
   - Verify .addin file is in correct location
   - Ensure all dependencies are available

2. **Authentication Failures**
   - Verify APS credentials are correct
   - Check redirect URI configuration
   - Confirm network connectivity

3. **File Upload Issues**
   - Check file size limits
   - Verify OSS bucket permissions
   - Review network and proxy settings

### Logging

Enable detailed logging in configuration:
```xml
<add key="LogLevel" value="Debug" />
<add key="VerboseLogging" value="true" />
```

Log files are typically saved to:
```
%TEMP%\RevitWebAppSync\logs\
```

## API Integration

### Web Application Requirements

Your web application should provide these API endpoints:

```
GET /api/projects                    # List available projects
POST /api/projects/detect           # Auto-detect project from metadata
GET /api/projects/{id}/sync-history # Get sync history
POST /api/files/check-upload        # Check if upload is needed
POST /api/files/update              # Update file information
POST /api/sync/report               # Report sync status
```

### Data Models

Key data structures your API should support:

- **Project Information**: ID, name, number, client, status
- **File Metadata**: Revit-specific information and statistics
- **Sync History**: Track sync operations and changes

## Contributing

1. Fork the repository
2. Create a feature branch
3. Implement your changes with appropriate comments
4. Add tests for new functionality
5. Update documentation
6. Submit a pull request

## License

[Specify your license here]

## Support

For support and questions:
- Create an issue in the repository
- Contact: [your-support-email]
- Documentation: [your-documentation-url]

---

**Note**: This is a template implementation. You'll need to customize the code for your specific web application, implement OAuth browser integration, and complete the TODO items throughout the codebase.