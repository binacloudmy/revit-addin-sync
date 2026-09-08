# Changes — the four stubs, plus units (continuing cad2bim-2)

Picks up from the working tree in `cad2bim-2.zip` (18 Aug). `ClassifyWalls`, `CadRenderSource`
and the WPF viewer are unchanged in behaviour. What was empty is now implemented, and one bug
that was quietly deciding the wall results is fixed.

---

## The bug worth reading first: units

`Wall.SMin = 0.05` / `Wall.SMax = 0.40` are metres. `test.dwg` is authored in millimetres.
Nothing converted between the two, so the pairing was looking for faces 0.05–0.4 **mm** apart —
which only matches near-coincident linework.

| | walls | segments consumed | median thickness |
|---|---|---|---|
| before (0.05 / 0.40 against a mm drawing) | 26 | 1.3% | 0.138 |
| after (50 / 400 mm, drawing normalised) | 1481 | 72.7% | 132 mm |

The 26 walls were noise, not walls. Same story on the imperial file: **0 walls** before, 8 walls
at exactly 152.4 mm (6") now.

### `Units.cs` (new)

- `FromHeader(document)` — `Header.InsUnits` to millimetres per drawing unit; null on Unitless.
- `InferScale(segments)` — fallback for unitless files: picks whichever of mm/cm/in/ft/m puts the
  drawing's diagonal closest to a real floor plan. Coarse on purpose.
- `Resolve(document, segments)` — header when stated, inference otherwise.
- `Normalize(geometry, text, scale)` — restates the loaded model in millimetres.

The drawing is scaled, not the thresholds, so every constant downstream (wall thickness, opening
width, junction tolerance, room area) means the same thing in every file. `Wall.SMin`/`SMax` now
default to `Units.DefaultMinWallThicknessMm` / `...MaxWallThicknessMm` (50 / 400).

---

## `Geometry.cs` — model changes only

| Type | Change |
|---|---|
| `Arc` | Added `StartAngle`, `EndAngle`, `SweepDegrees`, `PointAt`, `StartPoint`, `EndPoint`. A door swing is recognised by its sweep, and centre + radius alone cannot be told from a circle. `CadLoader` now fills the angles. |
| `Segment` | Added `Length`, `Midpoint`, `PointDistance`, and `Midline(a, b)` — the line between two parallel faces, clipped to where they actually overlap. Averaging the four endpoints instead would stretch a wall past its real extent whenever one face runs longer, which is the normal case at a junction. |
| `Wall` | Added `Centerline`, built in the constructor. Everything downstream works on this rather than the face pair. |
| `Opening` | The constructor took `e1, e2` and dropped them; it now stores both jambs and derives `Position` and `Width`. |
| `Space` | Added `Boundary`, `Area` via `PolygonArea` (shoelace), `Name` from a matched label, and a geometric constructor for a room found before any text is attached. |
| `CadClassifier` | Now `partial`. The four stubs are gone; each points at the file that implements it. |

---

## `Topology.cs` (new) — `CreateTopologicalPoints`

Turns the wall list into a graph, because an enclosed region is a property of how walls connect,
not of any single wall.

1. Every pair of centrelines is intersected, each segment allowed to run 150 mm past its own ends
   so a corner overshoot or a small gap still registers.
2. Each centreline is split at its crossings.
3. Coincident ends merge into shared nodes through a grid-bucketed index — a pairwise scan is
   quadratic over thousands of wall ends.

Returns `WallGraph { Nodes, Edges }`. `TopologicalPoint.Degree` reads as: 1 = loose end,
2 = corner or continuation, 3 or more = junction.

## `Openings.cs` (new) — `ClassifyOpenings`

Openings are read from the **jambs**, not from the gap. A gap in linework is indistinguishable
from linework the drafter never drew; a pair of jambs is a positive statement that something
passes through the wall here.

- A jamb: perpendicular to the centreline (±15°), 0.6–1.6× the wall thickness long, sitting on
  the wall rather than merely crossing its direction.
- Consecutive jamb pairs 400–3000 mm apart become an opening — consecutive only, so four jambs on
  a wall give two openings, not six.
- A door swing (60–120° sweep, 500–1500 mm radius) hinged **at one of the two jambs**, with a leaf
  about as wide as the clear opening, makes it a door. Proximity alone would claim the swing
  belonging to the door in the next room. No swing means window or plain opening — as far as a
  floor plan on its own can settle it.

## `Spaces.cs` (new) — `ClassifySpaces`, `SplitWalls`

Rooms are the bounded faces of the wall graph, traced by always taking the sharpest right turn at
each node. That closes the smallest loop through each edge, which is the room on that side of the
wall; the one loop that comes back clockwise is the outside of the building and is dropped. Loops
under 1.5 m² are dropped as slivers.

Labels attach afterwards: a text anchor inside a room names it, smallest containing room first,
so a drawing title over the whole plan does not name every room beneath it.

`SplitWalls` marks a wall external when fewer than two rooms border it.

---

## `Cad2Bim.Headless/` (new project, outside `cad2bim-2/`)

`net8.0`, no WPF, links the model sources. The viewer is `net8.0-windows` and cannot build off
Windows, so there was no way to compile or measure any of this on a Mac. Now:

```
dotnet run --project Cad2Bim.Headless -- test.dwg [sMinMm] [sMaxMm]
```

It prints the entity census, what reaches the classifier, then walls, topology, openings and
spaces. Same code path as the viewer, no UI — so a result is comparable across machines and
reviewable in a diff. Your project is untouched by it; it only adds `<Compile Include>` links.

---

## Where it stands — and the one thing blocking everything after this

`test.dwg`, defaults:

```
walls     1481   (72.7% of segments consumed, median 132 mm)
nodes     1530   edges 1801   junctions 427   loose ends 472
openings  1
rooms     3      9.4 m² total
```

Openings and rooms are almost empty, and the cause is the same for both:

**`CadLoader` only reads top-level `Line`, `Arc` and `TextEntity`.** In `test.dwg` that skips
**666 LwPolylines** and everything inside **568 block Inserts** — the renderer resolves 25,048
polylines from the same file against the classifier's 4,076 segments. Door blocks, and any wall
drawn as a polyline, never reach classification at all. That is why one opening was found, and
why the graph has 472 loose ends: the boundaries genuinely are not all there.

`CadRenderSource` already flattens all of it correctly — blocks, transforms, bulges, splines.
Feeding classification from that same flattening is the next change, and it is a decision about
your architecture (the two paths are deliberately separate today), so it is left for you rather
than made unilaterally. One wrinkle to settle when you do: flattening tessellates arcs into short
chords, so the segment source needs to keep arcs identifiable or door swings dissolve into
linework.

Second-order, after that: `ClassifyWalls` pairs any two parallel overlapping lines within range,
so furniture, hatch boundaries and jamb lines all become "walls". 1481 is an over-count. A second
pass — reject pairs whose faces do not run alongside each other for most of their length, prefer
longer runs — would cut it. Worth measuring against a drawing where the true wall count is known.
