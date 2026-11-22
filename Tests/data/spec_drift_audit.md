# Spec ↔ code drift audit (AI-469)

Spec: `JKR-Model-Audit-Spec.xlsx`, sheet "Peraturan comply" (61 rows, `spec_rows.json`) and
`model-audit-spec.md` (Semakan 2). Code: `Services/JkrSpec/SpecChecks.{A,BC,DE}.cs` at
`ai-423-test` (ce359782). Line numbers are of that tree plus this PR.
Checked per row: threshold, operator (< vs ≤), rounding / units, "not applicable" handling,
and whether the row's class (Ai / Human / List) matches what the code returns.

Verdicts: **MATCH** · **MATCH (rounds)** (judged in whole mm, see Q1) · **PARTIAL** (the spec
row is judged only in part, by design: backend data or the snapshot lacks it) · **EXT** (the code
adds a rule the spec does not state → question) · **DRIFT → fixed** (this PR) · **LIST/HUMAN ok**
(never a ✓/✗, as the spec says).

Boundary tests (just-under / on / just-over): `Tests/SpecThresholdBoundaryTests.cs`.

## Result

61 rows read. **1 clear drift fixed** (Db.1 printed the raw LOD as the LOi column). **0 operator
or threshold drifts** in the numeric rules: every `≥` in the spec is `≥` in the code, at the spec's
number. **17 questions** (below) where the spec is silent or ambiguous; none was guessed.
Class (Ai / Human / List) of all 61 rows matches `SpecCatalog` (AI-467's conformance test) and the
code returns no verdict for any Human / List row.

## AI-504: invented numbers removed (22 Nov 2026)

No JKR document gives the numbers below (JKR-spec-answers-from-documents, "Documents cannot answer"). They
are gone from the code, the remarks and the spec sheet. Where a check has a documented part it stays
automatic; the undocumented judgement is a Context result with an issue: a row for a person (Human in the
pane), listed, counted as passed in the % (AI-474), never a fail, never offered Fix via Copilot or Fix All
(`SpecPersonDecides`). Class counts are unchanged: 39 AI, 8 Human, 14 List. **AI + Human part** (class stays Ai):
A1.1, A1.2, A1.3, A1.4, A1.5, A4.1, A6.2, C4.1.2 (`SpecPersonDecides.AiWithHumanPart`). Dc.1 and A3.1 stay plain AI.
No row moved AI → Human.

| Item | Was | Now |
|---|---|---|
| Q1 A1.1 / A1.2 / A1.3 / A1.5 | fail below min − 0.5 mm (0.05 for A1.3) | exact minimum; under it but prints as it → listed |
| Q2 A4.1 | ≤ 10 mm passes, more fails | exactly 0 passes, any other offset listed |
| Q4 A1.4 | flat = < 2°, or named flat and < 10° | no flat test; RC roof without waterproofing listed |
| Q5 A6.2 | ≥ 50 % covered, pads < 1 m² skipped, 1500 mm slack | a room on the slab complies, no room → listed |
| Q8 A1.2 | double leaf (1500 mm) read from the name | name-double under 1500 mm listed; 900 / 2100 judged for every door |
| Q11 Dc.1 | ±1 mm | exact (float noise from feet → mm removed, a conversion fix) |
| Q16 A3.1 | remark on link count / part-like links | no remark |
| Spec #5 "5 meter" | not in code or catalog (checked) | guarded by a test |
| Spec #10 | a sheet named Butiran Pintu counts as the door detail | listed, not counted |
| Spec #12 | PT-A × PT1 read as Grid A/1 | listed, not read as A/1 |

Remaining numbers that are NOT JKR rules and stay, named so: A1.1 geometry reading (`A11PadM2` 1 m², `A11StorySlackMm`
1500 mm, `A11MinFloorShare` 10 %) decides which floor a ceiling hangs over; it is not a verdict number.

## Row table

| Spec row | Spec says | Code (file:line) | Verdict |
|---|---|---|---|
| A1.1 | every level ≥ 3000 mm; basement ≥ 2500; fail < | `A11CeilingHeight`: minimum compared exactly (`SpecAKit.FitMin`); a value under it that prints as the minimum (2999.6) is a Context row for a person, never a fail (AI-504, Q1). No ceiling → unjudged | MATCH (exact); AI + Human part |
| A1.2 | single ≥ 900, double ≥ 1500, clear height ≥ 2100 | `A12DoorSize`; fails nominal width < 900 or height < 2100 (exact, FailOnly); a door named double and < 1500 wide, or a value that only prints as the minimum, is a Context row (AI-504, Q1, Q8) | MATCH (exact); PARTIAL Q8; AI + Human part |
| A1.3 | main door solid core, thickness ≥ 40, parameter filled | `A13MainDoor`; thickness < 40 fails exactly; one that prints as 40 (39.96) is a Context row (AI-504, Q1); missing parameter fails; no main door → candidates to a person | MATCH (exact); AI + Human part |
| A1.4 | no RC flat roof, or flat + waterproofing | `A14FlatRoof`; no flat-roof test (the 2° / 10° and the "rata" name are gone, AI-504, Q4). An RC roof with a waterproofing layer complies at any slope; an RC roof without one, or with no materials read, is a Context row; never a fail | MATCH for the documented part; AI + Human part |
| A1.5 | decorative fence ≥ 2400 | `A15Fence`; exact; a height that prints as 2400 is a Context row (AI-504, Q1); no fence → person | MATCH (exact); AI + Human part |
| A1.6 | List | `A.cs:467` listing only | LIST ok |
| A1.7 | List | `A.cs:475` the spec's remark, listing | LIST ok |
| A2.1 | ≥ 3 segments | `A.cs:491`; `segs.Count >= 3` (`:510`), 2 fails; also fails a name not opening with the JKR block (`:503`) | MATCH; extra rule Q7 |
| A2.2 | W00 / WXnn found | `A.cs:525`; unreadable → fail | MATCH (code format open: spec H1) |
| A3.1 | W00 → comply | `A31Single`; split → not applicable; unreadable → person; no link-count remark (AI-504, Q16) | MATCH |
| A3.2 | every part linked | `A.cs:614`; W00 → not applicable; missing part / NotFound link fail; FailOnly (parts above the highest seen need the BPEP) | MATCH; PARTIAL |
| A3.3 | BPEP file list found as links | `A.cs:645`; backend only (BPEP) → unjudged; W00 → not applicable | PARTIAL (backend) |
| A3.4 | List | `A.cs:656` | LIST ok |
| A3.5 | List | `A.cs:669` | LIST ok |
| A4.1 | Project Base Point at Grid A/1 (0 mm); show the offset | `A41BasePoint`; exact (`SpecUnits.CleanMm` removes feet → mm noise only): 0 offset passes; any other offset, and a prefixed pair (PT-A × PT1) with no plain A / 1, is a Context row; never a fail (AI-504, Q2, spec #12). The BIM010 checker (`AuditCheckers.BasePoint`) follows | MATCH (exact); AI + Human part |
| A5.1 | List | `A.cs:781` | LIST ok |
| A5.2 | no jump in 1,2,3… / A,B,C…, either direction | `A.cs:812`; I and O skipped (H→J not a jump) | MATCH; letters I / O Q3 |
| A5.3 | valid sub-grid pattern; never a fail | `A.cs:886`; never fails, gaps go to a person | MATCH |
| A6.1 | List | `A.cs:957` | LIST ok |
| A6.2 | every slab area has a room (lift, stair, apron) | `A62SlabHasRoom`; a slab with a placed room on it complies at any share; a slab with no room is a Context row, however small; no 50 % / 1 m² / 1500 mm (AI-504, Q5). A room stands on a slab on the same level, or a stair / lift room whose height holds the slab top | MATCH in kind; AI + Human part |
| A6.3 | Human | `A.cs:1144` Context, never a fail | HUMAN ok |
| A7.1 | every element in every category has a material | `A.cs:1175`; per type, not per instance; non-physical categories skipped (`SpecA.NoMaterialCategories`) | MATCH; scope Q6 |
| A7.2 | Human | `A.cs:1212` warnings are Context | HUMAN ok |
| A7.3 | Human | `A.cs:1245` Context | HUMAN ok |
| B1.1 | every field mandatory at the project LOi (PB p.30–34) filled; fail empty or Revit default | `BC.cs:368`; `LoiOf` (`SpecPbData.cs:547`), `RequiredAt` is `from ≤ loi ≤ to` (`:32`); LOD < 100 → fields at every LOi, FailOnly | MATCH; LOD mapping Q9 |
| B1.2 | one "Tidak patuh" row per discipline + unfilled fields | `BC.cs:397`; AR / CD → not applicable | MATCH |
| B1.3 | List | `BC.cs:427`; percent rounded half-up | LIST ok |
| C1.0.1 | List | `BC.cs:442` | LIST ok |
| C1.1.1 | every JKR AR template view exists | `BC.cs` C1_1_1; judged locally against the embedded AR27 list (67 `jkrAR_` view templates, `Services/JkrSpec/data/jkr_ar_template_views.csv`); one aggregate row; missing + unmatched project templates → Context (person), missing + none unmatched → fail; unread/zero → unjudged | MATCH (AI-482) |
| C1.1.2 | template views keep their names | `BC.cs` C1_1_2; case/space/punctuation rename → one aggregate `warning` (scored pass, D3: no JKR document says a rename is wrong); other renames cannot be seen | MATCH (AI-482); rule from D3 |
| C1.1.3 | Human | `BC.cs:554` | HUMAN ok |
| C1.2.1 | List | `BC.cs:591` | LIST ok |
| C1.3.1 | floor plan, elevation, section all present | `BC.cs:647` via SetChecklist (`:626`), one fail per missing kind | MATCH (H3: fail or list is open) Q17 |
| C1.4.1 | the three kinds in the BOMBA set | `BC.cs:651`: floor plan, section, drafting view (spec md C 1.4) | MATCH Q17 |
| C1.5.1 | List | `BC.cs:658` | LIST ok |
| C1.5.2 | a 3D view per level in the Dokumen set | `BC.cs:677`; every Building Story level; a 3D view outside the set does not count | MATCH; level set Q14 |
| C1.6.1 | room schedule exists | `BC.cs:755` | MATCH |
| C1.6.2 | every room is in the schedule | `BC.cs:773`; placed rooms (`Area > 0`) | MATCH |
| C1.6.3 | every mandatory column filled per room | `BC.cs:817`; schedule fields + PB p.37 at the LOi; FailOnly when LOi unknown or Areas exist | MATCH; PARTIAL; LOD mapping Q9 |
| C2.1.1 | at least one legend | `BC.cs:882`; 0 fails, ≥ 1 passes | MATCH |
| C3.1.1 | every modelled PB AR category has a schedule | `BC.cs:943`; multi-category schedule / material takeoff counts (spec H14 open) | MATCH; Q15 |
| C3.1.2 | List | `BC.cs:982` | LIST ok |
| C4.1.1 | List | `BC.cs:1010` | LIST ok |
| C4.1.2 | the six kinds across all sheets | `C4_1_2`; a sheet named for door / window details is a Context row, not counted (AI-504, spec #10); a Detail / Drafting view named pintu / door still counts | MATCH; AI + Human part |
| C4.1.3 | every sheet has ≥ 1 view | `BC.cs:1092` | MATCH |
| C4.2.1 | title block is a JKR one | `BC.cs:1108`; name holds "JKR", case-insensitive | MATCH |
| C4.2.2 | project title, drawing title, modelled by, timestamp filled | `BC.cs:1172`; Revit placeholders ("Author", "Owner" …) also count as empty | MATCH; extension Q12 |
| C4.2.3 | Human | `BC.cs:1210` | HUMAN ok |
| C5.1.1 | ST, ME, EL linked and Loaded | `BC.cs:1258`; own discipline not asked; no link or not Loaded fails | MATCH |
| C5.1.2 | link names match the BPEP | `BC.cs:1292`; backend only → unjudged | PARTIAL (backend) |
| C5.1.3 | Human | `BC.cs:1301` | HUMAN ok |
| Da.1 | type name follows the JKR code pattern | `DE.cs:113`; pattern only, not the code list (backend); family-name code accepted | MATCH; PARTIAL (FailOnly) |
| Db.1 | every parameter mandatory at the LOi for the category filled | `DE.cs:175`; only the 12 parameters common to every category, from LOD 400 (`:178`); rest is backend. Message printed the raw LOD as "LOi" | **DRIFT → fixed** (label); PARTIAL Q10 |
| Db.2 | material filled and JKR-coded | `DE.cs:213`; empty fails, name not `^jkr[A-Z]{2}` fails (`:202`) | MATCH; PARTIAL (code list is backend) |
| Dc.1 | number in the name = parameter value | `DcNameNumbers`; compared exactly (`SpecUnits.SameMm`: float noise only); no ±1 mm (AI-504, Q11). Coverage stays as AI-475 set it (Full when every name was read) | MATCH (exact) |
| Dd.1 | Revit category matches the category the name implies | `DE.cs:447`; mismatches only; names with no code or keyword not judged | MATCH; PARTIAL (FailOnly) |
| Dd.2 | Human | `DE.cs:473` | HUMAN ok |
| De.1 | List | `DE.cs:481` | LIST ok |
| De.2 | Human | `DE.cs:499` Context | HUMAN ok |
| De.3 | List | `DE.cs:537` | LIST ok |
| E.1 | each BOMBA / PBT / Dokumen view uses its set's template | `DE.cs:569`; legends skipped, WIP listed (spec H7 open), view in two sets unjudged | MATCH; Q13 |

"Not applicable" handling, row by row: A3.1 (split), A3.2–A3.5 (W00) and B1.2 (AR / CD) return
`NotApplicable`; the spec text for them says so (A3.1 "W00 → TERUS COMPLY", B1.2 "setiap
disiplin"). A1.x, A7.1, C1.5.2 and every Section D row return *unjudged*, never a pass, when the
snapshot has nothing to judge (no doors, no ceilings, no levels, no types) — the spec's own
rule that missing data is not a pass (principle 0.4).

## Questions for the spec owner (not guessed)

1. **Q1 Rounding.** A1.1 / A1.2 / A1.5 fail only when more than 0.5 mm under the minimum (the
   notes print whole mm: 2999.6 shows "3000 mm"); A1.3 allows 0.05 mm (prints one decimal). Should
   a sub-millimetre shortfall (2999.6 mm) pass, as now, or fail?
   **AI-504:** no JKR rounding rule; exact minimum, a rounding-only shortfall is listed for a person.
2. **Q2 A4.1.** The spec says "= 0 mm (dalam toleransi)" and gives no tolerance. The code uses
   10 mm (the BIM010 base-point tolerance, inclusive). Which number? The spec also asks for the X/Y/Z
   deviation; the code shows X and Y (grids have no Z). Is Z needed?
   **AI-504:** JKR gives no tolerance; exact position, any other offset is listed.
3. **Q3 A5.2.** Letters I and O are skipped (H→J is not a jump). The spec says "A,B,C…" with
   no exception. Keep the skip?
4. **Q4 A1.4.** What counts as a flat roof? The code: slope < 2°, or a roof named flat with
   a fall < 10°. The spec has no angle.
   **AI-504:** JKR gives no flat-roof definition; the code no longer decides it.
5. **Q5 A6.2.** The spec says a slab area needs a room. The code: a slab is covered when ≥ 50 % of its
   area is under rooms; pads under 1 m² are not slabs; a room counts when its base is within
   1500 mm of the slab top. Which share, pad size and slack does JKR mean?
   **AI-504:** no numbers in any JKR document; principle only (a room on the slab), the rest listed.
6. **Q6 A7.1.** The spec says every element. The code judges per type, and skips non-physical
   categories. PR #303 (AI-419) adds a per-instance rule for Generic Models only; for doors and
   windows, does a material set only in the type satisfy A7.1?
7. **Q7 A2.1.** Beyond "≥ 3 segments", the code also fails a name that does not open with the
   JKR block (`jkrAR24…`, e.g. "Copy of jkrAR24_…"). Is that part of the guideline?
8. **Q8 A1.2.** The NS minimum is the clear opening; the snapshot has the nominal size, so the
   check can only fail (never fully pass). Double leaf is read from the type name. Acceptable?
   **AI-504:** the name is no source for a double leaf; listed for a person.
9. **Q9 LOD → LOi.** B1.1, C1.6.3: LOD 350 uses the LOi 300 column (rounds down); LOD below 100 or
   unknown uses only the fields marked at every LOi. The spec says "ikut LOD projek" and
   does not map LODs between columns.
10. **Q10 Db.1.** The spec wants every mandatory parameter per category at the project LOi. The add-in
    judges only the 12 parameters common to all categories from LOD 400; the rest need the backend's
    per-category table. Is the backend table the intended home?
11. **Q11 Dc.1.** The code allows ±1 mm between the name's number and the parameter. Spec: "sama".
    Window names `W x H` or `H x W` (spec H11, open)?
    **AI-504:** exact; W x H order stays (PB p.51-52).
12. **Q12 C4.2.2.** A Revit placeholder ("Author", "Owner", "Project Name") counts as an empty
    cell. The spec says "kosong". Keep?
13. **Q13 E.1.** Legends are skipped (Revit gives them no template); WIP is listed, not judged
    (spec H7, open). Confirm both.
14. **Q14 C1.5.2.** "Setiap aras": the code counts every level ticked Building Story (beam and
    roof levels too). Should structural / beam levels be left out?
15. **Q15 already open in the spec (H10, H14).** C4.1.2 accepts a "Butiran Pintu" sheet as the door
    detail; C3.1.1 accepts a multi-category schedule / material takeoff as a category's schedule.
16. **Q16 A3.1 remark.** W00 with more than 3 non-discipline links, or a part-like link, gets a
    remark (spec H2, open).
    **AI-504:** deleted; there is no remark.
17. **Q17 C1.3.1 / C1.4.1.** Missing section / drafting view is a ✗ in the code; the spec's
    own H3 asks whether it is ✗ or only listed.

## Legacy BM remark templates (BIM010 checker)

"BM" here is the language: the legacy checker (`BinaVibe/Mcp/Tools/Audit/AuditCheckers.cs`,
`AuditTools.cs`) writes every remark in Bahasa Melayu. No remark carries a BM-prefixed rule
code (searched the whole tree for `BM-` / `BM_` / `BM<n>` codes: none). I read "still prints legacy
BM after the rename" as "still prints BM text in the English pane".

> **AI-513 (22 Nov 2026): all 40 templates below now read English at source** (`AuditCheckers.cs`, `AuditTools.cs`,
> `AuditTemplateAvailability.cs`; "Rule: … Found … — compliant"). `PaneText.AuditRemarkEn` and its regex table are gone,
> `LegacyRemarkEnglishTests` guards the English templates, and the "BM" / "EN" columns and quoted remarks below are the
> state before AI-513, kept as the AI-469 record. The cells of `jkr_spec_rules.tsv`, `jkr_spec_items.tsv`,
> `jkr_spec_ns_clauses.tsv` and `spec_rows.json` are the English text of the spec sheet now (the workbook itself stays Malay).

Path of a remark: `AuditRecord.Remark` → (a) `IssueMapper.FromAuditRecords` makes an Issues-list row
for every record that is not a pass, not a heading, not not-applicable, with `Description = Remark`;
the pane shows it through `PaneText.AuditRemark`, which had English for the "no automatic check"
remark only. (b) `SpecReport.FromAuditRecords` → Checklist note; dropped when a spec evaluator
answered the same check (`Outranked`). **On `ai-423-test` path (a) is unfiltered, so every non-pass
remark below reaches the English pane in BM.** PR #303 (AI-412, `IssueMapper.FromAuditRecordsNotInSpec`)
drops (a) for rows a spec verdict covers; after it, the remarks that still arrive are those of the
checkers `SpecReport.MapAudit` maps to no spec check (`views_template`, `area_plans`,
`schedules_required`), plus "nothing to check", "checker failed", and every checker when the
evaluators could not run (snapshot failed).

"yes" remarks (marked **pass**) never reach the Issues list. EN = English text now exists.
Fix owner: **this PR** translated the five rows marked ✔ (`PaneText.LegacyRuleRemarkEn`, tested in
`Tests/LegacyRemarkEnglishTests.cs`); the dropping of covered rows is **#303**; the rest are listed
for #303's Round 2 ("remaining legacy remarks without English").

| # | `AuditCheckers.cs` line | Checker → spec check | Remark (outcome) | Reaches pane today | After #303 | EN |
|---|---|---|---|---|---|---|
| 1 | 271 | any → NothingToCheck | "Tiada {population} dalam model … semak manual" (not_verifiable) | yes | yes — only the graphical-views / `views_template` variant | ✔ views; others BM |
| 2 | 304 | file_naming → A2.1 | format found, patuh (pass) | no | no | — |
| 3 | 312 | file_naming → A2.1 | format wrong + suggested name (no) | yes | no | BM |
| 4 | 371 | base_point → A4.1 | grid A/1 not found, curved (not_verifiable) | yes | no | BM |
| 5 | 391 | base_point → A4.1 | grids parallel (not_verifiable) | yes | no | BM |
| 6 | 402 | base_point → A4.1 | within / over 10 mm (yes / no) | no / yes | no | BM |
| 7 | 505 | grids_levels → A5.1 (list) | grids / levels problems (yes / no) | no / yes | no | BM |
| 8 | 535 | rooms_department → A6.3 | 0 placed rooms (no) | yes | no | BM |
| 9 | 541 | rooms_department → A6.3 | Department filled / missing (yes / no) | no / yes | no | BM |
| 10 | 608 | materials_assigned → A7.1 | scan cap reached (not_verifiable) | yes | no | BM |
| 11 | 615 | materials_assigned → A7.1 | all / missing material (yes / no) | no / yes | no | BM |
| 12 | 632 | project_info → B1.1 | no Project Information element | yes | no | BM |
| 13 | 677 | project_info → B1.1 | six fields filled / not (yes / no) | no / yes | no | BM |
| 14 | 702 | views_template → none | all have template (pass) | no | no | — |
| 15 | 702 | views_template → none | n/m views no template (+ unactionable clause, full-list note) (no) | yes | **yes** | ✔ |
| 16 | 702 | views_template → none | no template of that view type exists (not_verifiable) | yes | **yes** | ✔ |
| 17 | 754 | views_wip / pbt / bomba / dokumen → C1.2–C1.5 | view with token found / not (yes / no) | no / yes | no | BM |
| 18 | 774 | area_plans → none | Area Plan found (pass) | no | no | — |
| 19 | 774 | area_plans → none | 0 Area Plan (no) | yes | **yes** | ✔ |
| 20 | 794 | legends → C2.1.1 | legend found / 0 (yes / no) | no / yes | no | BM |
| 21 | 826 | schedules_required → none | three required schedules found (pass) | no | no | — |
| 22 | 826 | schedules_required → none | names missing (no) | yes | **yes** | ✔ |
| 23 | 867 | sheets_contents → C4.1.3 | 0 sheets (no) | yes | no | BM |
| 24 | 873 | sheets_contents → C4.1.3 | empty sheets / all filled (no / yes) | yes / no | no | BM |
| 25 | 911 | titleblock_jkr → C4.2.1 | 0 title blocks (no) | yes | no | BM |
| 26 | 917 | titleblock_jkr → C4.2.1 | title blocks match / not (yes / no) | no / yes | no | BM |
| 27 | 946 | links_current → C5.1.1 | 0 links (not_verifiable) | yes | no | BM |
| 28 | 960 | links_current → C5.1.1 | all Loaded / n not Loaded (yes / no) | no / yes | no | BM |
| 29 | 978 | view_template_applied → E.1 | row names no set (not_verifiable) | yes | no | BM |
| 30 | 1002 | view_template_applied → E.1 | no view with the token (no) | yes | no | BM |
| 31 | 1008 | view_template_applied → E.1 | template applied / not / no template of type (3 variants) | partly | no | BM |
| 32–35 | 1135, 1143, 1155, 1170 | Section D categories → Da.1 / De.2 / De.3 | 0 elements expected / not expected; 0 types; no used type (not_verifiable) | yes | no | BM |
| 36 | 1186 | Section D categories → Da.1 | n types follow / not (yes / no) | no / yes | no | BM |
| 37–40 | 1259, 1268, 1278, 1288 | Section D Room → De.1 (list) | 0 rooms placed / not expected / parameters missing / all complete | yes (not pass) | no | BM |
| 41 | `AuditTools.cs:143` | any → checker crashed | "Semakan automatik gagal (id)" (not_verifiable) | yes | **yes** | ✔ |
| 42 | `AuditTools.cs:109/115` | unmatched row → none | section heading / "Tiada semakan automatik" | heading no (not an issue); no-check yes | yes | no-check ✔ already; heading not an issue |

That is 40 templates (the "~40" of PR #303): 37 `Remark =` sites in `AuditCheckers.cs` (many with a yes / no
pair, hence 36 outcome rows above) and the four in `AuditTools.cs`. Of them, **only rows 1 (views),
15, 16, 19, 22 and 41 can still reach the pane after #303 — all now have English.** The others
(rows 3–13, 17, 20, 23–40) reach the English pane in BM today and stop reaching it once #303 drops
the covered rows; if #303 is delayed, translating them is its Round 2. They also reach the
Checklist note, in BM, when the evaluators could not answer the check.
