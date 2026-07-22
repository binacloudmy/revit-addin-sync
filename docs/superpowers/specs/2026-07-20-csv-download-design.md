# Download CSV Under AI Tables — Design

**Date:** 2026-07-20
**ClickUp:** [86eybfttd](https://app.clickup.com/t/86eybfttd) — "Add download csv functionality"
**Repo:** revit-addin-sync only (bina-ai untouched)

## Problem

When the copilot answers with a schedule (door schedule, count_by breakdown, audit
table), the drafter sees the data rendered in the pane but has no way to take it
out of Revit. The ask: a download button under the schedule; clicking it opens a
save dialog and writes a CSV.

## Scope decision

**Any markdown table the AI renders gets the button** — not just replies that came
from schedule tools. The pane already parses every GitHub-style `| table |` into
rows/cells inside `Helpers/MarkdownRenderer.cs`; those parsed cells are the CSV
source. This makes the feature addin-only, protocol-free, and it covers count_by
results, audits and real schedules alike.

Rejected alternatives:

- **Backend flags downloadable frames** — two-repo change, protocol churn, no
  extra value: the pane already holds the cells it rendered.
- **Route through `export_schedule_to_excel`** — only works for real Revit
  schedule views, exports xlsx to Desktop with no save dialog, and misses
  AI-composed tables entirely.

## UX

- Under every rendered table with **≥ 1 data row after the header**, a small
  "Download CSV" text button, right-aligned, styled with the existing pane
  tokens (`CopilotColors`) so it is dark/light safe and low-key.
- Click → `Microsoft.Win32.SaveFileDialog`, filter `CSV (*.csv)|*.csv`,
  default extension `.csv`.
- Default filename: the nearest markdown heading **above** the table in the same
  reply, slugified (lowercase, spaces → `-`, filesystem-unsafe chars stripped,
  max ~60 chars); fallback `bina-schedule.csv`.
- Dialog cancel = no-op. Successful save = no ceremony (the OS dialog is the
  feedback). Write failure = non-fatal inline notice (see Errors).
- Appears everywhere `MarkdownRenderer.Render` runs — ChatView bubbles and
  HistoryView — with no extra wiring.

## Technical design

### 1. `Helpers/TableCsv.cs` (new, pure static)

```csharp
public static class TableCsv
{
    // rows = the same List<string[]> MarkdownRenderer builds (header first).
    public static string Serialize(IReadOnlyList<string[]> rows);
    public static string SuggestFileName(string nearestHeading); // slug or "bina-schedule"
}
```

- RFC 4180: fields containing `,`, `"`, CR or LF are quoted; inner `"` doubled;
  CRLF row terminator.
- Cell text is exported as **plain text**: inline markdown markup that
  `AddInlines` renders (`**bold**`, `*italic*`, `` `code` ``, `[text](url)`)
  is stripped to its visible text; element-id link cells export the bare id.
  Values stay exactly as displayed (mm/display units untouched).
- Ragged rows padded with empty fields to the widest row (mirrors how the grid
  renders missing cells as `""`).
- No I/O in this class — serialization only, fully unit-testable on any OS.

### 2. `Helpers/MarkdownRenderer.cs` changes

- `Render` already walks lines top-down; track the text of the last heading
  seen so the table block can name its file.
- `TableBlock(...)` currently returns the `Grid`. It now returns a vertical
  `StackPanel { Grid, downloadButton }` and keeps a reference to its
  `dataRows` (the pre-render `List<string[]>`) for the click handler.
  The existing `BlockUIContainer` hosting at the call site is unchanged.
  If the grid turns out to be wrapped in a horizontal `ScrollViewer` (the
  docstring mentions one), the button goes **outside** the scroll region —
  StackPanel wraps the ScrollViewer — so it never scrolls away with a wide
  table.
- Button click handler (in the renderer, near the existing `IdLinkText` click
  wiring):
  1. `SaveFileDialog` with suggested name.
  2. On OK: `File.WriteAllText(path, TableCsv.Serialize(rows), new UTF8Encoding(true))`
     — UTF-8 **with BOM** so Excel opens Malay/unicode text correctly.
  3. Entire handler wrapped in try/catch — the pane runs inside Revit and must
     never throw across the WPF dispatcher.

### 3. Errors

- Cancel → nothing.
- `IOException`/`UnauthorizedAccessException` → swallow, show a one-line
  notice using the pane's existing lightweight feedback affordance (same
  pattern other ResultView/ChatView failures use); never a modal, never a
  crash.

## Testing

- **Unit (runs on macOS/CI):** new `Tests/TableCsvTests.cs` — escaping (comma,
  quote, newline in cell), unicode round-trip, ragged rows, empty table, markdown
  stripping, element-id cells, filename slugging (Malay text, illegal chars,
  length cap, fallback).
- **Manual gate (Windows + Revit):** new COPILOT-TESTING.md entry — ask for a
  door schedule, button appears under table, save dialog opens with sensible
  name, file opens in Excel with correct columns/encoding; cancel is a no-op;
  history view shows the button on old replies.

## Out of scope

- xlsx output (native `export_schedule_to_excel` already covers that path).
- Exporting hidden/full schedule columns not shown in the reply.
- Backend/protocol changes of any kind.
