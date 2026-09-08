# Cad2Bim

A standalone experiment: read a DWG, work out what the linework means, and hand back building
elements — walls, openings, rooms — rather than lines. Separate from the Revit add-in on purpose;
it needs no Revit and no licence to run, so it can be iterated on and measured quickly.

Nothing here writes a BIM file yet. Getting from these classified elements to IFC or to native
Revit elements is the next piece of work, and it has not been started.

## Two projects

| | Runs on | What it is for |
|---|---|---|
| `cad2bim/` | Windows only (`net8.0-windows`, WPF) | The viewer. Drawing underneath, classified walls on top, thickness thresholds tunable while you watch. This is the tuning surface. |
| `Cad2Bim.Headless/` | Anywhere (`net8.0`) | Same classification code, no UI, prints numbers. This is the measuring surface — comparable across machines, diffable, CI-able. |

The headless project links the model sources rather than duplicating them, so both always run
exactly the same classification.

## Running it

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). On Windows pick the
x64 SDK installer; it brings the Windows Desktop runtime WPF needs.

```bash
# the numbers — works on Windows, macOS, Linux
dotnet run --project Cad2Bim.Headless -- path\to\drawing.dwg

# thresholds in millimetres: minimum and maximum wall thickness
dotnet run --project Cad2Bim.Headless -- path\to\drawing.dwg 75 300

# every text string in the drawing
dotnet run --project Cad2Bim.Headless -- path\to\drawing.dwg --texts

# the viewer — Windows only
dotnet run --project cad2bim
```

### Bring your own drawing

`*.dwg` is git-ignored here, deliberately: this repository is public and a real test drawing
carries a client's floor plans and title block. Copy your test file next to the checkout and pass
its path.

### Thresholds are millimetres

`SMin` / `SMax` are the minimum and maximum wall thickness, in **millimetres** — 50 and 400 by
default. Drawings are normalised to mm on load from the file's own unit header, so the same two
numbers hold whether the file was authored in millimetres, metres or inches. They used to be
metres, which silently produced garbage on a millimetre drawing; see `cad2bim/CHANGES-continued.md`.

## How it reads a drawing

```
DWG ─ ACadSharp ─┬─ CadRenderSource ── flattened polylines ──────── what you see
                 └─ CadLoader ─ Units ─ segments, arcs, text ─┐
                                                              │
   walls ── topology ── openings ── spaces ── indoor/outdoor ─┘
```

- **Walls** (`Geometry.cs`) — two parallel faces, 50–400 mm apart, overlapping along their shared
  direction; the nearest admissible partner wins and a face belongs to at most one wall.
- **Topology** (`Topology.cs`) — centrelines split at their crossings, coincident ends merged into
  shared nodes. Corners may overshoot or fall short by 150 mm and still meet.
- **Openings** (`Openings.cs`) — found from jambs, not from gaps: a gap in linework is
  indistinguishable from linework nobody drew. A door swing hinged at a jamb makes it a door.
- **Spaces** (`Spaces.cs`) — rooms are the bounded faces of the wall graph. Labels attach
  afterwards, smallest containing room first.

## Known state

Run against a real multi-storey plan, walls come out well and rooms do not. One cause covers
both: `CadLoader` reads only top-level `Line`, `Arc` and `TextEntity`, so polyline walls and
everything inside block references never reach classification — the renderer resolves 25,048
polylines from the same file that gives the classifier 4,076 segments. `CHANGES-continued.md` has
the numbers and what to do about it.
