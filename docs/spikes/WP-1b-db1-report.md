# WP-1b spike: why Db.1 shows "Failed: 468 of 468"

Read-only spike. No product code changed. Base: add-in `origin/ai-423-test` 51eb54e6, bina-ai `origin/ai-423-test` 699b7672 (read only).
Plan section: `JKR-copilot-new-tools-plan-2026-11-23.md` §2.6 / WP-1b. Feeds WP-8 (`bind_parameters`).

Tags: **[read]** code or evidence file read, **[arith]** computed from the exported CSVs, **[inferred]** follows from code but no run shows it, **[not verified]** needs Revit or a dump.

## 0. Answer in 8 lines

1. **468 = 61 + 407 [arith, exact].** 61 add-in Db.1 type results (PB tables) + 407 backend "Missing Type/Instance parameter" results (Doc 09 list, per element group). The 22 Nov export has Db.1 61 rows, "Missing Type" 316, "Missing Instance" 91 (`test-round-20261122/ar24_lod300.csv`): 316 + 91 + 61 = 468.
2. **Two different rule sets feed one cell.** `SpecReport.MapBackend` maps every backend rule that starts with `missing ` and contains ` parameter:` to Db.1 when the row has an element id (`UI/Jkr/Panel/Core/SpecReport.cs:308-325`). The add-in's own check (`SpecChecks.DE.cs:175`) uses the Buku Parameter CSV. They list different parameters and count in different units (per type vs per element group).
3. **The cell cannot show a pass.** Local type passes are `SpecCoverage.FailOnly`, which never adds to `Passed` (`SpecReport.cs:572-575`, `SpecChecks.DE.cs:219`). `Passed` only grows from a backend "LOD n required parameters complete" result (Full). A cell with 0 fails and no Full pass is **Partial**, and Partial is **not** a pass in the score (`SpecChecklist.cs` TallyOf: Partial counted apart). So "468 of 468" means 0 passes AND 468 fails.
4. **"Missing" is not always "not bound".** Local Db.1: name not on the type or the first instance (true "not on the type"). Backend: the scan sends only non-empty values (`JkrBuildingInfoExtractor.cs:375`, empty string returns null, so the key is absent), so the backend calls a **bound but empty** parameter "Missing". Part of the 407 is empty, not unbound [inferred; the export cannot split them].
5. **AI-516 does not touch the 468.** It dedupes board rows only (`BoardScore.cs:209-217`: "The % is the spec report's and does not read board rows"). Round b: headline unchanged at 468, as the code predicts.
6. **The legacy fixer binds only when it also has a value.** The backend emits a `fix_action` only if `_infer_param_value` returns a value (`jkr_deterministic.py:606-624`); only 8 parameters have one. Every other missing parameter (Warna_Kelulusan_PBT, Coping, Kumai, Lebar/Panjang/Tinggi, Komponen Bomba/IBS, …) has **no fix row, so nothing ever binds it**. That is the gap `bind_parameters` fills.
7. **Fix All is not a bind tool for the Copilot path and not enough alone.** It binds inside `JkrFixApplicator.PreflightSetParameter` (`JkrFixApplicator.cs:279-286`), but: needs a value, binds to one category per call, can create a *new* definition with a *new GUID* in a private file, and was never run on these rows in any round on record.
8. **Plan correction.** `ParameterWriter.TryBindSharedParameter` is the BINA Cloud sync binder (group "BINA", file `bina_shared_params.txt`, takes `BinaElementParameter`), and it is `private`. The JKR binder is `JkrFixApplicator.TryBindSharedParameter` (`:392`), also `private`. Neither can be called as is. WP-8 must extract one shared binder.

## 1. (a) What Db.1 counts

### 1.1 The add-in check (`SpecChecks.DE.cs` `DbMandatoryParameters`, `:175-221`)

- LOi from project LOD: `SpecPbData.LoiOf(m.Lod)`; 0 = nothing judged (`:178`). Test model is LOD 300, so LOi 300 (CSV "Required: filled (PB, LOi 300)").
- Scope: types in 19 Revit categories (`PbElementCategories`, `:52-56`) that map to a Buku Parameter AR page (`SpecPbParameters.BookCategoryOf`, `SpecPbParameters.cs:64`). Room and Area are judged per element (`DbRoomArea`).
- Required set per type: rows of the category ticked at that LOi, **shared only** (`IsShared`, suffix present) and **not starred** (`:187-190`). Data: `Services/JkrSpec/data/jkr_pb_ar_parameters.csv` (AI-503, 984 rows).
- Per parameter: `SpecExtractionRules.MandatoryState` (`SpecExtractionRules.cs:131-139`):
  - text value present in the snapshot: Empty (blank) or Filled;
  - not in the snapshot and the reader saw parameter names (`ParamNamesRead`): **Missing** (name absent on the type **and** on the first placed instance, `SpecModelReader.cs:794-815`);
  - carried but not text-typed (Yes/No, number, length, material, currency): **not judged** (null).
- Numerator / denominator: one `Fail` result per **type** with ≥1 empty or missing (`:214`, key `type:<typeId>`). A fully filled type gives one aggregated `Pass` per category (`:218-221`, FailOnly, count = types). Starred rows give a `Listing` (never failed).
- Name match is **ordinal** (`HashSet<string>(StringComparer.Ordinal)`, `SpecModelReader.cs:796`), by `Definition.Name`. No GUID, no binding check.

### 1.2 The backend rows that land in the same cell

- `validate_param_presence` (bina-ai `app/services/jkr_deterministic.py:561`) uses Doc 09 required lists (`get_required_params`), **not** the PB CSV. One FAIL result per missing required parameter per scanned element (`:597-625`). "Empty" parameters are WARNING (`:630-648`); "Optional … missing" is WARNING (`:652-663`). Only FAIL counts as a Db.1 failure.
- The scan sends **one element per (category, type, level)**, merging parameters across the group (`JkrBuildingInfoExtractor.cs:132-150`). Only `_jkr_` names, instance then type, **non-empty only** (`GetParamValue`, `:367-385`).
- Mapping: `SpecReport.MapBackend` (`:290`), lines `:308` (needs element id), `:324` (`missing …parameter:` or `empty …parameter:` → Db.1 Full), `:325` (`LOD n required parameters complete` → Db.1 Full). The same function sets `IssueVm.SpecCheckId` (`IssueMapper.cs:137`), so these rows are in the % and are **not** "Extra" (`IssueVm.IsExtra` needs an empty `SpecCheckId`, `IssueVm.cs:62`).
- Aggregation: `res.Failed++` per fail result (`SpecReport.cs:579`); display "Failed: {Failed} of {Passed+Failed+Unjudged} results" (`SpecChecklist.cs:903-904`). State rule `StateOf` (`SpecReport.cs:740-752`): any fail = Fail.

### 1.3 Numbers (model `jkrAR24_5a…`, LOD 300; evidence untrusted but internally consistent)

| Source | 22 Nov (pre AI-516) | 23 Nov b (board, after AI-516 r1) | In the 468? |
|---|---|---|---|
| Db.1 add-in rows (types) | 61 | 58 | 61 |
| Backend "Missing Type parameter" | 316 | 237 | 316 |
| Backend "Missing Instance parameter" | 91 | 72 | 91 |
| Total in the cell | | | **468** |
| Optional (WARNING) | 148 | 148 | 0 |

So the board lost 98 rows to AI-516 (407 to 309) and 3 Db.1 rows (61 to 58); the cell kept all 468 [arith].

Priority split of the 237 board rows: 203 "Component Parameter" (Medium) and 34 "LOD 400/500 parameter" (Low). The 34 Low rows are parameters the backend says belong to LOD 400/500, yet `MapBackend` still files them under Db.1 at LOD 300 [read: category is not consulted in the `missing …parameter:` branch; whether those results have status FAIL is **not verified**].

### 1.4 Breakdown by parameter and missing vs empty

The export has no Revit category column (Category = "Borang D"), so a (category, parameter, state) split is **not possible from the files**; see §6 for the one-run dump that gives it.

Add-in Db.1, 22 Nov (61 type rows, 393 parameter mentions; Malay "tiada" = missing, "kosong" = empty) [arith]:

| Parameter | missing | empty |
|---|---|---|
| Bidang_Kejuruteraan_jkr_stt | 56 | |
| Nama_Komponen_Utama_jkr_stt | 56 | |
| Warna_Kelulusan_PBT_jkr_sit | 21 | 3 (+5 as `_stt`) |
| LOD_jkr_sit | 11 | |
| Kaedah_Pemasangan_jkr_sit | 10 | |
| Coping / Kumai (`_stt` and `_stm`) | 8 each (32) | |
| Lebar / Panjang / Tinggi `_stl` | 8 each (24) | |
| Kod_Tinggi_jkr_jkr_stt | 7 | |
| Kod_Komponen_sumber_jkr_stt | 7 | (Sumber: 5) |
| Bahan `_stt` / `_stm` | 4 / 4 | 13 (`_stt`) |
| Kemasan_jkr_stt | 6 | |
| Kod_DAK_Komponen_jkr_stt | | 32 |
| Sistem / Sub_Sistem `_stt` | | 26 / 26 |
| Kod_Komponen_jkr_stt | | 21 |
| Kod_Jenis_jkr_stt | | 14 |
| Others (≤ 4 each) | 5 | 14 |
| **Total** | **239 missing** | **154 empty** |

Read: 61 of 61 failing types fail mainly on **missing** parameters; 112 of 393 mentions are just two parameters (Bidang, Nama_Komponen_Utama) that the project does not carry at all. Dedupe round 1 trimmed this to 167 missing + 91 empty on the board.

Backend "Missing" (22 Nov, 407): LOD_jkr_sit 59, Bidang 51, Nama_Komponen_Utama 51, Kod_DAK_Komponen 36, Kod_Komponen 27, Sistem 26, Sub_Sistem 26, Warna_Kelulusan_PBT (instance 20 + type 14), Kod_Tinggi_jkr_jkr 15, Kaedah_Pemasangan 12, Coping 18, Kumai 18, Bahan 6, Komponen Bomba 6, Komponen IBS 6, Kemasan 5+2, Lapisan_Kalis_Air 5, Kod_Komponen_Projek 2, Bingkai 1, Kod_Jenis 1.

### 1.5 Why every one fails (all 468 fail, none pass)

1. Every failing type lacks parameters the project never bound. The test model carries a **partial** JKR set (JkrFixApplicator.cs:451-455 comment, UAT 14 Jul: LOD/Bidang/Nama_Komponen_Utama absent). A type with one missing mandatory parameter is one fail.
2. The backend side adds one fail **per element group** per missing parameter, so a missing parameter on many types multiplies (Bidang: 51 backend + 56 add-in).
3. No result can be a counted pass: add-in passes are FailOnly; backend Full passes need an element with every Doc 09 required parameter present and filled, and the mandatory set includes parameters nobody fills (Kaedah, Warna_Kelulusan).
4. Two likely **false fails** that no binding can fix [inferred, needs the official shared parameter file to confirm]:
   - Case variants in the PB CSV: `Kod_Komponen_sumber_jkr_stt` (Casework, Generic Model, Planting; CSV lines 36, 459, 512) vs `Kod_Komponen_Sumber_jkr_stt` (Door, Furniture, Window, … lines 243, 347, 402, 552, 767, 939). If the model carries the capital-S name, the lowercase rows are "missing" by the ordinal compare (7 types in the export).
   - `Kod_Tinggi_jkr_jkr_stt` (Wall, Curtain System; CSV lines 185, 887): "jkr" twice, probably a book typo. Backend lists the same name (15 backend rows).
   `bind_parameters` must never create these names (§4).

## 2. (b) The legacy fixer path

### 2.1 Does it bind before it writes?

Yes, but only in one narrow case. `JkrFixApplicator.PreflightSetParameter` (`Services/JkrFixApplicator.cs:259-343`):
- `ResolveParameter` looks the name up on the element, then its type (`LookupParameter`, `:326-349`).
- Only when **both** are null (`:279`) it calls `TryBindSharedParameter` (`:392-580`), then re-resolves; if still null it undoes the bind (`:291-299`); if the resolved handle is read-only it undoes the bind (`:316-322`). Binding is not rolled back by a SubTransaction (comment `:316-321`), hence the manual undo.
- `FixNeedsIsolation` (`:90-107`) marks these fixes for a SubTransaction each.

`TryBindSharedParameter` details (all [read]):
- Opens the loaded shared parameter file (`app.OpenSharedParameterFile()`, `:405`). If none: **creates `%APPDATA%\RevitWebAppSync\jkr_shared_params.txt`, points Revit at it (`app.SharedParametersFilename = spPath`, `:437`) and never restores the user's file name** (the `ParameterWriter` twin does restore it, `ParameterWriter.cs:117-131`).
- Finds the definition by **exact ordinal name** in any group (`:455-465`). If absent and the name contains `_jkr_`: **creates a new definition** in group "JKR", type from the last suffix letter (`:490-510`): t/x/c/m-less default Text; `y` Yes/No; `i` Integer; `n` Number; `l` Length; `a` Area; `m` refused. So `_stx` (multiline) and currency `_c` become plain Text, and the new definition gets a **new GUID**, not the official JKR GUID.
- Binds to `elem.Category` **of that one element** only (`:523-577`): `_jkr_st?` Type binding, `_jkr_si?` Instance binding; if already bound elsewhere it ReInserts the category set (`:526-557`).
- Rule 24 ("JKR shared parameters loaded") is satisfied by the file **name** containing "jkr" (`jkr_deterministic.py:1127`: `any("jkr" in f.lower() …)`). The provisioned `jkr_shared_params.txt` passes it, so rule 24 does not prove the official file is loaded.

### 2.2 For which rows does it run?

Only rows with a `fix_action`. The backend sets one only if `_infer_param_value` returns a value (`jkr_deterministic.py:604-625`). Parameters with an inferable value (`:133-215`):

| Parameter | Value rule | Fix row? |
|---|---|---|
| Bidang_Kejuruteraan_jkr_stt | discipline of the Revit category (reference DB) | yes |
| Nama_Komponen_Utama_jkr_stt | Malay name of the category (+ material for walls/floors) | yes |
| Kod_DAK_Komponen_jkr_stt, Kod_Komponen_jkr_stt | first `kod_dak` of the category | yes (first code only) |
| LOD_jkr_sit | project LOD | yes |
| Kaedah_Pemasangan_jkr_sit | 7 categories only (Doors, Windows, Columns, Structural Columns, Ceilings, Roofs, Stairs) | partly |
| Bahan_jkr_stt | only if a material code is in the name | rarely |
| Kod_Jenis / Sistem / Sub_Sistem `_stt` | only when the category has exactly one valid value; else picker (AI-426) | rarely |
| everything else (Warna_Kelulusan_PBT, Coping, Kumai, Lebar/Panjang/Tinggi, Komponen Bomba/IBS, Kemasan, Lapisan_Kalis_Air, Kod_Tinggi, Kod_Komponen_Projek, Bingkai) | none | **no fix, never bound** |

Of the 407 backend results, the parameters with a value rule total about 234 (Bidang 51 + Nama 51 + Kod_DAK 34 + Kod_Komponen 27 + LOD 59 + Kaedah 12) [arith on the board rows]. That matches the "230 expected" in AI-516. The board's "Fix all 310 changes" is higher because it also counts Level, Grid, JKR-code and other Extra rows (screenshot `issues-842-fixall-310.png` shows a Level row, "Extra · outside the %"). The 310 is **not** a Db.1 number [read].

The 61 add-in Db.1 rows have **no** deterministic fix: Fix All reads "0" for Db.1 (`test-round-20261122b/SUMMARY.md:41`). They go only to "Fix via Copilot", whose Db.1 prompt says fill **empty** parameters and use `set_parameters` (`JkrCopilotPrompt.cs:84-89`). `set_parameters` skips a missing parameter with "has no parameter '…'" and does not bind (`SetParametersTool.cs:129-137`). `Eligible` also hides the Copilot button on any row that has a deterministic fix (`JkrCopilotFix.cs:109-111`, `!hasFix`).

### 2.3 Why the fixable rows do not reduce "468 of 468"

Honest status first: **no round on record ran legacy Fix All on these rows.** Round 22b only counted it ("Baiki semua 310"); round 23b only counted 310. "Does not reduce" is an expectation, not an observation. From the code:

1. **Re-check is a full re-scan, same readers.** `FixAllAsync` → `ApplyFixBatch` → `RunScanAfterFix` → `RunScanInner(isRecheck: true)` (`JkrComplianceDashboardPanel.xaml.cs:692, 1083, 1245-1256`). Both Db.1 sources read by **name**: add-in over type + first instance (`SpecModelReader.cs:794`); backend over instance + type `_jkr_` names, non-empty only. Neither looks at GUID, binding, or whether the parameter is bound to the category. A different parameter with the same name counts as present.
2. So a successful legacy fix **can** remove up to ~234 backend results (about 50% of 468), and nothing else: 173 backend results have no fix, 61 add-in rows have no fix.
3. The cell is all-or-nothing: **one** remaining failure keeps it Fail. The % moves only at 0 fails **and** one Full backend pass (§0.3). The % can stay 68% while Db.1 improves from 468 to 200 failed results.
4. Instance parameters (LOD_jkr_sit, Kaedah) are written on **one** element (the group's first); both checks judge only that element (documented choice, `SpecChecks.DE.cs:157-160`), so they re-pass, but other instances stay empty [inferred].
5. Write failures return to Manual with a reason (`ShowFailedFixes`): read-only (type-bound parameter written on instance), group members, no shared file [read, `:301-343`].

AI-515's rule that "Fixed" needs a re-pass is Copilot-only (`JkrCopilotRecheck`); legacy Fix All uses the re-scan above.

## 3. (c) Overlap with AI-516 (not redone)

- AI-516 (merged: b1f68624 PR #348; round 2 89c909bf PR #350 on the tip) dedupes **board rows**: a Db.1 row vs fixable legacy rows for the same parameter and type; fixable legacy row stays, the Db.1 row gives the parameter up (`BoardScore.cs:209-217, 244-306`). It does not read or change the spec report.
- Round b tested b1f68624 only: Db.1 rows 61 to 58, still duplicates of Bidang, Nama_Komponen_Utama, Sistem, Sub_Sistem, Coping, Kumai. Round 2 (suffix-tolerant matcher, dedupe dump) came after and is **untested**.
- Consequence for WP-8: do not touch dedupe. But the **number** "468 of 468" is a separate problem: two sources (PB vs Doc 09) counted twice in one cell. Whatever bind/fill tool lands, the cell stays Fail until both sources agree. That is a checker fix (one source or one unit), not a WP-8 job. Recommend a separate ticket (§5).

## 4. (d) Recommendation for `bind_parameters` (WP-8)

### 4.1 Is it redundant with legacy Fix All?

**Partly overlapping, mostly not redundant.**

| | Legacy Fix All | `bind_parameters` + `set_parameters` |
|---|---|---|
| Covers | ~234 backend results with a value rule | all 61 Db.1 type rows (the ones in the checklist), incl. parameters with no value rule |
| Binds parameters with no value | no (no fix row, so no bind) | yes (its purpose) |
| Needs the official shared file | no: invents definitions with new GUIDs | **must** (§4.3) |
| Fixes "empty but bound" | yes, if value known | via `set_parameters` |
| Re-check | full re-scan | `RecheckAsync(ids)` of the Db.1 row; backend rows still need a re-scan |
| Covers Db.1 spec rows | no | yes |

Overlap risk: if `bind_parameters` runs after legacy Fix All (or the reverse), the second one finds the name already present and must be a no-op, not a second binding. Tool must resolve by name first and by GUID second (§4.3).

### 4.2 Inputs / outputs

Inputs (replace the plan's `instance` bool; the suffix already decides):
- `params[]`: JKR names exactly as in the official shared parameter file.
- `categories[]`: Revit category names (≤ N, e.g. 5). No "all categories".
- `dry_run` default true. The plan's "preview in a rolled-back TransactionGroup" does **not** fit binding: `ParameterBindings.Insert/ReInsert` is a document-level call that a SubTransaction does not undo (JkrFixApplicator.cs:316-321; ParameterWriter.cs header says a group rollback reverting bindings is **unconfirmed**). The preview must be a plan (what would bind where), not an executed bind. **[not verified in Revit; WP-1-style spike needed]**

Output envelope (kind `write`): per (param, category): `bound` | `already_bound` | `extended` (ReInsert) | `refused(reason)`; binding kind (Type/Instance) and the shared parameter GUID; list of elements/types that now carry the parameter; and `needs_value` = the parameters left empty, handed to `set_parameters` (use `type_param: true` for `_stt`). Re-check: the tool should return the type ids so the pane can `RecheckAsync` the Db.1 rows.

### 4.3 What it must refuse

1. **No shared parameter file loaded, or the loaded file is not the JKR file** (match by the JKR group/GUIDs, not by "jkr" in the file name). Refuse with "Load the JKR shared parameter file (Manage > Shared Parameters)". Do **not** provision a private file as the legacy path does (`JkrFixApplicator.cs:407-445`): that creates definitions with new GUIDs which later clash with the official file (Revit rejects a same-name, different-GUID bind) [inferred].
2. **A name not found in the file.** Never `Definitions.Create` (the legacy path does, `:490-510`). This also blocks the two suspect names in §1.5 (`Kod_Tinggi_jkr_jkr_stt`, case twins).
3. A name present in the project with a **different GUID** (or a project parameter): refuse, report both, a person decides.
4. Material (`_stm`) parameters (legacy already refuses), and non-JKR names (no `_jkr_`).
5. Categories that do not accept bound parameters (`AllowsBoundParameters`, `:523`), or any category not named in the call.
6. Widening an existing binding to a category **without listing it**: ReInsert changes the project-wide binding for all elements of the category. Show category and element count in the plan.
7. Binding kind change: never flip Instance to Type (or back) for an existing binding; keep what exists (legacy rule, `:538-546`).
8. Worksharing: refuse a bind when the document is workshared and shared parameter changes are not allowed (not verified; needs the spike).

### 4.4 Values and re-check

- **Derivable** (reuse the backend rules; do not re-invent): Bidang (discipline of category), Nama_Komponen_Utama, Kod_DAK_Komponen / Kod_Komponen (**first valid code only**, so ambiguity needs `ask_user`), LOD (project LOD), Kaedah (7 categories). **Correction to plan §2.6:** Bidang, Sistem, Sub_Sistem are derived from the **Revit category** via the reference DB, not from the type-name code; Kod_Komponen is the category's `kod_dak`, not the Da.1 code (`jkr_deterministic.py:133-215`). Sistem / Sub_Sistem / Kod_Jenis with more than one valid value are a picker (AI-426), never auto.
- **Not derivable** (Warna_Kelulusan_PBT, Kaedah outside 7 categories, Coping, Kumai, Lebar/Panjang/Tinggi, Komponen Bomba/IBS, Kemasan, Lapisan_Kalis_Air, Kod_Komponen_Sumber/Projek): `ask_user` table by type, then `set_parameters` `from_table`. Plan §5 already lists this. Some of these are `_stl` length / Yes-No / material: Db.1 does not judge them when carried (not text), so binding them gives no score and adds risk. **Bind only what Db.1 judges** (text `_stt`/`_sit`/`_stx` at the project LOi) unless a person asks.
- **Re-check:** `RecheckAsync(typeIds)` reads names, so an unbound parameter re-passes as soon as it is bound and filled. Report two numbers: Db.1 add-in rows fixed, and "backend rows still open, press Re-scan". The row is "Fixed" only when both sources agree (today the add-in row can pass while backend rows still fail).

### 4.5 Gate and confirmation: what could let a write skip a question

This PR changes **no** gate or confirmation file. For WP-8 / WP-10 (to be said in those PRs):
- A `confirmed_write` that names `bind_parameters` would skip the server question (`gate.py`, `copilot_confirmed.py`). A bind has a **project-wide** blast radius (all elements of the category, document level, not undone by the group's Undo as far as known), unlike `set_parameters` on 50 ids. Do not add `bind_parameters` to the `confirmed_write` skip set until the confirm card shows category, element count and shared-parameter source, and an undo for the bind exists.
- Match scope on `(param, category)` and on the plan hash, not on element ids, or a mismatch like AI-509 (ids one per type, `types: []`) lets the card show anyway, or worse, lets a wider bind through.
- The "bind then set" two-step must not turn into one confirmed write that also runs a delete or type change; keep deletes asking.

## 5. Recommendations

1. **WP-8 scope:** bind-only tool, official file required, no create, dry-run plan, bind what Db.1 judges. Values via `set_parameters` / `from_table`. Extract one binder from `JkrFixApplicator.TryBindSharedParameter` (it is the JKR one; the `ParameterWriter` one is not) and delete the private-file provisioning for the JKR path.
2. **New ticket (checker, not WP-8): one Db.1 source and one unit.** Today PB (per type) and Doc 09 (per element group) both count. Either the backend `missing …parameter:` results stop mapping to Db.1, or the add-in's rows are the only Db.1 results and the backend rows become "Extra". Without it the cell never reaches Pass, whatever WP-8 does.
3. **New ticket: PB CSV spellings.** Normalise `Kod_Komponen_sumber`/`Sumber` and review `Kod_Tinggi_jkr_jkr_stt` against the official shared parameter file; compare names case-insensitively where Revit does (not verified). Likely removes about 7 + types of false fails.
4. **New ticket: backend scan sends empty strings** (or an `empty` flag) so "bound but empty" stops reading as "Missing" (`JkrBuildingInfoExtractor.cs:375`).
5. **Legacy fixer:** restore `SharedParametersFilename` after a provisioned run; do not create definitions (same hazard as above). Out of WP-8 unless Aiman agrees; it is JKR add-in code, not Copilot.
6. **Data to unlock the breakdown** (Tester, one scan with `BINA_JKR_DEDUPE_DUMP=1`): the dump has category, family, type, rule, element id per row (`DedupeDump.cs`). Gives (category, parameter, missing vs empty) and which legacy results have a fix, which this spike could not get from the CSVs.
7. **Spike on MrBiggie through Tester** (WP-1 style): does `ParameterBindings.Insert` roll back inside a rolled-back TransactionGroup; does Revit reject a same-name different-GUID bind; is the official JKR file's spelling of `Kod_Komponen_Sumber`/`Kod_Tinggi`.

## 6. Not verified / limits

- No Revit run and no scan on any model. Everything about Revit API behaviour (rollback of bindings, case-sensitivity of `LookupParameter`, duplicate-name binds, worksharing) is **not verified**.
- The exact split of the 407 backend results into "unbound" vs "bound but empty" is **not** in the exports (the add-in's own "missing" is exact unbound; the backend's is not).
- The 98 backend rows AI-516 removed from the board are inferred from 407 vs 309; the dump would confirm.
- Whether the 34 "LOD 400/500" rows count as FAIL in the report at LOD 300 is not verified.
- Evidence files are untrusted and were not re-run; round b tested b1f68624, not the tip.
- bina-ai read from `origin/ai-423-test` 699b7672 by `git show`; no backend tests run (docs-only PR).

## 7. Evidence list

- Add-in: `Services/JkrSpec/SpecChecks.DE.cs:175-221`, `SpecExtractionRules.cs:131`, `SpecModelReader.cs:704-705, 794`, `SpecPbParameters.cs:50-80`, `UI/Jkr/Panel/Core/SpecReport.cs:268, 290-325, 508-600, 740-752`, `SpecChecklist.cs:903-904`, `BoardScore.cs:209-306`, `IssueVm.cs:62`, `IssueMapper.cs:137`, `Services/JkrBuildingInfoExtractor.cs:50-150, 211-219, 367-385`, `Services/JkrFixApplicator.cs:259-343, 392-580`, `Services/ParameterWriter.cs:27-31, 117-131, 350-440`, `UI/JkrComplianceDashboardPanel.xaml.cs:692-760, 1245-1256`, `BinaVibe/Copilot/Tools/SetParametersTool.cs:129-137`, `UI/Jkr/ViewModels/JkrCopilotFix.cs:73, 111`, `JkrCopilotPrompt.cs:84-89`, `Services/JkrSpec/data/jkr_pb_ar_parameters.csv:36, 185, 243, 459, 512, 887`.
- bina-ai (`origin/ai-423-test` 699b7672): `app/services/jkr_deterministic.py:133-215, 561-663, 1127`.
- Evidence: `model-audit/test-round-20261123b/SUMMARY.md`, `issues-export.csv` (896 rows), `issues-842-fixall-310.png`; `test-round-20261122/ar24_lod300.csv` (997 rows), `SUMMARY.md:30-33`; `test-round-20261122b/SUMMARY.md:41, 58`; `path-to-100-2026-11-23.md` row 4.
