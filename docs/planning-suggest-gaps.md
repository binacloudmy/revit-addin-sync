# `/planning/suggest` — capability gaps

Findings from add-in integration testing, 30–31 Jul 2026. Every item below was
measured against the live endpoint, not inferred. The add-in side is data-driven:
it renders and builds whatever the response contains, so **everything here is
backend work** unless stated otherwise.

Ordered by how much they limit real use.

---

## 1. The generator tops out at ~3,530 m² — it cannot serve a large school

Only 1- and 2-storey layouts are produced, from three archetypes (*Dua Blok
Selari*, *Sisir*, *Courtyard*). Footprint × 2 storeys is the ceiling.

| brief | classrooms | target GFA | schemes returned |
|---|---|---|---|
| `Tahun 1-6 with 5 kelas each` | 30 | 3,150 | **2** |
| `Tahun 1-6 with 6 kelas each` | 36 | 3,538.8 | **0** |
| `Tahun 1-6 with 8 kelas each` | 48 | 4,316.4 | **0** |
| `Tahun 1-6 with 14 kelas each` | 84 | 6,649.2 | **0** |

The last row is SK Cyberjaya's scale (~3,200–3,500 pupils, 14 classes in Year 1
alone) — roughly **2× beyond** what the generator can lay out. Site area does not
help: the same briefs with `tapak 3000` and `tapak 20000` return identical
schemes, so site is echoed but never used for fitting.

**Ask:** 3–4 storey variants of the existing archetypes. That alone lifts the
ceiling to ~7,000 m² and covers the schools that most need planning help.

---

## 2. Only `bilik_darjah` scales — support rooms are constant

Same three briefs as above:

| SOA row | 18 classrooms | 30 classrooms | 84 classrooms |
|---|---|---|---|
| Bilik Darjah | 18 | 30 | 84 |
| Bilik Sokongan | 8 | 8 | **8** |
| Blok Tandas | 4 | 4 | **4** |
| Dewan Perhimpunan | 1 | 1 | **1** |
| Kantin | 1 | 1 | **1** |

This is a correctness problem, not just a limitation: sanitary **fixture** counts
*do* scale (84 classrooms → 51 female WCs, 51 urinals, 63 basins), but the
**toilet block** count stays at 4. Fifty-one WCs cannot fit in four 64.8 m²
blocks. The fixture maths and the room maths disagree, and only the fixtures look
right.

**Ask:** scale support/toilet/hall/canteen counts with enrolment, consistent with
the sanitary calculation already implemented.

---

## 3. Unparsed briefs silently return the default school

No error, no flag — an 18-classroom `sekolah rendah` SOA comes back regardless.

| brief | result |
|---|---|
| `international school, Early Years to Sixth Form, ages 3-18, 500 students, science lab, ICT lab, library, art studio, gym, swimming pool, surau` | **default 18 classrooms / 2,372.4 m²** |
| `hospital daerah 200 katil, wad, dewan bedah, ICU` | **default 18 classrooms / 2,372.4 m²** |
| `sekolah menengah, Tingkatan 1-5 with 6 kelas each` | **`target_gfa_m2: 0`, no `bilik_darjah` row** |

The first two are the dangerous ones: the pane renders a confident, fully-cited
SOA that has nothing to do with the brief. A user would have to already know the
right answer to notice.

**Ask:** return `success: false` with a reason when the brief cannot be
classified, rather than falling back to a default.

---

## 4. English phrasing breaks the per-year multiplication

| brief | classrooms |
|---|---|
| `sekolah rendah, Tahun 1-6 with 2 kelas each` | **12** ✅ |
| `international primary school, Year 1-6 with 2 classes each` | **2** ❌ |

Same structure, different language, 6× difference. `Year N-M` / `classes each`
are not treated as equivalent to `Tahun N-M` / `kelas each`.

---

## 5. Room vocabulary is fixed at six types

`bilik_darjah, bilik_sokongan, tandas, perhimpunan, kantin, padang`.

No lab, library, surau, gym, pool, workshop, staff room. Any brief naming them
gets them silently dropped — see §3.

**Add-in readiness:** new `type` values need **no** add-in change to draw or
build; they render with a borrowed legend colour until a palette entry is added,
and a test fails the moment an unrecognised type appears, so it will not slip
through unnoticed. Sending new types is safe.

---

## 6. Confirmed working — no action needed

Recorded so these are not re-litigated:

- `pending_tool_calls` in the `awaiting_revit` terminal SSE frame — parsed correctly
- `event: "tool"` frames treated as progress only
- `X-Tenant-Id` requirement — documented and sent
- Empty brief → `400 "Provide brief or needs."`
- `site_area_m2` / `setback_m` — echoed from the request *and* parsed from brief
  text (`tapak 9000 m2, setback 8 m`); note `site_width_m` / `site_depth_m` do
  **not** feed them
- `levels` array on every SOA row
- `warnings` firing on over-provisioned schemes (>25% over target)
- Schemes that fail the target appearing in `rejected[]`, never in `schemes[]`

---

## Add-in-side work already done for this

- **Origin offset** (`offset_x_mm` / `offset_y_mm` / `auto_offset`) on
  `place_massing_scheme`. Every response places rooms from the same origin
  (x=6 m, y=6 m), so two Builds landed exactly on top of each other. Repeated
  Builds now step clear of each other, which is the only way to model a
  programme above the §1 ceiling today — several briefs placed side by side.
- **Zero-scheme empty state** now explains the §1 ceiling with real figures
  instead of suggesting a larger site, which cannot help.
