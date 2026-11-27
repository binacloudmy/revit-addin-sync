# CAD-to-BIM: Revit Link Limitations & Alternatives

Analysis of what Revit's CAD link API can and cannot extract, and the
alternative paths now implemented.

## Status (2026-08)

**SOLVED:** ACadSharp integration is complete. For DWG **attachments**, the
agent can now read block names, text content, and attributes directly.

| Gap | Status | Tool |
|-----|--------|------|
| Block names | ✅ Solved (attachments) | `get_dwg_block_names` |
| Text content | ✅ Solved (attachments) | `get_dwg_texts` |
| Block attributes | ✅ Solved (attachments) | `get_dwg_block_names` |
| Model CAD | ❌ Still geometry-only | `get_dwg_blocks` (no names) |

For **model CAD** (linked/imported in Revit), the limitations below still
apply — Revit's API only exposes geometry, not the original DWG metadata.

## Revit link limitations by element type

| Element | What you need from CAD | Revit link gives | Gap |
|---------|----------------------|------------------|-----|
| **Walls** | Line pairs, layer | Lines + layer | None — working now |
| **Doors** | Block name, insertion point, rotation, width/height | Insertion + rotation | **Block name** (can't map `DR-900` → `Door 900mm`), **attributes** (width/height stored in block) |
| **Windows** | Block name, insertion, width/height/sill | Insertion + rotation | Same as doors |
| **Furniture** | Block name, insertion, rotation | Insertion + rotation | **Block name** (can't map `CHAIR-01` → family) |
| **Fixtures (WC, sink)** | Block name, insertion | Insertion + rotation | Same |
| **Rooms** | Boundary + room name text | Boundary lines | **Text content** — room name is curves, not string |
| **Dimensions** | Measurement values | Nothing usable | Just geometry — no numeric value |
| **Roof** | Outline + slope annotations | Outline lines | **Text** (slope angle), **hatch** (if roof shown as hatch pattern) |
| **Stairs** | Outline + riser count text | Lines | **Text** (riser count, width) |
| **Annotations** | All text labels | Nothing | Text = curves, not readable |
| **Hatches** | Pattern boundary | Boundary only | **Pattern name** (can't tell "concrete" vs "insulation") |
| **Columns** | Rectangle/circle, layer | Full geometry | Works if on own layer |
| **Grids** | Lines + grid label text | Lines | **Text** (grid names A, B, 1, 2) |

## The three critical gaps

### 1. Block names — doors/windows/furniture

```
CAD block "DR-900" at (5000, 3000), rotation 90°
                    │
        Revit link API sees:
                    │
        GeometryInstance at (5000, 3000), rotation 90°
        Symbol.Name = ??? (often generic, not "DR-900")
```

**Problem:** Can't reliably match `DR-900` → Revit family `Single Door 900mm`.

**Workarounds:**
- Layer (all doors on `A-DOOR`) — identifies class, not type
- Geometry size (measure the rectangle = width) — fragile
- Position in wall (host assignment) — doesn't help with type

**Result:** Door placement works, **door type selection = guesswork**.

### 2. Text content — rooms, grids, annotations

```
CAD: TEXT "LIVING ROOM" at (5000, 4000)
                    │
        Revit link API sees:
                    │
        List<Curve> (the letter outlines)
        No string value
```

**Problem:** Can't read room names, grid labels, dimension values, slope
annotations. All rendered as curves.

**Workaround:** OCR on rendered image. Fragile, adds latency.

### 3. Block attributes — metadata in doors/windows

```
CAD block "DOOR-01"
├── geometry (arc + rect)
├── ATTRIBUTE "WIDTH" = "900"
├── ATTRIBUTE "HEIGHT" = "2100"
└── ATTRIBUTE "FIRE_RATING" = "FD30"
```

Revit link: sees geometry only. **Attributes = invisible.**

## Element-by-element verdict (attachments vs model CAD)

| Element | Model CAD | Attachment (ACadSharp) |
|---------|-----------|------------------------|
| **Walls** | ✅ Full (cad_walls_to_centerlines) | ✅ Full |
| **Columns** | ✅ Full | ✅ Full |
| **Doors** | ⚠️ Position only | ✅ Full (block name + attributes) |
| **Windows** | ⚠️ Position only | ✅ Full |
| **Furniture** | ⚠️ Position only | ✅ Full (block name) |
| **Rooms** | ❌ No text | ✅ Full (get_dwg_texts) |
| **Grids** | ❌ No text | ✅ Full |
| **Annotations** | ❌ No text | ✅ Full |

**Recommendation:** For full CAD-to-BIM conversion, have users **attach** the
DWG in the Copilot pane rather than linking it in Revit first. The attachment
path reads the file directly via ACadSharp and extracts everything.

## Implementation (current)

### Architecture

```
User attaches DWG in Copilot pane
              │
              ▼
┌─────────────────────────────────────┐
│   Add-in: DwgScratchCache           │
│   ├── ACadSharp.Extract()           │  ← block names, text, attributes
│   └── Revit Link (ImportInstance)   │  ← geometry for placement
└─────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────┐
│   Agent tools                        │
│   ├── get_dwg_block_names           │  ← DR-900 → Door 900mm
│   ├── get_dwg_texts                 │  ← LIVING ROOM, A, B, 1, 2
│   └── cad_walls_to_centerlines      │  ← line pairs → walls
└─────────────────────────────────────┘
              │
              ▼
┌─────────────────────────────────────┐
│   Revit creation                     │
│   ├── Wall.Create (batch mode)      │
│   ├── Door.Create                   │
│   └── Room.Create                   │
└─────────────────────────────────────┘
```

### Key components

| Component | Location | Purpose |
|-----------|----------|---------|
| `CadFileReader.cs` | BinaVibe/Mcp/Tools/ | ACadSharp extraction |
| `DwgScratchCache.cs` | BinaVibe/Mcp/Tools/ | Caches ACadSharp + Revit data |
| `ToolRegistry.cs` | BinaVibe/Mcp/Tools/ | Tool dispatch |
| `tools.py` | bina-ai/.../copilot/ | Tool schemas |

### Tools

| Tool | What it does | Works on |
|------|-------------|----------|
| `get_dwg_block_names` | Block names + attributes | Attachments only |
| `get_dwg_texts` | Text content | Attachments only |
| `get_dwg_blocks` | Block positions (no names) | Model CAD + attachments |
| `get_dwg_summary` | Layer overview | Both |
| `cad_walls_to_centerlines` | Line pairs → walls | Model CAD |

### Source detection

ACadSharp detects AutoCAD Architecture / Civil 3D / MEP files by checking
for `AEC_*` / `AECC_*` / `AECB_*` custom classes. These are warned but not
blocked — AEC objects explode to geometry, losing semantic data.

## Alternative paths (reference)

| Feature | ACadSharp (add-in) | ezdxf (backend) | ODA SDK | APS |
|---------|-------------------|-----------------|---------|-----|
| **Format** | DWG + DXF | DXF only | DWG/DXF | DWG/DXF |
| **Block names** | ✅ | ✅ | ✅ | ✅ |
| **Text content** | ✅ | ✅ | ✅ | ✅ |
| **Attributes** | ✅ | ✅ | ✅ | ✅ |
| **AEC objects** | ❌ (exploded) | ❌ | ✅ (with ACA) | ✅ (with ACA) |
| **Cost** | Free (MIT) | Free (MIT) | $$$ | $$$ |
| **Where** | Add-in (C#) | Backend (Python) | Anywhere | Cloud |

**Current choice:** ACadSharp in add-in. Direct DWG read, no conversion,
no backend dependency. ezdxf backend exists but not wired — for future
API-only clients.

## Summary

**Attachments (via ACadSharp):** Full CAD data — block names, text, attributes.
Use `get_dwg_block_names` and `get_dwg_texts`.

**Model CAD (via Revit link):** Geometry only — lines, positions, layers.
Use `cad_walls_to_centerlines` for walls.

**Recommendation:** Have users attach DWGs in the Copilot pane for full
CAD-to-BIM conversion. The attachment path extracts everything.

## DWG engine: ODA Drawings.NET with an ACadSharp fallback (AI-647)

Detection reads DWG through `IDwgReader` (`Services/CadToBim/Dwg/`). **v1 ships on ACadSharp**: ODA
Drawings.NET 27.10 is in the code (Revit 2027 builds with HAS_ODA) but OFF by default, because
the in-process memory and time numbers failed the go/no-go table (AI-647-plan.md, "ODA status"). Either engine hands the
detector the same in-memory ACadSharp document, so the downstream code is identical.

- **Where ODA lives:** `%LocalAppData%\Bina\RevitSync\oda\27.10\` holds `bin\` (the runtime,
  with no DWF modules) and `oda.key` (the licence). An opt-in installer build puts them there. It is not
  the OTA `versions\` folder, so updates do not re-ship the runtime.
- **Config** (the `cadToBim` section of `%APPDATA%\RevitWebAppSync\config.json`):
  `dwgReader` is `acadsharp` (the default), `auto` (opt-in: ODA when installed) or `oda` (ODA or
  an error). A normal installer build ships no ODA runtime or key (`-OdaRuntimeDir` / `-OdaKeyFile`
  opt in). `odaDir` / `odaKeyFile` override the folder and the key.
- **Fallback:** ODA not installed means ACadSharp, silently, with identical output. ODA
  installed but failing (missing DLL, activation, another add-in's ODA) means ACadSharp plus one
  note (too little memory falls back silently, as if ODA were not installed): "Read with the built-in reader: ODA isn't set up correctly
  (…)". A failed ODA init is not retried until Revit restarts.
- **Default load context:** BinaLoader loads the plugin into Revit's default
  AssemblyLoadContext, so the `OdSwig_*` assemblies land there as well. If another add-in has
  already loaded its own ODA/Teigha wrappers, ours would bind to theirs. ODA init detects
  this and falls back with "another add-in already loaded ODA <version>".
- **Threads:** all ODA work runs on one dedicated thread, one read at a time.
- **Not covered by ODA:** `CadFileReader` (the copilot's attachment extract) stays on ACadSharp.
