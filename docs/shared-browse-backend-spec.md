# Backend prompt — browse the Shared area too

> **Shipped 2026-08-21. As-built corrections, where this prompt guessed wrong:**
>
> - **Routes renamed:** `GET project/{id}/folders?area=` and
>   `GET project/{id}/folder/{folderId}/designs`. The old `wip-folders` /
>   `wip-folder/{id}/designs` names still resolve, deprecated. The add-in
>   calls the new ones.
> - **Three areas, not two:** `wip | shared | published`, one design status
>   each. InReview and Archive are not browsable (404).
> - **Folder ids do not collide** — `bim_designs.id` is a project-wide PK, so
>   the designs route derives the area from the folder. `?area=` is accepted
>   as an *assertion*: a mismatch is 404, never a cross-area read. The add-in
>   sends it, which turns a stale folder list into an error rather than
>   plausible rows from the wrong area.
> - **Promotion mirrors the version number** — WIP V6 approved into Shared is
>   V6 there, stepping up only when that number is already taken in the
>   target folder by different content. Staging: 45 promoted rows, 0
>   mismatches. So the "Shared V1 for a file the drafter knows as V7" fear
>   below is not the normal case; `promotedFromDesignId` /
>   `promotedFromVersionNumber` / `promotedFromArea` exist for when it is,
>   and the picker names the source **only** on a mismatch.
> - **`lineageId` is carried forward through promotion, not restarted** —
>   deliberately, so comment history survives. The version chain is scoped by
>   project + folder + status + filename. This prompt assumed the opposite.
> - **`docGuid` is not ambiguous**: promoted rows carry no GUID at all
>   (`copyDesignWithRelations` never copies `sourceDocumentGuid`). Use
>   `designId` for Shared and Published. `sync/versions?area=` exists as
>   defence and the add-in sends it anyway.
> - **`syncSource: "promotion"` does not exist** — promoted rows get the
>   column default, `web`. The example below is wrong on that field.
> - **Shared scope needed no special-casing**: the permission model already
>   reads Shared wider than WIP (discipline-scoped roles read Shared across
>   every discipline, WIP and Published only in their own). One helper,
>   `applyBimReadScope`, drives folders, designs and versions, so browse
>   cannot drift from what download allows.

Follow-on to `wip-browse-backend-spec.md`, which shipped as
`GET .../project/{projectId}/wip-folder/{folderId}/designs` plus a rebuilt
`sync/versions`. The add-in now browses WIP: folder → model → version →
download, with everything filtered server-side by the caller's role.

**What we want next:** the same browse against the **Shared** area, with the
drafter choosing which area they are looking at. One toggle in the picker,
two areas, everything else identical.

Service: **bina-be**. Auth: same user bearer token as the WIP routes.

---

## Why `latest-shared-urls` is not enough

`GET /api/cloud-docs/bim-discipline/project/{id}/latest-shared-urls`
already exists and the add-in uses it for the bulk discipline download.
It cannot back this feature:

- it returns **only the latest** version of each filename — this feature is
  version picking, so the history is the point;
- it groups by discipline → tracking folder → files, a shape unrelated to
  the row-per-lineage contract the picker already renders;
- rows carry no `designId`, no `canDownload`, no `versionCount`, no
  `lineageId`;
- it was built for a bulk pull, not for per-role browse filtering.

Please leave it alone — it has its own callers. This is a separate surface.

---

## 1. Preferred shape: an `area` parameter on the routes that exist

Rather than a parallel set of `shared-folder` routes, take an `area` on the
two browse routes and default it to `wip` so today's callers are unchanged:

```
GET /api/cloud-docs/bim-discipline/project/{projectId}/wip-folders?area=wip|shared
GET /api/cloud-docs/bim-discipline/project/{projectId}/wip-folder/{folderId}/designs
```

Two things to decide on your side, because you know the schema:

1. **Does the folder list route need renaming?** `wip-folders?area=shared`
   reads badly. If a rename is cheap, `…/folders?area=` with `wip-folders`
   kept as a deprecated alias is nicer. If not, the ugly name is fine — the
   add-in does not care, and a rename is not worth a migration.
2. **Do folder ids collide across areas?** If a folder id is unique
   project-wide, the designs route can derive the area from the folder
   itself and needs no `area` param at all — that is the better outcome, so
   prefer it if the schema allows. If ids are only unique *within* an area,
   the designs route needs `area` too, and must 404 (not silently
   cross-read) when the id belongs to the other area.

Tell us which you picked; the client sends whatever you specify.

## 2. Response shape — unchanged, plus one field

Same `designs[]` rows as the WIP route, same `hasMore` / `limit` / cursor
paging, same `canDownload`. Add one field so the picker can label a row
honestly when the two areas are shown together in future:

```jsonc
{
  "designs": [
    {
      "docGuid": "…", "designId": 5120, "lineageId": "…",
      "name": "ARC-Tower-A-Model.rvt",
      "versionNumber": 3, "versionCount": 3,
      "area": "shared",              // NEW: "wip" | "shared"
      "uploadedAt": "…", "uploaderName": "…",
      "fileSize": 184922112, "fileHash": "9f2c…",
      "disciplineType": "ARCHITECTURE", "designStatus": "ACTIVE",
      "syncSource": "promotion",     // whatever you use for promoted rows
      "canDownload": true
    }
  ],
  "nextCursor": null, "hasMore": false, "limit": 200
}
```

Send `area` on WIP rows too (`"area": "wip"`), so the field is never
ambiguous by omission.

## 3. Versions of a Shared model — the part we cannot guess

`sync/versions?projectId=&docGuid=|designId=` must resolve Shared designs
as well. You wrote that lineage identity is
`project + folder + status + filename` with a real `lineageId` column, so
**promotion presumably starts a new lineage in the Shared scope**. If that
is right, a Shared model has its own version chain and `designId` lookup
already lands on it — please confirm and cover it with a test.

What we need decided and written down:

- **Does a Shared lineage have its own version numbers?** If Shared V1 is
  WIP V7 promoted, the picker will show "V1" for a file the drafter knows
  as V7. That is confusing, and the fix belongs server-side: either return
  the source version on the row, or a label we can show. If you can give us
  `promotedFromDesignId` (and ideally `promotedFromVersionNumber`) on
  Shared versions, the picker will render "V1 · promoted from WIP V7" and
  the whole class of confusion disappears.
- **Does `docGuid` still resolve correctly?** A promoted copy may carry the
  same `sourceDocumentGuid` as its WIP original. If so, a `docGuid` lookup
  is now ambiguous across areas and must be scoped — otherwise the picker
  can show WIP history for a Shared model or vice versa. Either scope the
  lookup by the design's area, or tell us to always use `designId` for
  Shared rows and we will.

Both of these are correctness, not polish: getting them wrong shows a
drafter the wrong file's history, and they download the wrong bytes
believing they picked right.

## 4. Access control — §3 of the WIP spec, restated for Shared

Everything in `wip-browse-backend-spec.md` §3 applies unchanged: the
server filters by role, rows the caller may not read are absent (not
flagged), an unreadable folder is 403 rather than an empty list, and
`download` re-checks independently on every call.

One difference to get right rather than assume:

- **Shared is usually readable more widely than WIP.** A role that cannot
  see another discipline's WIP folder can very often see that discipline's
  published Shared models — that is what publishing is for. Do not reuse
  the WIP scope verbatim if the real permission model says otherwise;
  apply whatever the Shared rules actually are.
- **Read wide, download narrow.** If a role may view a published model but
  not pull the .rvt, that is exactly what `canDownload: false` is for. The
  picker greys the button and says why, so the drafter is never left
  clicking a dead control.

## 5. Test cases to hand back

1. Same project, both areas — `area=wip` and `area=shared` return
   different folder sets, each role-filtered.
2. A discipline whose WIP the caller cannot read but whose Shared models
   they can — WIP 403, Shared lists rows.
3. A folder id from the other area — 404, never a cross-area read.
4. Shared model with 3 versions — `sync/versions` by `designId` returns
   that Shared chain, not the WIP chain it was promoted from.
5. Promoted model whose `docGuid` matches its WIP original — a `docGuid`
   lookup returns exactly one area's history, and it is the right one.
6. Shared row with `canDownload: false` — listed, and `download` 403s.
7. Paging over a Shared folder with more than `limit` models — `hasMore`
   true, cursor walks to the end without repeats or gaps.

## 6. What the add-in will do with it

A source toggle (WIP / Shared) above the folder column. Switching it
reloads folders for that area and clears the model and version columns.
Everything downstream — role filtering, `canDownload`, paging, the
staging-file download — is already built and unchanged.

No add-in work is blocked on the answers to §1; the client can send
whatever the routes end up wanting. §3 is the one that blocks, because the
picker cannot decide by itself which history belongs to which area.
