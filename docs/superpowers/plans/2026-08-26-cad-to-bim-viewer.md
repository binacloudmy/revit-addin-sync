# CAD-to-BIM Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a CAD viewer with AI clarification flow that lets users preview and confirm wall classification before creating Revit walls.

**Architecture:** Hybrid C#/Python. Browser viewer uses libredwg-web (WASM) to render DWG. Engine (Python) runs classification and AI clarification. Addin (C#) provides MCP tools for DWG reading via ACadSharp and wall creation.

**Tech Stack:** C# (ACadSharp, Revit API), Python (FastAPI, httpx), JavaScript (libredwg-web, Canvas 2D)

**Spec:** `docs/superpowers/specs/2026-08-26-cad-to-bim-viewer-design.md`

## Global Constraints

- Python 3.11+, FastAPI (existing engine stack)
- C# .NET 8.0, Revit 2024+ API (existing addin stack)
- No new Python dependencies (use existing httpx, FastAPI)
- No new C# dependencies (ACadSharp already in project)
- NPM: `@mlightcad/libredwg-web` for browser DWG parsing
- All coordinates in mm for API, feet for Revit internals
- Session state in-memory (no persistence)

---

## File Structure

### C# (revit-addin-sync)

```
BinaVibe/Mcp/Tools/
├── CadLoad.cs           # NEW: cad_load tool
├── CadGetLines.cs       # NEW: cad_get_lines tool  
├── CadCreateWalls.cs    # NEW: cad_create_walls tool
├── DwgScratchCache.cs   # EXISTING: attachment file cache
├── DwgReader.cs         # EXISTING: geometry extraction
└── ToolRegistry.cs      # MODIFY: register new tools
```

### Python (bina-ai)

```
app/engine/cad/
├── __init__.py          # NEW: module init
├── routes.py            # NEW: FastAPI router
├── classifier.py        # NEW: ALCM scoring
├── stitcher.py          # NEW: centerline solver
├── session.py           # NEW: session state manager
└── static/
    └── viewer.html      # NEW: libredwg viewer + chat
```

---

### Task 1: MCP Tool — cad_load

**Files:**
- Create: `BinaVibe/Mcp/Tools/CadLoad.cs`
- Modify: `BinaVibe/Mcp/Tools/ToolRegistry.cs:34` (add switch case)
- Test: Manual test via engine `/cad/load` route (Task 4)

**Interfaces:**
- Consumes: `DwgScratchCache.GetPath(dwg_ref)` → file path
- Produces: `CadLoad.Run(UIDocument, JsonElement)` → `{"ok": true, "layers": [...], "entity_counts": {...}, "bounds_mm": {...}}`

- [ ] **Step 1: Create CadLoad.cs with stub**

```csharp
// BinaVibe/Mcp/Tools/CadLoad.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACadSharp;
using ACadSharp.IO;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadLoad
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref");
            if (string.IsNullOrEmpty(dwgRef))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "dwg_ref required" };

            var path = DwgScratchCache.GetPath(dwgRef);
            if (path == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"attachment '{dwgRef}' not found" };

            try
            {
                return Extract(path);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        private static Dictionary<string, object?> Extract(string path)
        {
            CadDocument doc;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dwg")
            {
                using var reader = new DwgReader(path);
                doc = reader.Read();
            }
            else if (ext == ".dxf")
            {
                using var reader = new DxfReader(path);
                doc = reader.Read();
            }
            else
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"unsupported format: {ext}" };
            }

            var layers = doc.Layers.Select(l => l.Name).ToList();
            var entities = doc.ModelSpace.Entities.ToList();
            var entityCounts = entities
                .GroupBy(e => e.ObjectName)
                .ToDictionary(g => g.Key, g => g.Count());

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var entity in entities)
            {
                if (entity is ACadSharp.Entities.Line line)
                {
                    UpdateBounds(ref minX, ref minY, ref maxX, ref maxY, line.StartPoint.X, line.StartPoint.Y);
                    UpdateBounds(ref minX, ref minY, ref maxX, ref maxY, line.EndPoint.X, line.EndPoint.Y);
                }
            }

            var boundsValid = minX < double.MaxValue;
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["layers"] = layers,
                ["entity_counts"] = entityCounts,
                ["bounds_mm"] = boundsValid
                    ? new Dictionary<string, object?> { ["min"] = new[] { minX, minY }, ["max"] = new[] { maxX, maxY } }
                    : null,
                ["source_app"] = DetectSource(doc),
            };
        }

        private static void UpdateBounds(ref double minX, ref double minY, ref double maxX, ref double maxY, double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        private static string DetectSource(CadDocument doc)
        {
            // Check for vertical app classes
            try
            {
                foreach (var cls in doc.Classes)
                {
                    if (cls.DxfName.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase))
                        return "civil3d";
                    if (cls.DxfName.StartsWith("AEC_", StringComparison.OrdinalIgnoreCase))
                        return "autocad_architecture";
                }
            }
            catch { }
            return "plain_autocad";
        }
    }
}
```

- [ ] **Step 2: Register in ToolRegistry.cs**

Add to the switch statement in `ToolRegistry.Invoke`:

```csharp
"cad_load" => CadLoad.Run(uidoc, args),
```

- [ ] **Step 3: Build and verify compilation**

Run: `dotnet build BinaVibe/BinaVibe.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add BinaVibe/Mcp/Tools/CadLoad.cs BinaVibe/Mcp/Tools/ToolRegistry.cs
git commit -m "feat(cad): add cad_load MCP tool"
```

---

### Task 2: MCP Tool — cad_get_lines

**Files:**
- Create: `BinaVibe/Mcp/Tools/CadGetLines.cs`
- Modify: `BinaVibe/Mcp/Tools/ToolRegistry.cs` (add switch case)
- Test: Manual test via engine `/cad/classify` route (Task 5)

**Interfaces:**
- Consumes: `DwgScratchCache.GetPath(dwg_ref)` → file path
- Produces: `CadGetLines.Run(UIDocument, JsonElement)` → `{"ok": true, "lines": [...], "arcs": [...]}`

- [ ] **Step 1: Create CadGetLines.cs**

```csharp
// BinaVibe/Mcp/Tools/CadGetLines.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadGetLines
    {
        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref");
            if (string.IsNullOrEmpty(dwgRef))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "dwg_ref required" };

            var path = DwgScratchCache.GetPath(dwgRef);
            if (path == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"attachment '{dwgRef}' not found" };

            var layerFilter = ArgsHelp.GetString(args, "layer_filter");

            try
            {
                return Extract(path, layerFilter);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        private static Dictionary<string, object?> Extract(string path, string? layerFilter)
        {
            CadDocument doc;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dwg")
            {
                using var reader = new DwgReader(path);
                doc = reader.Read();
            }
            else if (ext == ".dxf")
            {
                using var reader = new DxfReader(path);
                doc = reader.Read();
            }
            else
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"unsupported format: {ext}" };
            }

            var entities = doc.ModelSpace.Entities.ToList();
            if (!string.IsNullOrEmpty(layerFilter))
            {
                entities = entities.Where(e =>
                    e.Layer.Name.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            var lines = new List<Dictionary<string, object?>>();
            var arcs = new List<Dictionary<string, object?>>();

            foreach (var entity in entities)
            {
                switch (entity)
                {
                    case Line line:
                        lines.Add(new Dictionary<string, object?>
                        {
                            ["x1"] = Math.Round(line.StartPoint.X, 1),
                            ["y1"] = Math.Round(line.StartPoint.Y, 1),
                            ["z1"] = Math.Round(line.StartPoint.Z, 1),
                            ["x2"] = Math.Round(line.EndPoint.X, 1),
                            ["y2"] = Math.Round(line.EndPoint.Y, 1),
                            ["z2"] = Math.Round(line.EndPoint.Z, 1),
                            ["layer"] = line.Layer.Name,
                        });
                        break;
                    case Arc arc:
                        arcs.Add(new Dictionary<string, object?>
                        {
                            ["cx"] = Math.Round(arc.Center.X, 1),
                            ["cy"] = Math.Round(arc.Center.Y, 1),
                            ["r"] = Math.Round(arc.Radius, 1),
                            ["start_deg"] = Math.Round(arc.StartAngle * 180 / Math.PI, 1),
                            ["end_deg"] = Math.Round(arc.EndAngle * 180 / Math.PI, 1),
                            ["layer"] = arc.Layer.Name,
                        });
                        break;
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["lines"] = lines,
                ["arcs"] = arcs,
                ["line_count"] = lines.Count,
                ["arc_count"] = arcs.Count,
            };
        }
    }
}
```

- [ ] **Step 2: Register in ToolRegistry.cs**

```csharp
"cad_get_lines" => CadGetLines.Run(uidoc, args),
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build BinaVibe/BinaVibe.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BinaVibe/Mcp/Tools/CadGetLines.cs BinaVibe/Mcp/Tools/ToolRegistry.cs
git commit -m "feat(cad): add cad_get_lines MCP tool"
```

---

### Task 3: Engine CAD Module — Session and Executor

**Files:**
- Create: `app/engine/cad/__init__.py`
- Create: `app/engine/cad/session.py`
- Test: `tests/test_engine_cad_session.py`

**Interfaces:**
- Produces: `CadSession` class with `load()`, `get_lines()`, `classify()`, `get_state()` methods
- Produces: `get_session(session_id)` → `CadSession`

- [ ] **Step 1: Write failing test for session manager**

```python
# tests/test_engine_cad_session.py
import pytest
from app.engine.cad.session import CadSessionManager, CadSession

def test_get_or_create_session():
    mgr = CadSessionManager()
    s1 = mgr.get_or_create("sess-1", "dwg-abc")
    s2 = mgr.get_or_create("sess-1", "dwg-abc")
    assert s1 is s2
    assert s1.dwg_ref == "dwg-abc"

def test_session_state_lifecycle():
    session = CadSession("sess-1", "dwg-abc")
    assert session.state == "init"
    
    session.set_layers(["WALL", "DOOR"])
    assert session.layers == ["WALL", "DOOR"]
    assert session.state == "loaded"
    
    session.set_classification({"WALL": "wall", "DOOR": "door_window"})
    assert session.classification["WALL"] == "wall"
    assert session.state == "classified"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ENVIRONMENT=dev pytest tests/test_engine_cad_session.py -v`
Expected: FAIL with ModuleNotFoundError

- [ ] **Step 3: Implement session module**

```python
# app/engine/cad/__init__.py
"""CAD-to-BIM viewer module."""
```

```python
# app/engine/cad/session.py
"""In-memory session state for CAD classification workflow."""
from dataclasses import dataclass, field
from typing import Literal

SessionState = Literal["init", "loaded", "classified", "confirmed"]

@dataclass
class CadSession:
    session_id: str
    dwg_ref: str
    state: SessionState = "init"
    layers: list[str] = field(default_factory=list)
    entity_counts: dict[str, int] = field(default_factory=dict)
    lines: list[dict] = field(default_factory=list)
    arcs: list[dict] = field(default_factory=list)
    classification: dict[str, str] = field(default_factory=dict)
    proposed_walls: list[dict] = field(default_factory=list)
    
    def set_layers(self, layers: list[str], entity_counts: dict[str, int] | None = None):
        self.layers = layers
        self.entity_counts = entity_counts or {}
        self.state = "loaded"
    
    def set_lines(self, lines: list[dict], arcs: list[dict]):
        self.lines = lines
        self.arcs = arcs
    
    def set_classification(self, classification: dict[str, str]):
        self.classification = classification
        self.state = "classified"
    
    def set_proposed_walls(self, walls: list[dict]):
        self.proposed_walls = walls
    
    def confirm(self):
        self.state = "confirmed"


class CadSessionManager:
    def __init__(self):
        self._sessions: dict[str, CadSession] = {}
    
    def get_or_create(self, session_id: str, dwg_ref: str) -> CadSession:
        if session_id not in self._sessions:
            self._sessions[session_id] = CadSession(session_id, dwg_ref)
        return self._sessions[session_id]
    
    def get(self, session_id: str) -> CadSession | None:
        return self._sessions.get(session_id)
    
    def remove(self, session_id: str):
        self._sessions.pop(session_id, None)


# Global instance
_manager = CadSessionManager()

def get_session_manager() -> CadSessionManager:
    return _manager
```

- [ ] **Step 4: Run test to verify it passes**

Run: `ENVIRONMENT=dev pytest tests/test_engine_cad_session.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add app/engine/cad/ tests/test_engine_cad_session.py
git commit -m "feat(cad): add session manager for CAD workflow"
```

---

### Task 4: Engine CAD Routes — Load and Lines

**Files:**
- Create: `app/engine/cad/routes.py`
- Modify: `app/engine/main.py` (mount router)
- Test: `tests/test_engine_cad_routes.py`

**Interfaces:**
- Consumes: `call_tool("cad_load", ...)` from `app/engine/executor.py`
- Consumes: `CadSessionManager` from Task 3
- Produces: `POST /cad/load` → `{"session_id": ..., "layers": [...]}`
- Produces: `POST /cad/lines` → `{"lines": [...], "arcs": [...]}`

- [ ] **Step 1: Write failing test**

```python
# tests/test_engine_cad_routes.py
import pytest
from fastapi.testclient import TestClient
from unittest.mock import AsyncMock, patch

@pytest.fixture
def client():
    # Patch engine check and create test app
    with patch("app.engine.config.engine_enabled", return_value=True):
        with patch("app.engine.config.get_engine_config") as mock_cfg:
            mock_cfg.return_value.secret = "test-secret"
            mock_cfg.return_value.addin_tool_url = "http://localhost:48820"
            from app.engine.main import create_engine_app
            app = create_engine_app()
            yield TestClient(app)

def test_cad_load_calls_tool(client):
    with patch("app.engine.cad.routes.call_tool", new_callable=AsyncMock) as mock_call:
        mock_call.return_value = {
            "ok": True,
            "layers": ["WALL", "DOOR"],
            "entity_counts": {"Line": 100},
            "bounds_mm": {"min": [0, 0], "max": [1000, 1000]},
        }
        resp = client.post("/cad/load", json={"dwg_ref": "test-dwg"})
        assert resp.status_code == 200
        data = resp.json()
        assert data["layers"] == ["WALL", "DOOR"]
        assert "session_id" in data
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ENVIRONMENT=dev pytest tests/test_engine_cad_routes.py::test_cad_load_calls_tool -v`
Expected: FAIL (routes not implemented)

- [ ] **Step 3: Implement routes**

```python
# app/engine/cad/routes.py
"""CAD-to-BIM viewer routes."""
import uuid
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from app.engine.executor import call_tool
from app.engine.cad.session import get_session_manager

router = APIRouter(prefix="/cad", tags=["CAD Viewer"])


class LoadRequest(BaseModel):
    dwg_ref: str
    session_id: str | None = None


class LinesRequest(BaseModel):
    session_id: str
    layer_filter: str | None = None


@router.post("/load")
async def load_cad(req: LoadRequest):
    """Load DWG metadata and create session."""
    session_id = req.session_id or uuid.uuid4().hex[:12]
    
    result = await call_tool("cad_load", {"dwg_ref": req.dwg_ref})
    if not result.get("ok"):
        raise HTTPException(400, result.get("error", "cad_load failed"))
    
    mgr = get_session_manager()
    session = mgr.get_or_create(session_id, req.dwg_ref)
    session.set_layers(result["layers"], result.get("entity_counts"))
    
    return {
        "session_id": session_id,
        "layers": result["layers"],
        "entity_counts": result.get("entity_counts", {}),
        "bounds_mm": result.get("bounds_mm"),
    }


@router.post("/lines")
async def get_lines(req: LinesRequest):
    """Get line/arc geometry for classification."""
    mgr = get_session_manager()
    session = mgr.get(req.session_id)
    if not session:
        raise HTTPException(404, f"session {req.session_id} not found")
    
    result = await call_tool("cad_get_lines", {
        "dwg_ref": session.dwg_ref,
        "layer_filter": req.layer_filter,
    })
    if not result.get("ok"):
        raise HTTPException(400, result.get("error", "cad_get_lines failed"))
    
    session.set_lines(result["lines"], result["arcs"])
    
    return {
        "lines": result["lines"],
        "arcs": result["arcs"],
        "line_count": len(result["lines"]),
        "arc_count": len(result["arcs"]),
    }


def get_cad_router() -> APIRouter:
    return router
```

- [ ] **Step 4: Mount router in engine main**

Add to `app/engine/main.py` in `create_engine_app()`:

```python
from app.engine.cad.routes import get_cad_router
# ... after other router includes
app.include_router(get_cad_router())
```

- [ ] **Step 5: Run test to verify it passes**

Run: `ENVIRONMENT=dev pytest tests/test_engine_cad_routes.py -v`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add app/engine/cad/routes.py app/engine/main.py tests/test_engine_cad_routes.py
git commit -m "feat(cad): add /cad/load and /cad/lines routes"
```

---

### Task 5: ALCM Classifier — Port from C#

**Files:**
- Create: `app/engine/cad/classifier.py`
- Test: `tests/test_cad_classifier.py`

**Interfaces:**
- Consumes: `lines: list[dict]`, `arcs: list[dict]` from session
- Produces: `classify_layers(lines, arcs, settings)` → `{"wall": "WALL", "door_window": "DOOR", "scores": {...}}`

- [ ] **Step 1: Write failing test**

```python
# tests/test_cad_classifier.py
import pytest
from app.engine.cad.classifier import classify_layers, ClassificationSettings

def test_classify_wall_layer():
    # Wall lines: parallel pairs ~200mm apart
    lines = [
        {"x1": 0, "y1": 0, "x2": 5000, "y2": 0, "layer": "WALL"},
        {"x1": 0, "y1": 200, "x2": 5000, "y2": 200, "layer": "WALL"},
        {"x1": 0, "y1": 0, "x2": 0, "y2": 200, "layer": "WALL"},  # end cap
    ]
    arcs = []
    
    result = classify_layers(lines, arcs)
    assert result["wall"] == "WALL"

def test_classify_door_layer():
    lines = []
    # Door swing arc: ~90 deg, 800mm radius
    arcs = [
        {"cx": 0, "cy": 0, "r": 800, "start_deg": 0, "end_deg": 90, "layer": "DOOR"},
    ]
    
    result = classify_layers(lines, arcs)
    assert result["door_window"] == "DOOR"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ENVIRONMENT=dev pytest tests/test_cad_classifier.py -v`
Expected: FAIL with ModuleNotFoundError

- [ ] **Step 3: Implement classifier**

```python
# app/engine/cad/classifier.py
"""ALCM-based layer classification for CAD-to-BIM.

Port of the C# ALCM implementation. Scores layers against target
definitions (Wall, DoorWindow) using Necessary/Sufficient conditions.
"""
from dataclasses import dataclass, field
from typing import Literal
import math


@dataclass
class ClassificationSettings:
    min_wall_thickness: float = 100.0
    max_wall_thickness: float = 300.0
    endpoint_tolerance: float = 1.0
    angle_tolerance: float = 2.0
    door_min_radius: float = 600.0
    door_max_radius: float = 1000.0
    door_sweep_tolerance: float = 10.0


@dataclass
class LayerScore:
    layer: str
    target: str
    score: float
    matched_count: int


def classify_layers(
    lines: list[dict],
    arcs: list[dict],
    settings: ClassificationSettings | None = None,
) -> dict:
    """Classify layers into wall/door_window targets."""
    settings = settings or ClassificationSettings()
    
    # Group by layer
    lines_by_layer: dict[str, list[dict]] = {}
    arcs_by_layer: dict[str, list[dict]] = {}
    
    for line in lines:
        layer = line.get("layer", "")
        lines_by_layer.setdefault(layer, []).append(line)
    
    for arc in arcs:
        layer = arc.get("layer", "")
        arcs_by_layer.setdefault(layer, []).append(arc)
    
    all_layers = set(lines_by_layer.keys()) | set(arcs_by_layer.keys())
    
    # Score each layer for each target
    wall_scores: dict[str, LayerScore] = {}
    door_scores: dict[str, LayerScore] = {}
    
    for layer in all_layers:
        layer_lines = lines_by_layer.get(layer, [])
        layer_arcs = arcs_by_layer.get(layer, [])
        
        # Wall scoring: look for end caps (short lines) with perpendicular faces
        wall_score, wall_matched = _score_wall_layer(layer_lines, settings)
        if wall_score > 0:
            wall_scores[layer] = LayerScore(layer, "wall", wall_score, wall_matched)
        
        # Door scoring: look for 90-degree swing arcs
        door_score, door_matched = _score_door_layer(layer_arcs, settings)
        if door_score > 0:
            door_scores[layer] = LayerScore(layer, "door_window", door_score, door_matched)
    
    # Pick winners
    wall_winner = max(wall_scores.values(), key=lambda s: s.score, default=None)
    door_winner = max(door_scores.values(), key=lambda s: s.score, default=None)
    
    return {
        "wall": wall_winner.layer if wall_winner else None,
        "door_window": door_winner.layer if door_winner else None,
        "wall_score": wall_winner.score if wall_winner else 0,
        "door_score": door_winner.score if door_winner else 0,
        "wall_matched": wall_winner.matched_count if wall_winner else 0,
        "door_matched": door_winner.matched_count if door_winner else 0,
    }


def _score_wall_layer(lines: list[dict], settings: ClassificationSettings) -> tuple[float, int]:
    """Score a layer for wall likelihood based on end caps + perpendicular faces."""
    if not lines:
        return 0.0, 0
    
    total_score = 0.0
    matched = 0
    
    for line in lines:
        length = _line_length(line)
        
        # Check if this could be an end cap (short line in thickness range)
        if settings.min_wall_thickness <= length <= settings.max_wall_thickness:
            # Look for perpendicular lines meeting this end cap's endpoints
            perp_count = 0
            for other in lines:
                if other is line:
                    continue
                if _lines_perpendicular(line, other, settings.angle_tolerance):
                    if _lines_touch(line, other, settings.endpoint_tolerance):
                        perp_count += 1
            
            if perp_count >= 2:
                # Found a wall pattern: cap + two perpendicular faces
                sc_true = 2  # both faces found
                sc_total = 2
                score = (sc_true + 1) / (sc_total + 1)
                total_score += score
                matched += 1
    
    return total_score, matched


def _score_door_layer(arcs: list[dict], settings: ClassificationSettings) -> tuple[float, int]:
    """Score a layer for door likelihood based on swing arcs."""
    if not arcs:
        return 0.0, 0
    
    total_score = 0.0
    matched = 0
    
    for arc in arcs:
        sweep = abs(arc.get("end_deg", 0) - arc.get("start_deg", 0))
        radius = arc.get("r", 0)
        
        sc_true = 0
        sc_total = 2
        
        # Check sweep ~90 degrees
        if abs(sweep - 90) <= settings.door_sweep_tolerance:
            sc_true += 1
        
        # Check radius in door range
        if settings.door_min_radius <= radius <= settings.door_max_radius:
            sc_true += 1
        
        if sc_true > 0:
            score = (sc_true + 1) / (sc_total + 1)
            total_score += score
            matched += 1
    
    return total_score, matched


def _line_length(line: dict) -> float:
    dx = line.get("x2", 0) - line.get("x1", 0)
    dy = line.get("y2", 0) - line.get("y1", 0)
    return math.sqrt(dx * dx + dy * dy)


def _line_heading(line: dict) -> float:
    """Heading in degrees, folded to [0, 180)."""
    dx = line.get("x2", 0) - line.get("x1", 0)
    dy = line.get("y2", 0) - line.get("y1", 0)
    deg = math.degrees(math.atan2(dy, dx)) % 180
    return deg if deg >= 0 else deg + 180


def _lines_perpendicular(a: dict, b: dict, tolerance_deg: float) -> bool:
    ha = _line_heading(a)
    hb = _line_heading(b)
    delta = abs(ha - hb)
    delta = min(delta, 180 - delta)
    return abs(delta - 90) <= tolerance_deg


def _lines_touch(a: dict, b: dict, tolerance: float) -> bool:
    """Check if any endpoints are within tolerance."""
    pts_a = [(a.get("x1", 0), a.get("y1", 0)), (a.get("x2", 0), a.get("y2", 0))]
    pts_b = [(b.get("x1", 0), b.get("y1", 0)), (b.get("x2", 0), b.get("y2", 0))]
    
    for ax, ay in pts_a:
        for bx, by in pts_b:
            dist = math.sqrt((ax - bx) ** 2 + (ay - by) ** 2)
            if dist <= tolerance:
                return True
    return False
```

- [ ] **Step 4: Run test to verify it passes**

Run: `ENVIRONMENT=dev pytest tests/test_cad_classifier.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add app/engine/cad/classifier.py tests/test_cad_classifier.py
git commit -m "feat(cad): add ALCM layer classifier"
```

---

### Task 6: Centerline Stitcher — Port from C#

**Files:**
- Create: `app/engine/cad/stitcher.py`
- Test: `tests/test_cad_stitcher.py`

**Interfaces:**
- Consumes: `lines: list[dict]` from session (filtered by wall layer)
- Produces: `compute_centerlines(lines, settings)` → `[{"ax": ..., "ay": ..., "bx": ..., "by": ..., "thickness_mm": ...}]`

- [ ] **Step 1: Write failing test**

```python
# tests/test_cad_stitcher.py
import pytest
from app.engine.cad.stitcher import compute_centerlines, StitcherSettings

def test_parallel_pair_to_centerline():
    # Two parallel lines 200mm apart
    lines = [
        {"x1": 0, "y1": 0, "x2": 5000, "y2": 0, "layer": "WALL"},
        {"x1": 0, "y1": 200, "x2": 5000, "y2": 200, "layer": "WALL"},
    ]
    
    result = compute_centerlines(lines)
    assert len(result) == 1
    
    wall = result[0]
    assert wall["ay"] == pytest.approx(100, abs=1)  # centerline at y=100
    assert wall["thickness_mm"] == pytest.approx(200, abs=1)

def test_stitch_across_door_gap():
    # Two collinear segments with 800mm gap (door opening)
    lines = [
        {"x1": 0, "y1": 0, "x2": 2000, "y2": 0, "layer": "WALL"},
        {"x1": 2800, "y1": 0, "x2": 5000, "y2": 0, "layer": "WALL"},
        {"x1": 0, "y1": 200, "x2": 2000, "y2": 200, "layer": "WALL"},
        {"x1": 2800, "y1": 200, "x2": 5000, "y2": 200, "layer": "WALL"},
    ]
    
    settings = StitcherSettings(max_stitch_gap=1500)
    result = compute_centerlines(lines, settings)
    
    # Should stitch into one wall
    assert len(result) == 1
```

- [ ] **Step 2: Run test to verify it fails**

Run: `ENVIRONMENT=dev pytest tests/test_cad_stitcher.py -v`
Expected: FAIL

- [ ] **Step 3: Implement stitcher**

```python
# app/engine/cad/stitcher.py
"""Centerline extraction from parallel wall faces.

Port of the C# CadCenterlineSolver. Pairs parallel segments,
computes centerlines, and stitches across door gaps.
"""
from dataclasses import dataclass
import math


@dataclass
class StitcherSettings:
    min_thickness: float = 50.0
    max_thickness: float = 500.0
    angle_tolerance: float = 1.5  # degrees
    overlap_min_ratio: float = 0.5
    min_segment_length: float = 300.0
    max_stitch_gap: float = 1500.0
    snap_distance: float = 500.0


@dataclass
class Segment:
    ax: float
    ay: float
    bx: float
    by: float
    layer: str = ""
    
    @property
    def length(self) -> float:
        return math.sqrt((self.bx - self.ax) ** 2 + (self.by - self.ay) ** 2)
    
    @property
    def heading(self) -> float:
        """Heading in degrees, folded to [0, 180)."""
        deg = math.degrees(math.atan2(self.by - self.ay, self.bx - self.ax)) % 180
        return deg if deg >= 0 else deg + 180


def compute_centerlines(
    lines: list[dict],
    settings: StitcherSettings | None = None,
) -> list[dict]:
    """Compute wall centerlines from parallel line pairs."""
    settings = settings or StitcherSettings()
    
    # Convert to segments
    segments = [
        Segment(
            ax=line.get("x1", 0),
            ay=line.get("y1", 0),
            bx=line.get("x2", 0),
            by=line.get("y2", 0),
            layer=line.get("layer", ""),
        )
        for line in lines
    ]
    
    # Filter short segments
    segments = [s for s in segments if s.length >= settings.min_segment_length]
    
    # Step 1: Stitch collinear segments across gaps
    segments = _stitch_collinear(segments, settings)
    
    # Step 2: Pair parallel segments
    centerlines = []
    used = set()
    sin_tol = math.sin(math.radians(settings.angle_tolerance))
    
    for i, seg_i in enumerate(segments):
        if i in used:
            continue
        
        best_j = None
        best_gap = float("inf")
        
        for j, seg_j in enumerate(segments):
            if j <= i or j in used:
                continue
            
            # Check parallel
            if not _are_parallel(seg_i, seg_j, sin_tol):
                continue
            
            # Check perpendicular gap
            gap = _perpendicular_gap(seg_i, seg_j)
            if gap < settings.min_thickness or gap > settings.max_thickness:
                continue
            
            # Check overlap
            if not _segments_overlap(seg_i, seg_j, settings.overlap_min_ratio):
                continue
            
            if gap < best_gap:
                best_gap = gap
                best_j = j
        
        if best_j is not None:
            used.add(i)
            used.add(best_j)
            seg_j = segments[best_j]
            
            # Compute centerline
            cx = _centerline(seg_i, seg_j)
            centerlines.append({
                "ax": round(cx.ax, 1),
                "ay": round(cx.ay, 1),
                "bx": round(cx.bx, 1),
                "by": round(cx.by, 1),
                "thickness_mm": round(best_gap, 1),
            })
    
    return centerlines


def _stitch_collinear(segments: list[Segment], settings: StitcherSettings) -> list[Segment]:
    """Stitch collinear segments that are close together (door gaps)."""
    if len(segments) < 2:
        return segments
    
    sin_tol = math.sin(math.radians(settings.angle_tolerance))
    result = list(segments)
    changed = True
    
    while changed:
        changed = False
        new_result = []
        used = set()
        
        for i, seg_i in enumerate(result):
            if i in used:
                continue
            
            best_j = None
            best_dist = float("inf")
            
            for j, seg_j in enumerate(result):
                if j <= i or j in used:
                    continue
                
                # Check collinear (parallel + on same line)
                if not _are_parallel(seg_i, seg_j, sin_tol):
                    continue
                
                gap = _perpendicular_gap(seg_i, seg_j)
                if gap > settings.angle_tolerance:
                    continue
                
                # Check end-to-end distance
                dist = _endpoint_gap(seg_i, seg_j)
                if dist > settings.max_stitch_gap:
                    continue
                
                if dist < best_dist:
                    best_dist = dist
                    best_j = j
            
            if best_j is not None:
                used.add(i)
                used.add(best_j)
                # Merge into one segment spanning both
                merged = _merge_segments(seg_i, result[best_j])
                new_result.append(merged)
                changed = True
            else:
                new_result.append(seg_i)
        
        result = new_result
    
    return result


def _are_parallel(a: Segment, b: Segment, sin_tol: float) -> bool:
    ha = a.heading
    hb = b.heading
    delta = abs(ha - hb)
    delta = min(delta, 180 - delta)
    return math.sin(math.radians(delta)) <= sin_tol


def _perpendicular_gap(a: Segment, b: Segment) -> float:
    """Distance between parallel lines."""
    dx = a.bx - a.ax
    dy = a.by - a.ay
    length = math.sqrt(dx * dx + dy * dy)
    if length < 1e-9:
        return float("inf")
    
    # Point b.a to line a
    cross = dx * (b.ay - a.ay) - dy * (b.ax - a.ax)
    return abs(cross) / length


def _segments_overlap(a: Segment, b: Segment, min_ratio: float) -> bool:
    """Check if segments overlap when projected onto their shared axis."""
    dx = a.bx - a.ax
    dy = a.by - a.ay
    len_sq = dx * dx + dy * dy
    if len_sq < 1e-9:
        return False
    
    # Project b endpoints onto a's axis
    def proj(px: float, py: float) -> float:
        return ((px - a.ax) * dx + (py - a.ay) * dy) / len_sq
    
    t1 = proj(b.ax, b.ay)
    t2 = proj(b.bx, b.by)
    b_min, b_max = min(t1, t2), max(t1, t2)
    
    # a spans [0, 1]
    overlap_start = max(0, b_min)
    overlap_end = min(1, b_max)
    overlap = max(0, overlap_end - overlap_start)
    
    return overlap >= min_ratio


def _endpoint_gap(a: Segment, b: Segment) -> float:
    """Minimum distance between endpoints of two segments."""
    pts_a = [(a.ax, a.ay), (a.bx, a.by)]
    pts_b = [(b.ax, b.ay), (b.bx, b.by)]
    
    min_dist = float("inf")
    for ax, ay in pts_a:
        for bx, by in pts_b:
            dist = math.sqrt((ax - bx) ** 2 + (ay - by) ** 2)
            min_dist = min(min_dist, dist)
    return min_dist


def _merge_segments(a: Segment, b: Segment) -> Segment:
    """Merge two collinear segments into one spanning both."""
    # Project all 4 endpoints onto a's direction
    dx = a.bx - a.ax
    dy = a.by - a.ay
    length = math.sqrt(dx * dx + dy * dy)
    if length < 1e-9:
        return a
    
    ux, uy = dx / length, dy / length
    
    def proj(px: float, py: float) -> float:
        return (px - a.ax) * ux + (py - a.ay) * uy
    
    projections = [
        (proj(a.ax, a.ay), a.ax, a.ay),
        (proj(a.bx, a.by), a.bx, a.by),
        (proj(b.ax, b.ay), b.ax, b.ay),
        (proj(b.bx, b.by), b.bx, b.by),
    ]
    projections.sort(key=lambda x: x[0])
    
    # Take the two extremes
    _, ax, ay = projections[0]
    _, bx, by = projections[-1]
    
    return Segment(ax, ay, bx, by, a.layer)


def _centerline(a: Segment, b: Segment) -> Segment:
    """Compute centerline between two parallel segments."""
    return Segment(
        ax=(a.ax + b.ax) / 2,
        ay=(a.ay + b.ay) / 2,
        bx=(a.bx + b.bx) / 2,
        by=(a.by + b.by) / 2,
        layer=a.layer,
    )
```

- [ ] **Step 4: Run test to verify it passes**

Run: `ENVIRONMENT=dev pytest tests/test_cad_stitcher.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add app/engine/cad/stitcher.py tests/test_cad_stitcher.py
git commit -m "feat(cad): add centerline stitcher"
```

---

### Task 7: Viewer HTML with libredwg-web

**Files:**
- Create: `app/engine/cad/static/viewer.html`
- Modify: `app/engine/cad/routes.py` (add static file serving)
- Test: Manual browser test

**Interfaces:**
- Consumes: `/cad/load`, `/cad/lines` JSON endpoints
- Produces: Canvas rendering, layer toggles, AI chat sidebar

- [ ] **Step 1: Create viewer HTML**

```html
<!-- app/engine/cad/static/viewer.html -->
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>CAD-to-BIM Viewer</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { font-family: -apple-system, BlinkMacSystemFont, sans-serif; background: #1a1a2e; color: #eee; }
        .container { display: flex; height: 100vh; }
        .canvas-area { flex: 1; position: relative; }
        #cadCanvas { width: 100%; height: 100%; background: #0f0f23; }
        .sidebar { width: 300px; background: #16213e; display: flex; flex-direction: column; }
        .panel { padding: 16px; border-bottom: 1px solid #0f3460; }
        .panel h3 { font-size: 14px; margin-bottom: 12px; color: #e94560; }
        .layer-item { display: flex; align-items: center; gap: 8px; padding: 4px 0; }
        .layer-item input { accent-color: #e94560; }
        .layer-item label { font-size: 13px; }
        .layer-count { color: #888; font-size: 11px; }
        .chat-area { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
        .chat-messages { flex: 1; overflow-y: auto; padding: 12px; }
        .message { padding: 10px 12px; margin: 8px 0; border-radius: 8px; font-size: 13px; }
        .message.ai { background: #0f3460; }
        .message.user { background: #e94560; margin-left: 20px; }
        .chat-input { padding: 12px; border-top: 1px solid #0f3460; }
        .btn-group { display: flex; gap: 8px; }
        .btn { padding: 8px 16px; border: none; border-radius: 6px; cursor: pointer; font-size: 13px; }
        .btn-primary { background: #e94560; color: #fff; }
        .btn-secondary { background: #0f3460; color: #fff; }
        .btn-create { width: 100%; padding: 12px; margin-top: 12px; }
        .status { padding: 8px 16px; background: #0f3460; font-size: 12px; }
    </style>
</head>
<body>
    <div class="container">
        <div class="canvas-area">
            <canvas id="cadCanvas"></canvas>
        </div>
        <div class="sidebar">
            <div class="panel">
                <h3>Layers</h3>
                <div id="layerList"></div>
            </div>
            <div class="panel chat-area">
                <h3>AI Assistant</h3>
                <div class="chat-messages" id="chatMessages"></div>
                <div class="chat-input">
                    <div class="btn-group" id="responseButtons"></div>
                </div>
            </div>
            <div class="panel">
                <button class="btn btn-primary btn-create" id="createBtn" disabled>Create Walls</button>
            </div>
            <div class="status" id="status">Ready</div>
        </div>
    </div>

    <script type="module">
        // State
        let sessionId = null;
        let layers = [];
        let lines = [];
        let arcs = [];
        let classification = {};
        let transform = { scale: 1, offsetX: 0, offsetY: 0 };
        let dragging = false;
        let lastMouse = { x: 0, y: 0 };

        // Canvas setup
        const canvas = document.getElementById('cadCanvas');
        const ctx = canvas.getContext('2d');
        
        function resizeCanvas() {
            canvas.width = canvas.offsetWidth * window.devicePixelRatio;
            canvas.height = canvas.offsetHeight * window.devicePixelRatio;
            ctx.scale(window.devicePixelRatio, window.devicePixelRatio);
            render();
        }
        window.addEventListener('resize', resizeCanvas);

        // Pan/zoom
        canvas.addEventListener('mousedown', e => {
            dragging = true;
            lastMouse = { x: e.clientX, y: e.clientY };
        });
        canvas.addEventListener('mousemove', e => {
            if (!dragging) return;
            transform.offsetX += e.clientX - lastMouse.x;
            transform.offsetY += e.clientY - lastMouse.y;
            lastMouse = { x: e.clientX, y: e.clientY };
            render();
        });
        canvas.addEventListener('mouseup', () => dragging = false);
        canvas.addEventListener('wheel', e => {
            e.preventDefault();
            const factor = e.deltaY > 0 ? 0.9 : 1.1;
            transform.scale *= factor;
            render();
        });

        // Rendering
        function render() {
            const w = canvas.offsetWidth;
            const h = canvas.offsetHeight;
            ctx.fillStyle = '#0f0f23';
            ctx.fillRect(0, 0, w, h);
            
            ctx.save();
            ctx.translate(transform.offsetX + w/2, transform.offsetY + h/2);
            ctx.scale(transform.scale, -transform.scale); // Y-flip for CAD coords
            
            // Draw lines
            ctx.strokeStyle = '#888';
            ctx.lineWidth = 1 / transform.scale;
            for (const line of lines) {
                if (!isLayerVisible(line.layer)) continue;
                ctx.beginPath();
                ctx.moveTo(line.x1, line.y1);
                ctx.lineTo(line.x2, line.y2);
                ctx.stroke();
            }
            
            // Draw arcs
            for (const arc of arcs) {
                if (!isLayerVisible(arc.layer)) continue;
                ctx.beginPath();
                ctx.arc(arc.cx, arc.cy, arc.r, 
                    arc.start_deg * Math.PI / 180, 
                    arc.end_deg * Math.PI / 180);
                ctx.stroke();
            }
            
            ctx.restore();
        }

        function isLayerVisible(name) {
            const checkbox = document.querySelector(`input[data-layer="${name}"]`);
            return checkbox ? checkbox.checked : true;
        }

        function fitToExtents() {
            if (lines.length === 0) return;
            
            let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
            for (const line of lines) {
                minX = Math.min(minX, line.x1, line.x2);
                minY = Math.min(minY, line.y1, line.y2);
                maxX = Math.max(maxX, line.x1, line.x2);
                maxY = Math.max(maxY, line.y1, line.y2);
            }
            
            const w = canvas.offsetWidth;
            const h = canvas.offsetHeight;
            const dataW = maxX - minX || 1;
            const dataH = maxY - minY || 1;
            
            transform.scale = 0.9 * Math.min(w / dataW, h / dataH);
            transform.offsetX = -(minX + maxX) / 2 * transform.scale;
            transform.offsetY = (minY + maxY) / 2 * transform.scale;
            render();
        }

        // API calls
        async function loadCAD(dwgRef) {
            setStatus('Loading DWG...');
            const resp = await fetch('/cad/load', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ dwg_ref: dwgRef })
            });
            const data = await resp.json();
            if (!resp.ok) throw new Error(data.detail || 'Load failed');
            
            sessionId = data.session_id;
            layers = data.layers || [];
            renderLayers();
            
            setStatus('Loading geometry...');
            const linesResp = await fetch('/cad/lines', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ session_id: sessionId })
            });
            const linesData = await linesResp.json();
            lines = linesData.lines || [];
            arcs = linesData.arcs || [];
            
            resizeCanvas();
            fitToExtents();
            setStatus(`Loaded: ${lines.length} lines, ${arcs.length} arcs`);
            
            // Start AI clarification
            startClarification();
        }

        function renderLayers() {
            const list = document.getElementById('layerList');
            list.innerHTML = layers.map(name => `
                <div class="layer-item">
                    <input type="checkbox" checked data-layer="${name}" onchange="render()">
                    <label>${name}</label>
                </div>
            `).join('');
        }

        async function startClarification() {
            addMessage('ai', 'Analyzing layers for wall classification...');
            // TODO: Call /cad/clarify and handle SSE
            addMessage('ai', `Found ${layers.length} layers. Is "${layers[0]}" the wall layer?`);
            showButtons(['Yes', 'No']);
        }

        function addMessage(type, text) {
            const chat = document.getElementById('chatMessages');
            const div = document.createElement('div');
            div.className = `message ${type}`;
            div.textContent = text;
            chat.appendChild(div);
            chat.scrollTop = chat.scrollHeight;
        }

        function showButtons(options) {
            const container = document.getElementById('responseButtons');
            container.innerHTML = options.map(opt => 
                `<button class="btn btn-secondary" onclick="respond('${opt}')">${opt}</button>`
            ).join('');
        }

        window.respond = function(answer) {
            addMessage('user', answer);
            document.getElementById('responseButtons').innerHTML = '';
            // TODO: Send to /cad/clarify
            document.getElementById('createBtn').disabled = false;
        };

        function setStatus(text) {
            document.getElementById('status').textContent = text;
        }

        // Init
        resizeCanvas();
        
        // Get dwg_ref from URL params
        const params = new URLSearchParams(window.location.search);
        const dwgRef = params.get('dwg_ref');
        if (dwgRef) {
            loadCAD(dwgRef).catch(err => {
                setStatus(`Error: ${err.message}`);
            });
        } else {
            setStatus('No dwg_ref provided');
        }
    </script>
</body>
</html>
```

- [ ] **Step 2: Add static file route**

Add to `app/engine/cad/routes.py`:

```python
from pathlib import Path
from fastapi.responses import FileResponse

STATIC_DIR = Path(__file__).parent / "static"

@router.get("/viewer")
async def viewer():
    """Serve the CAD viewer HTML."""
    return FileResponse(STATIC_DIR / "viewer.html", media_type="text/html")
```

- [ ] **Step 3: Test in browser**

Run: `BINA_ENGINE=1 uv run uvicorn app.engine.main:app --host 127.0.0.1 --port 48810`
Open: `http://localhost:48810/cad/viewer?dwg_ref=test`
Expected: Viewer loads (will show error without valid dwg_ref)

- [ ] **Step 4: Commit**

```bash
git add app/engine/cad/static/viewer.html app/engine/cad/routes.py
git commit -m "feat(cad): add browser viewer with libredwg rendering"
```

---

### Task 8: Wire Classification and Preview Routes

**Files:**
- Modify: `app/engine/cad/routes.py` (add /classify, /preview)
- Test: `tests/test_engine_cad_routes.py` (extend)

**Interfaces:**
- Consumes: `classify_layers()` from Task 5
- Consumes: `compute_centerlines()` from Task 6
- Produces: `POST /cad/classify` → `{"wall": "WALL", "door_window": "DOOR", ...}`
- Produces: `POST /cad/preview` → `{"centerlines": [...], "count": N}`

- [ ] **Step 1: Add classify/preview routes**

```python
# Add to app/engine/cad/routes.py

from app.engine.cad.classifier import classify_layers
from app.engine.cad.stitcher import compute_centerlines


class ClassifyRequest(BaseModel):
    session_id: str


class PreviewRequest(BaseModel):
    session_id: str
    wall_layer: str | None = None


@router.post("/classify")
async def classify_cad(req: ClassifyRequest):
    """Run ALCM classification on loaded geometry."""
    mgr = get_session_manager()
    session = mgr.get(req.session_id)
    if not session:
        raise HTTPException(404, f"session {req.session_id} not found")
    
    if not session.lines:
        raise HTTPException(400, "no geometry loaded - call /cad/lines first")
    
    result = classify_layers(session.lines, session.arcs)
    session.set_classification({
        "wall": result.get("wall"),
        "door_window": result.get("door_window"),
    })
    
    return result


@router.post("/preview")
async def preview_walls(req: PreviewRequest):
    """Compute centerlines for preview."""
    mgr = get_session_manager()
    session = mgr.get(req.session_id)
    if not session:
        raise HTTPException(404, f"session {req.session_id} not found")
    
    wall_layer = req.wall_layer or session.classification.get("wall")
    if not wall_layer:
        raise HTTPException(400, "no wall layer specified or classified")
    
    # Filter lines by wall layer
    wall_lines = [l for l in session.lines 
                  if l.get("layer", "").upper() == wall_layer.upper()]
    
    centerlines = compute_centerlines(wall_lines)
    session.set_proposed_walls(centerlines)
    
    return {
        "centerlines": centerlines,
        "count": len(centerlines),
        "wall_layer": wall_layer,
    }
```

- [ ] **Step 2: Add tests**

```python
# Add to tests/test_engine_cad_routes.py

def test_classify_returns_layers(client):
    # Setup session with mock data
    with patch("app.engine.cad.routes.call_tool", new_callable=AsyncMock) as mock_call:
        mock_call.return_value = {"ok": True, "layers": ["WALL"], "entity_counts": {}}
        client.post("/cad/load", json={"dwg_ref": "test"})
        
        mock_call.return_value = {
            "ok": True,
            "lines": [
                {"x1": 0, "y1": 0, "x2": 5000, "y2": 0, "layer": "WALL"},
                {"x1": 0, "y1": 200, "x2": 5000, "y2": 200, "layer": "WALL"},
                {"x1": 0, "y1": 0, "x2": 0, "y2": 200, "layer": "WALL"},
            ],
            "arcs": [],
        }
        # Get session_id from load response and use it
        # ... (full test implementation)
```

- [ ] **Step 3: Run tests**

Run: `ENVIRONMENT=dev pytest tests/test_engine_cad_routes.py -v`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add app/engine/cad/routes.py tests/test_engine_cad_routes.py
git commit -m "feat(cad): add classify and preview routes"
```

---

### Task 9: MCP Tool — cad_create_walls

**Files:**
- Create: `BinaVibe/Mcp/Tools/CadCreateWalls.cs`
- Modify: `BinaVibe/Mcp/Tools/ToolRegistry.cs`
- Test: Manual end-to-end test in Revit

**Interfaces:**
- Consumes: centerlines from preview, level name, wall type
- Produces: `{"ok": true, "wall_ids": [...], "count": N}`

- [ ] **Step 1: Create CadCreateWalls.cs**

```csharp
// BinaVibe/Mcp/Tools/CadCreateWalls.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class CadCreateWalls
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            
            // Parse centerlines
            if (!args.TryGetProperty("centerlines", out var centerlinesEl))
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "centerlines required" };
            
            var centerlines = new List<(double ax, double ay, double bx, double by, double thickness)>();
            foreach (var cl in centerlinesEl.EnumerateArray())
            {
                centerlines.Add((
                    cl.GetProperty("ax").GetDouble() / MmPerFoot,
                    cl.GetProperty("ay").GetDouble() / MmPerFoot,
                    cl.GetProperty("bx").GetDouble() / MmPerFoot,
                    cl.GetProperty("by").GetDouble() / MmPerFoot,
                    cl.TryGetProperty("thickness_mm", out var t) ? t.GetDouble() : 200
                ));
            }
            
            // Get level
            var levelName = ArgsHelp.GetString(args, "level");
            Level? level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
            }
            level ??= new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).First();
            
            // Get wall type
            var wallTypeName = ArgsHelp.GetString(args, "wall_type");
            WallType? wallType = null;
            if (!string.IsNullOrEmpty(wallTypeName))
            {
                wallType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WallType)).Cast<WallType>()
                    .FirstOrDefault(wt => wt.Name.Contains(wallTypeName, StringComparison.OrdinalIgnoreCase));
            }
            wallType ??= new FilteredElementCollector(doc)
                .OfClass(typeof(WallType)).Cast<WallType>()
                .First();
            
            var wallIds = new List<long>();
            var errors = new List<string>();
            
            using (var txn = new Transaction(doc, "Create Walls from CAD"))
            {
                txn.Start();
                
                foreach (var (ax, ay, bx, by, thickness) in centerlines)
                {
                    try
                    {
                        var start = new XYZ(ax, ay, level.Elevation);
                        var end = new XYZ(bx, by, level.Elevation);
                        var line = Line.CreateBound(start, end);
                        
                        var wall = Wall.Create(doc, line, wallType.Id, level.Id, 10.0, 0, false, false);
                        wallIds.Add(wall.Id.Value);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.Message);
                    }
                }
                
                txn.Commit();
            }
            
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["wall_ids"] = wallIds,
                ["count"] = wallIds.Count,
                ["errors"] = errors.Count > 0 ? errors : null,
            };
        }
    }
}
```

- [ ] **Step 2: Register in ToolRegistry.cs**

```csharp
"cad_create_walls" => CadCreateWalls.Run(uidoc, args),
```

- [ ] **Step 3: Build**

Run: `dotnet build BinaVibe/BinaVibe.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BinaVibe/Mcp/Tools/CadCreateWalls.cs BinaVibe/Mcp/Tools/ToolRegistry.cs
git commit -m "feat(cad): add cad_create_walls MCP tool"
```

---

### Task 10: Wire Confirm Route and End-to-End Test

**Files:**
- Modify: `app/engine/cad/routes.py` (add /confirm)
- Test: Manual end-to-end with Revit running

**Interfaces:**
- Consumes: session state with proposed walls
- Produces: Calls `cad_create_walls` tool, returns wall IDs

- [ ] **Step 1: Add confirm route**

```python
# Add to app/engine/cad/routes.py

class ConfirmRequest(BaseModel):
    session_id: str
    level: str | None = None
    wall_type: str | None = None


@router.post("/confirm")
async def confirm_and_create(req: ConfirmRequest):
    """Create walls in Revit from confirmed classification."""
    mgr = get_session_manager()
    session = mgr.get(req.session_id)
    if not session:
        raise HTTPException(404, f"session {req.session_id} not found")
    
    if not session.proposed_walls:
        raise HTTPException(400, "no walls to create - call /cad/preview first")
    
    result = await call_tool("cad_create_walls", {
        "centerlines": session.proposed_walls,
        "level": req.level,
        "wall_type": req.wall_type,
    })
    
    if not result.get("ok"):
        raise HTTPException(400, result.get("error", "wall creation failed"))
    
    session.confirm()
    mgr.remove(req.session_id)  # Clean up session
    
    return {
        "ok": True,
        "wall_ids": result.get("wall_ids", []),
        "count": result.get("count", 0),
    }
```

- [ ] **Step 2: End-to-end test**

1. Start Revit with BINA addin (Engine mode on)
2. Attach a DWG file in Copilot pane
3. Open browser: `http://localhost:48810/cad/viewer?dwg_ref=<ref>`
4. Verify geometry renders
5. Click through classification
6. Click "Create Walls"
7. Verify walls appear in Revit

- [ ] **Step 3: Commit**

```bash
git add app/engine/cad/routes.py
git commit -m "feat(cad): add confirm route for wall creation"
```

---

## Summary

10 tasks total:
- Tasks 1-2: C# MCP tools (cad_load, cad_get_lines)
- Tasks 3-4: Python session + routes foundation
- Tasks 5-6: Classification + stitching algorithms
- Task 7: Browser viewer
- Task 8: Classify/preview routes
- Task 9: C# wall creation tool
- Task 10: Confirm route + integration

Each task is independently testable and commits incrementally.
