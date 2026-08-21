# Backend prompt — browse WIP models and their versions

Hand this to whoever builds bina-be. Everything below is what the Revit
add-in needs in order to let a drafter pick ANY model in a project's WIP
area and download a chosen version of it. Today the add-in can only list
versions of the model that is already open, because the only version
route is keyed by `docGuid` and there is no way to enumerate WIP files.

Service: **bina-be** (`api-stg.binacloud.ai` / `api.binacloud.ai`).
Auth: the same user bearer token as `/api/cloud-docs/bim-discipline/sync/*`.

---

## What already exists (do not change)

> **Built 2026-08-21 — this section was wrong when written.** Kept for the
> record, corrected inline. `sync/versions` was listed here as shipped; it
> never existed, even though the add-in has been calling it since the
> rollback feature (`SyncApiClient.GetVersionsAsync`) — every call 404'd and
> was swallowed as "no history". It was built fresh with both keys.

| Route | Returns | Status |
|---|---|---|
| `GET /api/cloud-docs/bim-discipline/user/projects` | projects the user can sync into | existed |
| `GET /api/cloud-docs/bim-discipline/project/{projectId}/wip-folders` | WIP folders: `{ id, name, disciplineType }` | existed, now role-filtered |
| `GET /api/cloud-docs/bim-discipline/sync/versions?projectId=&docGuid=` | every version of one lineage | **did not exist — built** |
| `GET /api/cloud-docs/bim-discipline/discipline/{designId}/download` | streams that version's bytes | existed, already re-checks access |

Lineage identity is **not** `docGuid`. bina-be keys it on a `lineageId`
uuid that survives rename, move and promotion, falling back to a
project + folder + status + filename scope for rows predating that
column. `docGuid` (`sourceDocumentGuid`) is provenance only — returned
because the add-in asked for it, never the anchor.

---

## 1. NEW — list the models inside a WIP folder

```
GET /api/cloud-docs/bim-discipline/project/{projectId}/wip-folder/{folderId}/designs
```

Optional query: `?search=<substring>` (match on name), `?limit=` + `?cursor=`
if a folder can hold enough files to need paging — say so in the response
either way so the client knows whether it is showing everything.

One row per **lineage** (one model), carrying its head version — not one
row per version. The version list is fetched separately once the user
picks a model.

```jsonc
{
  "designs": [
    {
      "docGuid": "8f3c...-...",   // null for models never synced from Revit — see §2
      "designId": 4211,            // head version's design id; downloadable as-is
      "name": "ARC-Tower-A.rvt",
      "versionNumber": 7,
      "versionCount": 7,
      "uploadedAt": "2026-08-19T04:12:33.000Z",
      "uploadedBy": 132,
      "uploaderName": "Wafiy",
      "fileSize": 184922112,
      "fileHash": "sha256:...",
      "disciplineType": "ARCHITECTURE",
      "designStatus": "ACTIVE",
      "syncSource": "revit-addin",     // or "web-upload"
      "urnInBase64": "dXJuOi4uLg",
      "xktConversionStatus": "SUCCESS"
    }
  ],
  "nextCursor": null,
  "hasMore": false,
  "limit": 200
}
```

As built: `hasMore` and `limit` are **always** present, so the client never
infers "is that everything?" from a full page. The cursor is keyset on
`(name, id)`, base64url; `limit` defaults to 200, caps at 500. Rows also
carry `lineageId`. `fileHash` is raw hex with no `sha256:` prefix — the
example above was wrong, nothing writes one.

Field notes:

- `docGuid` is the key the add-in already uses for version history. Return
  it whenever the lineage has one.
- `designId` must be the **head** version, so "download latest" needs no
  second call.
- `fileSize` drives a size warning before a drafter pulls 200 MB over VPN.
- Names are shown verbatim in a Revit dialog; no HTML.

Status codes: `200` with `"designs": []` for an empty folder (not 404),
`403` when the user cannot see the project, `404` for an unknown folder.

## 2. NEW — version history keyed by design id

Models uploaded through the web have no `docGuid`, so
`sync/versions?docGuid=` cannot resolve them and those rows would be
un-browsable. Extend the existing route to accept either key:

```
GET /api/cloud-docs/bim-discipline/sync/versions?projectId=&docGuid=...
GET /api/cloud-docs/bim-discipline/sync/versions?projectId=&designId=...   // NEW
```

`designId` resolves to that design's lineage and returns the same
`{ "versions": [...] }` shape already in use — unchanged fields, so the
add-in's `DesignVersion` model still deserializes. Supplying both is not
an error; `docGuid` wins. Supplying neither is `400`.

## 3. Access control — filter by the caller's role

**The list must contain only the files this user's role lets them see.**
The add-in shows the response verbatim and applies no filtering of its
own: it has no copy of the permission model and cannot be trusted to
enforce one. Anything the server returns, the drafter sees and can try to
download. So the filtering is the server's job, on every one of these
routes.

Concretely:

- Scope to the caller's project membership first, then to their role
  within that project, then to any per-folder or per-discipline grant on
  top. An architect who can reach only the ARC WIP folders must get the
  ARC folders' files and nothing from STR or MEP — not a full list they
  are then refused at download time.
- Row-level too, not just folder-level. If a role can see a folder but is
  restricted to a subset of its files (own uploads only, published
  designs only, whatever the model says), return that subset. A design
  the caller may not read must be absent from `designs[]`, not present
  with a flag.
- A folder the user cannot read at all is `403`, not an empty list. The
  add-in tells the drafter "you don't have access to this folder" rather
  than "this folder is empty", and those are different problems to chase.
- Filter `GET .../project/{projectId}/wip-folders` the same way, since it
  is the first step of the same browse. A folder listed there but `403`
  on its files is a dead end the drafter cannot explain.
- `GET .../discipline/{designId}/download` must re-check independently.
  Listing is not authorization to download: the add-in calls download
  with an id it got from a list that may be minutes stale, and the id is
  guessable. Re-run the role check on every download.
- Role changes take effect on the next call — do not serve a cached list
  computed under the caller's old grants.

If a role can browse a design but not download it (a read-only reviewer,
say), add `"canDownload": true|false` to the design row so the add-in can
disable the button instead of letting the drafter wait through a picker
and then eat a 403. Absent field = treat as `true`.

## 4. Test cases to hand back

> All 8 covered in `src/modules/bim-discipline/bim-wip-browse.spec.ts`, plus
> paging, malformed cursor, legacy-lineage fallback and add-in field shape.
> Case 3 splits in practice: a cross-org project is 403 at
> `assertProjectAccess`, while a folder id belonging to another project is
> 404. A foreign or unsynced version anchor returns an empty list rather
> than 403, so ids cannot be probed for existence.

1. Folder with 3 models, one of them web-uploaded (`docGuid: null`) —
   all 3 listed, the web-uploaded one resolvable via `designId`.
2. Empty folder — `200`, empty array.
3. Folder in another org's project — `403`.
4. `sync/versions?designId=` on a lineage with 7 versions — 7 rows, one
   `isActive: true`.
5. `download` on a designId the caller cannot see — `403`.
6. **Role filtering:** same folder, two users with different roles — each
   gets only their permitted rows, and the union of the two responses is
   the folder's real contents.
7. **Stale id:** user lists a folder, loses access, then calls
   `download` with an id from that list — `403`.
8. **Read-only role:** rows come back with `canDownload: false`, and
   `download` on one of them is `403`.
