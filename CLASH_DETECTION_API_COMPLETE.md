# Clash Detection - API Upload Complete

## Summary
The clash detection feature now includes complete server integration with local report backup.

## What Gets Sent to Server

When a clash detection is run, the following data is uploaded to your server endpoint:

### Report Metadata
- `reportId`: Unique identifier (e.g., "RPT-20251201-abc123de")
- `timestamp`: UTC timestamp when report was generated
- `generatedByVersion`: Plugin version (e.g., "1.0.0")
- `runByUser`: Windows username who ran the detection
- `uploadedBy`: Windows username who uploaded the report
- `uploadedAt`: UTC timestamp of upload
- `machineName`: Computer name where detection was run

### Project Information
- `projectInfo.id`: Project ID (if available)
- `projectInfo.name`: Project name from Revit
- `projectInfo.number`: Project number
- `projectInfo.clientName`: Client name
- `projectInfo.address`: Project address

### Files Involved
- `filesInvolved`: Array of file paths
  - Index 0: Current Revit model path
  - Index 1+: External file paths used in detection

### Element Selection Sets
**Set A (typically current model):**
- `setA.selectedCategories`: Array of category names (e.g., ["Walls", "Floors"])
- `setA.totalElementCount`: Number of elements in set
- `setA.selectionType`: Type of selection ("Category", "CurrentSelection", etc.)

**Set B (typically external files):**
- `setB.selectedCategories`: Array of category names (e.g., ["Ducts", "Pipes"])
- `setB.totalElementCount`: Number of elements in set
- `setB.selectionType`: Type of selection

### Detection Settings
- `toleranceUsed`: Clash tolerance in model units (mm)
- `clashTypesChecked`: Array of clash types checked (["Hard", "Clearance"])

### Results Summary
- `totalClashCount`: Total number of clashes found
- `criticalClashCount`: Number of critical severity clashes
- `warningClashCount`: Number of warning severity clashes
- `infoClashCount`: Number of info severity clashes

### Detailed Clash Results
For each clash detected:
- `clashId`: Unique identifier for the clash
- `elementId1`: Revit element ID from set A
- `elementId2`: Revit element ID from set B
- `category1`: Category of element 1 (e.g., "Walls")
- `category2`: Category of element 2 (e.g., "Ducts")
- `clashType`: "Hard" or "Clearance"
- `severity`: "Critical", "Major", or "Minor"
- `clashPoint`: 3D coordinates of clash location
  - `x`, `y`, `z`: Coordinates in model units
- `overlapVolume`: Volume of intersection (cubic units)
- `distance`: Distance between elements (0 for hard clashes)
- `description`: Human-readable description

### Statistics
- `clashStatistics`: Dictionary of clash counts by category pairs
  - Key: "Category1 vs Category2" (e.g., "Walls vs Ducts")
  - Value: Number of clashes

### Performance Metrics
- `executionTimeSeconds`: Time taken to run detection
- `totalComparisons`: Number of element pairs compared
- Derived: `comparisonsPerSecond` (calculated from above)

## API Endpoint

**POST** `/clash-reports`

The endpoint should be configured in your app settings:
- Base URL: `WebApp_BaseUrl` (default: "https://your-webapp.com/api")
- API Key: `WebApp_ApiKey` (sent in `X-API-Key` header)

## Expected Response

The server should respond with:
```json
{
  "serverId": "unique-server-assigned-id",
  "success": true,
  "message": "Clash report received successfully"
}
```

## Implementation Details

### Files Created/Modified

1. **NEW: ClashReportService.cs** (`Services/ClashReportService.cs`)
   - Saves reports locally to `%USERPROFILE%\Documents\RevitClashReports\`
   - Creates both JSON and text summary files
   - Provides report management (list, load, delete, archive)
   - Exports to CSV for Excel analysis

2. **MODIFIED: ApiService.cs** (`Services/ApiService.cs`)
   - Added `UploadClashReportAsync()` method
   - Validates report before upload
   - Serializes all clash data to JSON
   - Sends to configured API endpoint
   - Returns success/failure result

3. **MODIFIED: ClashDetectionCommand.cs** (`ClashDetectionCommand.cs`)
   - Updated to use ProjectInfo model instead of simple project name
   - Integrated ClashReportService for local saves
   - Integrated ApiService for server uploads
   - Added proper error handling with user notifications
   - Operations continue even if save/upload fails

### Workflow

1. User runs clash detection through Revit UI
2. Clash detection runs and generates ClashReport object
3. **Local Save**: Report saved to Documents folder
   - JSON file: `RPT-{id}_{timestamp}.json`
   - Text file: `RPT-{id}_{timestamp}.txt`
4. **Server Upload**: Report sent to configured API endpoint
   - Validates report data
   - Serializes to JSON
   - Posts to `{baseUrl}/clash-reports`
5. User sees results dialog with:
   - Success: Both local and server save succeeded
   - Warning: Local save succeeded, server upload failed (shows local path)
   - Error: Both operations failed

### Error Handling

- **Local save fails**: Shows warning, continues with upload attempt
- **Upload fails**: Shows warning with local file path, user has backup
- **Both fail**: Shows error, clash detection results still displayed
- Network issues don't block clash detection workflow

### Configuration Required

Update these settings in your application configuration:

```xml
<add key="WebApp_BaseUrl" value="https://your-api-server.com/api" />
<add key="WebApp_ApiKey" value="your-api-key-here" />
```

Or use ConfigManager.GetSetting() to retrieve from your configuration source.

## Local Report Storage

Reports are automatically saved to:
```
C:\Users\{username}\Documents\RevitClashReports\
```

Each report creates two files:
- `RPT-{id}_{timestamp}.json` - Full JSON data
- `RPT-{id}_{timestamp}.txt` - Human-readable summary

### Report Management Features

```csharp
var reportService = new ClashReportService();

// Get all reports
var reports = reportService.GetAllReports();

// Get reports for a specific project
var projectReports = reportService.GetReportsByProject("Project ABC");

// Load a specific report
var report = reportService.LoadReport("path/to/report.json");

// Export to CSV
var csvPath = reportService.ExportToCSV(report);

// Archive old reports (older than 30 days)
reportService.ArchiveOldReports(30);

// Delete a report
reportService.DeleteReport("path/to/report.json");
```

## Next Steps

To complete the integration:

1. **Configure API endpoint** in your app settings
2. **Set up API key** for authentication
3. **Create server endpoint** to receive clash reports:
   - Endpoint: `POST /clash-reports`
   - Accepts JSON body with report data
   - Stores in database
   - Returns server ID confirmation
4. **Optional**: Implement report viewing in your web app
5. **Optional**: Add analytics/dashboards for clash trends

## Testing

To test the integration:

1. Run clash detection in Revit
2. Check `Documents\RevitClashReports` for local files
3. Verify API call in server logs
4. Confirm data appears in your database

## Security Considerations

- API key should be stored securely (not hardcoded)
- Use HTTPS for API endpoint
- Consider adding user authentication
- File paths in report may contain sensitive directory structures
- Element IDs can be used to locate elements in model
