# Live test round 10, 13 Nov 2026 (binacloudpc): stopped after 2 runs

**No value was written to a type the drafter did not name.** Two runs were done, F-t04 and F-t01. Both were right and are consistent with fixed. The round then stopped on the Mac side. The reverse tunnel PC:18007 → Mac:8005 dropped. The backend's database, OrbStack Postgres on 127.0.0.1:5434, stopped answering. Before that, the PC kept refusing remote input with `USER_CONTROL`. A RustDesk viewer was connected to the PC at 21:42. Every other run is **not done**.

There was one run per prompt, so nothing here is a rate.

## Header

| | |
|---|---|
| PC | binacloudpc, driven from the Mac through Windows-MCP (port 48820) and SSH. It did not reboot: boot time stayed 13:41:38 |
| Revit | 2027.3 (Education) |
| Add-in | Debug **0.0.103**, commit `f15d646a` exactly, built in a detached worktree with the staging update feed. Copied to `C:\Users\Public\rel-test\r10-addin-0.0.103` and loaded through `BINA_SYNC_PLUGIN_DIR` (task `BinaLiveTestR7`). The loader log read "loaded 0.0.103.0" at 19:22:56, 20:22:01 and 21:50:48. The pane menu showed "Version 0.0.103". The 0.0.101 folder is kept |
| Backend | local bina-ai **`2f331aa1`** (worktree `lh/lt-r2`, detached) on Mac `127.0.0.1:8005`, through a reverse tunnel to the PC's port 18007. No migration or dependency change from `3302ee55` |
| Fixtures | F: `C:\Users\Public\rel-test\brochure\downlight-model.rvt` with `PCO_broucher_Downlight_2026.pdf`. E: `sheets-test.rvt`. Both were reopened without saving. **No model was saved**: downlight-model mtime is still 14:28:45 and sheets-test is still 11 Nov |
| Exports | turn rows and events, screenshots and `notes.md` are in `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/b8bbfc99-95cf-49ed-a7f8-32e08adf1161/scratchpad/runs/R10/` |

## What stopped the round

| Time | What happened | Screen at the time |
|---|---|---|
| 19:24 | A short `USER_CONTROL` while Revit was starting. The guard waited and went on | — |
| 19:39–20:10 | `USER_CONTROL`, then `CONTROL_UNAVAILABLE`, which also refused screenshots. The F-t01 prompt was typed into the Attach dialog's File name box instead ("The file name is not valid"). Nothing was sent. I stopped and asked | at 20:10: only my Revit and my Attach dialog |
| 20:12–20:16 | `USER_CONTROL` again while Revit was starting. The guard waited | — |
| 20:21–21:25 | F-t03 hung in the guard's retries after the brochure was attached. I killed the run; the prompt was not sent | at 21:26: only my Revit and the PDF chip |
| 21:36 | `USER_CONTROL` again at the first click of the F-t03 resend. Killed before typing. F-t03 was then dropped by the new run order | — |
| 21:42 | A RustDesk process started on the PC with a relay connection, so a viewer was connected (another session saw this over SSH) | — |
| 21:45 | Three read-only checks in a row were free, so I went on | — |
| 22:06 | The E-c07 prompt was typed into a fresh `sheets-test.rvt` (Revit started 21:50) | only my Revit |
| 22:45 | **No turn reached the backend.** The backend log has no new turn after F-t01 (20:16). The ssh reverse tunnel for 18007 no longer exists. The DB on 127.0.0.1:5434 accepts TCP but every connect times out; `docker ps` and `orb list` hang; `orb status` says Running. Backend `/health` still answers 200 | — |

I did not restart OrbStack or the DB container, because the other local backends (ports 8001 to 8004) probably share it.

## Part F: the brochure fill

| Run | Round 9 / round 10 | Stored script? | Values written (checked in Revit) | Wrong value | Questions | Wide-write | Reply as shown | Failed | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|---|---|
| F-t04 | **Wrong** (0.04 A on 10 types) / **Right** | yes: `fill-from-document`, code starts `// inputs` (52 lines). Read **not cut** (`truncated: false`, 7 lines, 36654 steps) | Kadaran_Arus_jkr_stec = 0.04 A on 1X13W (38) and 1X8W (14) only. **Type Properties:** 2X14W, 1X28W and 2X18W read 0.00 A. Voltage is 0.00 V on both downlights (not written) | **no** | none | **did not appear** (right) | "Kadaran_Arus_jkr_stec = 0.04 A set on both downlight types (1X13W and 1X8W); Voltage was skipped, not in the brochure." | 0 | 3 | 9 |
| F-t01 | **Wrong** (turn failed) / **Right** | yes: `fill-from-document`, `// inputs` (52 lines). Read not cut (4 lines) | on 1X13W only: Model "Adrino LED 601 / 10 / 840" (page 2), URL "www.pcolite.com" (page 8), Description "LED Recessed Downlight" (prompt). **Type Properties:** 1X13W holds the three; 1X8W's Model, URL and Description stay empty | no | none | did not appear | "Type parameter Model, Description dan URL bagi type Normal (jkrEL_lit-fx_LED_c_dl_1X13W) sudah diisi." | **1**: `lookup {"playbook":"fill-from-document"}`, error `"arguments: Additional properties are not allowed ('playbook' was unexpected)"`. Next the model called `lookup {"id": …}`, which worked | 5 | 15 |
| F-t03 | Right / **not done** | | | | | | | | | |
| F-t10 | Right on values (Malay card) / **not done** | | | | | | | | | |
| F-n1, F-n2, F-n3 | new / **not done** | | | | | | | | | |

In both runs the backend sent the writes itself ("PLANNED n writes: the backend sends them now"), not the model.

**F-t04 result card** (screenshots `F-t04-card*.png`):
- Headline: the reply above, with an [Undo] button.
- Chip: **"Read back from Revit: 38 of 38 match"**. This is wrong, because 52 fixtures changed (38 + 14).
- "TYPES WRITTEN 38 elements" has one row, **"Normal 38 elements"**. No family is named and the 14 of 1X8W are missing.
- "Written as asked · 2" has two rows, "Kadaran_Arus_jkr_stec 0.04 A, was 0 A, PCO_broucher…pdf, page 2". Neither row says which type it is for.
- "Check 4 things": Voltage not in the brochure, once per type (×2); a "SKIP: Voltage (Normal Voltage) not printed…" line; "Type parameter: changes every instance — 38 … 1X13W and 14 of … 1X8W".
- The in-model popup said "38 elements in 3 views".
- Strip: while working, the yellow "Working" bar with 4 of 5 segments; at the end, a green "Done 9.9s" chip.

**F-t01 result card** (Malay, `F-t01-card*.png`):
- Headline: the reply above, with [Undo].
- Chip: "Dibaca balik dari Revit: 38 daripada 38 sepadan".
- "JENIS DITULIS 38 elemen" has one row, "Normal 38 elemen [Pilih]".
- "Ditulis seperti diminta · 3": Model (halaman 2), Description ("Prompt anda"), URL (halaman 8).
- Done chip: 15.5 s.

## Part E, Part D, Part G

**Not done.** E-c07 was typed at 22:06, but the tunnel and the DB were down (see above), so no turn exists for it. Nothing in the model changed.

## Round 9's findings

| Round 9 finding | Round 10 |
|---|---|
| F-t04: 0.04 A written to 8 other families (a type name shared by many families, "Normal") | **consistent with fixed**: 2 types only, checked in Type Properties on three other families |
| F-t04: the plan said the fields are "not on the family" | **consistent with fixed**: the read lists both as type parameters, and the plan wrote Kadaran_Arus and skipped Voltage as "not in the brochure" |
| F-t04: the fill read was cut | **consistent with fixed**: `truncated: false` |
| F: the fill playbook was not called (t01, t03, t10) | **consistent with fixed** in t01; t03 and t10 not tested |
| F-t01: `set_parameters` refused for a bad target, the turn failed, and the pane showed a green Done chip | **consistent with fixed**: the turn is done, with one failed `lookup` call (a new shape, see above) |
| F-t01: Model kept the prompt's spacing | **consistent with fixed**: "601 / 10 / 840" from the brochure |
| F-t04: the drafter's "Undo that batch" was ignored | not tested (no wrong write happened) |
| F-t10: the wide-write card was in Malay and appeared for 52 fixtures; the reply said "2 elemen" | not tested |
| F-t03: answered right, but read "Model" as `Model_jkr_sit` | not tested |
| Fallback reply replaces the model's sentence | consistent with fixed in F-t04 and F-t01 (the model's sentence was shown); D not tested |
| Dimensions: overall strings skipped, d03 timeout, three identical type labels | not tested |
| Change type: c07 timeout, asked twice, "other 2" card, the two type lists differ | not tested |
| Schedule: s07 question came from the model | not tested |
| Tagging: t07 answered from the chat; strip "50/50" for 58; fallback "58 objects created." | not tested |

## New or worse

- **The result card counts one type for a write to two types (F-t04).** The chip reads "38 of 38 match" and there is one row, "Normal 38 elements", for 38 + 14 fixtures. Neither the family nor the type of a row is named. This was handed to another session; a fix is in add-in `a47b1018`, which is built as 0.0.104 but not yet run.
- **`lookup {"playbook": …}` is refused** as invalid arguments (F-t01). The model recovered. A fix is in backend `02d3541e`, not yet run.
- **Remote control kept dropping** with `USER_CONTROL`, mostly while Revit started, and once for 30 minutes. While control was held, one prompt was typed into a file dialog. The driver's attach step does not check that the dialog opened before typing.
- **The Mac's test setup failed during the round.** The reverse tunnel dropped and the DB hung, and both happened silently: the backend's `/health` stayed 200. A run sent while the tunnel was down produces no turn and no error on the Mac side.

## What is still running or changed

Nothing is restored yet.

- **PC:**
  - Revit is open on `sheets-test.rvt` (started 21:50), with the E-c07 prompt possibly sitting unanswered in the pane. Nothing is changed in the model.
  - The task `BinaLiveTestR7` points at `r10-addin-0.0.103` and `sheets-test.rvt`.
  - New this round: the folder `C:\Users\Public\rel-test\r10-addin-0.0.103` and `addin-0.0.103.tgz`.
  - From earlier rounds: `r7`, `r7-addin-0.0.101`, `r7-addin-0.0.102`, their tgz files, the `brochure` folder, and the add-in config pointing at port 18007 (backup `config.json.bak-20261113-r7-before-18007`).
  - Not touched: port 18002 and the installed 0.0.117 and 0.0.118 folders.
- **Mac:**
  - The backend on 8005 runs `2f331aa1` from `lh/lt-r2` (it was at `3302ee55`).
  - The reverse tunnel is **down**.
  - The OrbStack Postgres on 5434 is **hung** (not restarted).
  - Add-in 0.0.104 (`a47b1018`) is built at `…/scratchpad/addin-build` and `addin-0.0.104.tgz`, not copied to the PC.
  - The build worktree `…/scratchpad/addin-build` is registered in the add-in repo's worktree list.
