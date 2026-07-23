# Continue Conversation from History — Design

**Date:** 2026-07-23
**ClickUp:** https://app.clickup.com/t/86eybfcqj
**Repo:** revit-addin-sync only — zero backend changes.

## Problem

Opening a conversation from the History tab is view-only. Users cannot pick up
an old conversation and keep talking in it.

## Why the backend needs nothing

`RevitChatRouter` stamps a GUID `_sessionId` on every backend call. The backend
agent (`revit_ai.py`) runs with `add_history_to_context=True` over PostgresDb,
so any new turn sent under an old `session_id` continues that conversation with
its server-side run history — across restarts and instances (proven by the
resume-by-run_id work). The whole gap is that the addin forgets which
`session_id` a `HistoryEntry` belonged to.

## Behavior (decided)

- **Continue button** in the HistoryView detail-pane header (next to Download).
- Click → past bubbles load into ChatView, router adopts the entry's session,
  tab switches to Chat, input ready. New exchanges append to the same
  `HistoryEntry` — no duplicate history rows.
- **Always continue, best-effort:** entries saved before this feature carry no
  `SessionId`; Continue still works but starts a fresh backend session (the
  stored bubbles remain visible in the pane; the model simply has no
  server-side memory of them). Button is always visible.

## Changes

| File | Change |
|------|--------|
| `UI/Copilot/Model/CopilotModels.cs` | `HistoryEntry` gains nullable `SessionId` (serialized by CopilotStateStore; old JSON deserializes it as null). |
| `UI/Copilot/RevitChatRouter.cs` | `AdoptSession(string id)`: non-empty → `_sessionId = id`; null/empty → `ResetSession()`. |
| `UI/Copilot/CopilotViewModel.cs` | Stamp `SessionId` from the router when `AppendToCurrentSession` creates a new `HistoryEntry`. New `ContinueSession(HistoryEntry)`: `_currentSession = entry`, `AdoptSession(entry.SessionId)`, repopulate ChatView bubbles from `entry.History`, `Tab = CpTab.Chat`. |
| `UI/Copilot/Screens/HistoryView.xaml(.cs)` | Continue button in detail-pane header → `Vm.ContinueSession(entry)`. |

## Edge cases

- **Pending confirm in the current chat:** the router's existing stale-confirm
  handling resolves an abandoned Ya/Tidak before routing the next turn.
- **Current unsaved chat:** already lives in History as `_currentSession`;
  switching sessions loses nothing.
- **Backend session retention:** agno sessions are rows keyed by `session_id`;
  nothing expires them, so Continue works regardless of age.

## Testing

Windows build + manual Revit pass (standard gate; repo does not build on
macOS): continue a new-style session mid-conversation and confirm the model
recalls earlier turns; continue a pre-feature entry and confirm it behaves as a
fresh session with bubbles still shown; confirm renamed/deleted entries behave.
