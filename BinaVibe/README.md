# BinaVibe

C# modules behind the Revit copilot pane. The copilot talks to the backend over
`/copilot/turns` (HTTP + SSE); the nine tools it drives are executed here and
their measured results are reported back.

## Layout

- **Copilot/** - the lean tool harness: `Contracts/` (wire DTOs and envelopes),
  `Read/` (find, inspect, overview, look, snapshot), `Check/` (rule sets and
  compliance checks), `Writes/` (set_parameters engine, commit ledger, the
  Revit-thread marshal), `Undo/`, `Code/` (run_revit_code), `Guard/`,
  `Drafting/`, `Tools/`. Add coverage with playbooks and rule sets on the
  backend, not by adding tools.
- **Mcp/Tools/** - what is left of the old tool layer, used by the copilot and
  the pane:
  - `Inspectors` - the read helpers the copilot readers call (levels, grids,
    rooms, views, sheets, worksets, phases, family types, warnings, project
    info, CAD links, element selection, parameter-name suggestions).
  - `TxGuard` - transaction wrapper that tolerates Revit failures.
  - `ArgsHelp`, `CategoryResolve`, `BulkEdit` (category + level targets),
    `Schedules` (name lookup).
  - `DwgReader`, `DwgScratchCache`, `CadFileReader` and `PdfReader`,
    `PdfAttachmentCache` - read attached DWG and PDF files.
  - `PaneTools` - the handful of reads and actions the pane runs by direct call
    on Revit's thread.
- **DocState/OperationLedger** - idempotency ledger behind the copilot commit
  ledger.
- **Policy/VibeFlags** - local tenant config from `%APPDATA%\RevitWebAppSync\vibe.json`.
- **Auth/** - OAuth and password sign-in, secure token store.
- **WarmupHandler** - warms the Revit API on the first document open.

The MCP-era tool surface (a 176-arm `ToolRegistry`, the `Mutators`, the
`DesignSpec`/`Audit`/`Cad*FromAttachment` tools and their plan modules) was
deleted; it was never reachable from the model path.
