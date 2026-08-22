# Cost to BIM — Diagnostic & Improvement Plan (v2, corrected)

**Status:** Review · **Date:** 2026-08
**Revision note:** v1 claimed the AI pipeline was "built and working." That was checked against **code only**.
v2 is corrected after verifying the **deployed data + config**. The v1 "✅ working" claim was **false** — see P0.

All findings below were **verified directly against the code, the `.env*` files, and the live dev + staging Postgres**.

---

## 0. THE BOTTOM LINE (what changed vs v1)

**The AI cost pipeline (vector matching / N3C pricing / learned mappings / review queue) is dead in every
environment — it has never run against real data.** The offline lane (JKR parse → seed → master DB) and
`/cost/analyze` (hardcoded Malaysian benchmarks) DO work. Everything else is unbacked by data.

### Verified evidence chain

| # | Claim | Verification |
|---|---|---|
| 1 | `COST_DB_URL` is required but set nowhere | `app/tools/cost_tools.py:27-29` → `raise RuntimeError("COST_DB_URL not set")`. Grepped all `.env*` (base, sample, dev, staging, prod): **no** `COST_DB_URL`. `embed_costs.py:33-37` requires it too. |
| 2 | So `/cost/*` DB routes error on every backend | `cost_matching.py` (layers 1-3) gets the engine from `cost_tools._get_engine()` → raises. `match-pipeline`/`vector-match` cannot respond. |
| 3 | Staging table is empty AND lacks the embedding column | **Verified live:** `ai.n3c_material_costs` = **0 rows**, `ai.learned_mappings` = **0 rows**, `has embedding col: False` on `bina-ai-stg.postgres.database.azure.com`. |
| 4 | Dev table is empty AND lacks the embedding column | **Verified live:** same — `n3c count: 0`, `learned: 0`, no `embedding` column on `bina_ai` localhost:5433. |
| 5 | Schema expected a manual import + column add that never happened | `docker/init/01-extensions.sql:7`: *"N3C CIDB Material Costs (without embedding column - **added after import**)"* — the import never ran. |
| 6 | The "53K N3C CIDB records" is not grounded anywhere | **No ingest script** (only `embed_costs.py`, which embeds *existing* rows), **no CSV** anywhere in repo, **no "53K" reference**. The figure exists only in `cost_analyst.py:31` + docstring copy — aspirational. |
| 7 | The 4-layer pipeline's fallback ("match rate collapses when server down") understates reality | Match rate is **~0 for non-JKR-named items even when the server is UP**, because the DB it queries is empty. |

**Correction to the v1 plan:** every Phase-0 item (unit guard, volume takeoff, code validation) is **moot
until the pipeline can return a single N3C match**. There is a **new P0: provision + wire the cost database.**

---

## 1. What "Cost to BIM" is today (the data flow)

An element's cost is `Quantity × UnitPrice`, summed (`Models/CostItem.cs:20`, `Services/CostCalculator.cs:22`).

| Input | Where it comes from | File:line |
|---|---|---|
| Quantity | `RevitModelWalker.GetAllItems()` — m² / m / unit by category | Services/RevitModelWalker.cs:187-213 |
| UnitPrice | 4-tier waterfall: project DB → master DB → AI pipeline → manual | UI/CostDashboardPanel.xaml.cs:723 |
| Live update | `DocumentChanged`, 2s debounce → refresh + banner | Events/CostUpdateHandler.cs:69-165 |

**What genuinely works deployed (verified):**
- ✅ Live cost engine (per-level/category, coverage bar, LIVE banner + RM delta) — addin-only, offline.
- ✅ `/cost/analyze` — pure hardcoded Malaysian JKR benchmarks, no DB (`cost_insights.py`) — verified.
- ✅ Master DB auto-seed from 902-line `master_prices_seed.json` + Excel export/import.
- ✅ `cost_analyst` agent is mounted backend-side (`main.py:46,74` + `/agents/cost_analyst/runs`) — addin not wired (Phase 2 gap).

---

## 2. Problems — ranked for a system that must actually run

### 🔴 P0 (NEW — shipping blocker above all) — The cost data pipeline is unprovisioned

1. Set `COST_DB_URL` (or repoint `cost_tools.py` at `DATABASE_URL`) in `.env*` **and** as an App Service
   env var on staging/prod — the deployment doc (`DEPLOYMENT.md:254-256`) says cost tables live in the
   same `DATABASE_URL` Azure Postgres, but the code reads a different var that's set nowhere.
2. Add the `embedding vector` column to `ai.n3c_material_costs` (migration — it was "to be added after
   import", never done).
3. **Ingest the actual N3C dataset** — obtain the real N3C CIDB price list, write a `scripts/ingest_n3c.py`
   (COPY/insert), then run `scripts/embed_costs.py` against it.
4. Only then does any Phase-0 correctness fix (below) matter.

### P1 — Quantity and price UNITS never align (silent wrong totals)
A wall is flat m² (`RevitModelWalker.cs:196`), never m³. `AutoMatch_Click`
(`CostDashboardPanel.xaml.cs:775-790`) applies `match.UnitPrice` with **zero check of `match.Unit` vs
`item.Unit`**; both sides carry `unit` but neither compares. A per-m³ N3C rate can land on an m² wall,
silently. Isn't reachable until P0 is fixed, but must be fixed before trusting any number.

### P2 — JKR code is a fragile name-string, not a validated attribute
Regex-extracted from text names only (`JkrCodeParser.cs:18-79`); broad fallback invents fake codes
(`(W32)`); no validation against real JKR prefixes; off-the-shelf names (`Basic Wall 200mm`) → null →
unpriced, with no drafter feedback (`Tests/JkrCodeParserTests.cs:55`).

### P3 — No real m³ volume takeoff; structural elements miscounted
Wall/slab volume is never measured — the takeoff tool's `GetMaterialQuantities` (m³/material, Inspectors.cs)
is a **separate walk** that never feeds `RevitModelWalker.GetAllItems()`. Two sources, two numbers.

### P4 — AI matching is a rescue, not the core
Local-rescue for types; is layer 3/4 only and only works if the server is up (should be the everyday
workhorse for non-JKR names).

### P5 — No versioning
Cost overwritten every refresh; no snapshots, no diff, no "what did that edit cost" history. The LIVE
banner delta dies with the panel (`_recentChanges` is in-memory).

### P6 — "Priced %" masks bad prices
`CreateDetailRow` renders Item/Qty/Rate/Total only (`CostDashboardPanel.xaml.cs:563-576`);
`PriceSource`/`confidence`/`match_layer` are stored but **never displayed** — no per-item provenance.

---

## 3. Half-cooked / dead spots confirmed

- **Two takeoff walks never meet.** `GetMaterialQuantities` (m³) and `RevitWalker.GetAllItems` (per-element)
  disagree; material volume is never in the cost pipeline.
- **Seed vs N3C drift.** Local `master_prices_seed.json` (~900 lines) and the (empty) N3C DB are separate
  sources with no stated precedence / reconciliation.
- **"53K records" is copy, not data.** Only the agent prompt mentions it; nothing ships it.

---

## 4. How to fix — phased to ship a genuinely good Cost to BIM

### Phase P0 — Provision the data pipeline (this is the real first step)
1. `COST_DB_URL` wiring (+ `DATABASE_URL` fallback) in `.env*` + Azure App Service env.
2. Migration: add `embedding vector(1536)` to `ai.n3c_material_costs`.
3. `scripts/ingest_n3c.py` — load real N3C data, then `embed_costs.py`.
4. Verify: `curl /cost/match-pipeline` with 3 sample items → expect real matches. **door)

### Phase 0 — Correctness core (now reachable)
5. **Unit-alignment guard** — reject when `price.unit != item.Unit` (explicit m²↔m³ via thickness or flag).
6. **Real m³ volume takeoff + single source of truth** (`RevitModelWalker` → `GetMaterialQuantities`).
7. **JKR validation + AI-first fallback** for non-JKR-named items, with clear "matched by AI / needs review" label.

### Phase 1 — QS surfaces (sellable in Malaysia)
8. Versioned snapshots + diff; 9. project rate libraries; 10. BQ breakdown + preliminaries/wastage/contingency; 11. confidence bands + provenance.

### Phase 2 — Cost copilot
12. Wire `cost_analyst` into RevitChatRouter for "what'll it cost if I…?" answers with N3C sources.

---

## 4. SCOPE DECISION REQUIRED

The P0 blocker (no `COST_DB_URL`, empty table, no embedding column, no N3C data + no ingest script) needs a
**source for the real N3C dataset** before Phases 1-3 have any value. That dataset does not exist in this
repo. Options:
- **(a)** You supply/point me at the real N3C CIDB extract → I write `ingest_n3c.py` + migrate + wire env.
- **(b)** We start from the small curated `master_prices_seed.json` and grow it — honest but does not give the
  "53K real prices" promise yet.
- **(c)** Roadmap the full pipeline but ship the offline lane + `/cost/analyze` first (they already work),
  treating the AI layer as a follow-up once data is in.

---

## Credits / correction

v1 (authored before data verification) claimed the pipeline "built & working" — **that was wrong** and is
corrected here. Claude's independent audit caught the data/config gap; all its claims were **re-verified
line-by-line and against the live DB** before being included in this v2. Every code line-number in this
doc was awk-checked against source.

### Optional summary

The addin's offline Cost to Tracker is real and verified. The AI matching/N3C layer is a **stub awaiting
real data** — the single most honest framing of "Cost to BIM" today.