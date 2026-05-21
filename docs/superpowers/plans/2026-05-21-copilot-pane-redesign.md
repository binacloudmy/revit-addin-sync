# Revit Copilot Dockable Pane Redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the dark floating `AIAssistantWindow` with a right-docked WPF "Revit Copilot" pane that faithfully reproduces the `design_handoff_revit_copilot` hi-fi design — Chat / Library / History / Saved tabs, Tier-1 vetted forms, Tier-2 AI review-and-run, mention input, and viewport highlight markers.

**Architecture:** A new `CopilotPaneHost : Page, IDockablePaneProvider` (DockPosition.Right) wraps a single `CopilotPanel` UserControl. The panel is driven by one `CopilotViewModel` (manual `INotifyPropertyChanged`, mirroring the prototype's `useReducer` state machine: `screen`, `tab`, `toolId`, `formValues`, `runResult`, `thread`, `history`, `pinned`, `highlights`). Screen bodies are `UserControl`s swapped via a `ContentControl` + `DataTemplate` selector. Tool execution reuses the existing `App.AIExternalEvent` / `CodeExecutionHandler` / `CodeExecutor` pipeline and `VettedToolCode` synthesizers; Tier-2 codegen uses `AIService.RouteAsync`. A new copilot-specific design token + style ResourceDictionary (`UI/Copilot/CopilotTokens.xaml`, `CopilotStyles.xaml`) reproduces the handoff palette (ink `#0b0d12`, blue `#2563eb`, purple `#7c3aed`, green `#16a34a`). Persistence (History/Saved) via a JSON file in `%APPDATA%\RevitWebAppSync\copilot-state.json`.

**Tech Stack:** WPF (net10.0-windows, SDK-style csproj → files auto-included), Revit API 2027, Newtonsoft.Json, ClosedXML, Microsoft.CodeAnalysis (existing). No new NuGet packages.

> **⚠️ BUILD CONSTRAINT:** This is a WPF + Revit-API add-in. It **cannot be compiled or run on macOS** (the dev box). All `dotnet build` / Revit smoke-test verification must happen on a Windows machine with Revit 2027. Each task commits a focused, self-contained change so build errors stay localized. The cross-platform `Tests/Tests.csproj` (Revit-API-free) is the only thing buildable on macOS — use it for catalog/data/logic tasks.

---

## File Structure

**New files (all under `UI/Copilot/`):**

| File | Responsibility |
|---|---|
| `CopilotPaneHost.cs` | `Page` + `IDockablePaneProvider`, DockPosition.Right, static `PaneId`, exposes `.Panel`. Mirrors `CostDashboardHost.cs`. |
| `Commands/OpenCopilotCommand.cs` | Ribbon command: `GetDockablePane(PaneId).Show()`, push `UIApplication`/`UIDocument` into the panel. Replaces `OpenAssistantCommand`. |
| `CopilotTokens.xaml` | Brushes/fonts/dims for the handoff palette + tier colors + icon-tile palettes. |
| `CopilotStyles.xaml` | Reusable styles: tab button, chip, tool card, ghost/primary/run buttons, segmented control, select dropdown, pill, code `<pre>` block. |
| `CopilotTheme.cs` | `EnsureLoaded()` merges the two dictionaries once (copy of `JkrTheme` pattern, copilot URIs). |
| `CopilotIcons.cs` | Static `Geometry`/`PathData` map reproducing the 30 SVG icons from `shared.jsx` `Icons` (24×24 viewbox path strings). |
| `Model/CopilotCatalog.cs` | The canonical 14-tool catalog (5 `VETTED` + 9 `AI`), `CATEGORIES`, `SEED_HISTORY` — direct port of `data.jsx`. Plain C# records, Revit-free (testable). |
| `Model/CopilotModels.cs` | `ToolDef`, `FieldDef`, `ResultModel` (+ `BarItem`, `IssueItem`, `DiffItem`), `HistoryEntry`, `ChatMessage`, `ClarifyOption`, `HighlightMarker`, enums (`Screen`, `Tab`, `MsgKind`, `ResultKind`, `FieldKind`). |
| `Model/QueryInterpreter.cs` | Port of `interpretQuery` + `pickResponseTool` + `CLARIFICATIONS` from `chat.jsx`. Revit-free (testable). |
| `Model/CopilotStateStore.cs` | Load/save `pinned` + `history` to `%APPDATA%\RevitWebAppSync\copilot-state.json`. |
| `CopilotViewModel.cs` | Central state + commands (the reducer). Holds child screen state, raises `PropertyChanged`. |
| `Controls/TierBadge.xaml(.cs)` | Tier 1 "Vetted" (green) / Tier 2 "AI" (purple) pill. |
| `Controls/IconTile.xaml(.cs)` | Rounded colored tile rendering a `CopilotIcons` geometry; `Bg`/`Fg`/`Glyph`/`Size` deps. |
| `Controls/ToolCard.xaml(.cs)` | Library/Saved tool row (vetted green stripe variant + AI chevron variant). |
| `Controls/PromptBar.xaml(.cs)` | Bottom input: hosts `MentionInput` + AI pill + send button + `@` hint line. |
| `Controls/MentionInput.xaml(.cs)` | `RichTextBox`-based `@`-mention editor + picker popup. Emits `(text, mentions[])`. |
| `Screens/ChatView.xaml(.cs)` | Empty state (greeting, suggested prompts, topic chips, library CTA, "how runs work") + active thread `ItemsControl` with message templates. |
| `Screens/LibraryView.xaml(.cs)` | Search bar, category chips, Recent, Vetted section, AI section, Ask-Copilot fallback. |
| `Screens/HistoryView.xaml(.cs)` | Scrollable run list. |
| `Screens/SavedView.xaml(.cs)` | Pinned tools / empty state. |
| `Screens/ToolFormView.xaml(.cs)` | Tier-1 form (fields + live Preview card + Run). |
| `Screens/ToolReviewView.xaml(.cs)` | Tier-2 plan + code disclosure + amber reassurance + Run. |
| `Screens/RunningView.xaml(.cs)` | Step list with status circles, info chip, Cancel. |
| `Screens/ResultView.xaml(.cs)` | Done header + `ResultBody` variants (count/issues/list/file/plain) + Next steps + follow-up bar. |
| `Controls/MessageTemplates.xaml` | `DataTemplate`s for chat messages: user, thinking, clarify, proposal, running, result (`CompactResult`). |
| `Highlights/HighlightOverlay.cs` | Adorner on the active Revit view window projecting `HighlightMarker`s; floating "N highlighted" clear chip. |
| `CopilotPanel.xaml(.cs)` | Panel chrome: header (bot avatar, title, status, +/⋯), tabs (hidden on sub-screens), breadcrumb, body `ContentControl`. Binds to `CopilotViewModel`. |

**Modified files:**

| File | Change |
|---|---|
| `App.cs:24-32` | Add `public static CopilotPaneHost CopilotPaneHost { get; private set; }`. |
| `App.cs:48-88` | Register the copilot pane (RegisterDockablePane, DockPosition.Right) in a try/catch block like the others. |
| `App.cs` `CreateRibbonTab` (~174-184) | Replace the `AskAI`/`OpenAssistantCommand` push button with `OpenCopilot`/`OpenCopilotCommand` (keep `microchip.png`). |
| `RevitWebAppSync.csproj` | No edits needed for `.cs`/`.xaml` (SDK glob). Only confirm `UI/Copilot/**` is not excluded. |
| `Commands/OpenAssistantCommand.cs` | Delete (replaced). `AIAssistantWindow.xaml(.cs)` deleted at end (Task 16) once parity confirmed. |

---

## Conventions (match existing code)

- Manual `INotifyPropertyChanged`: `protected void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));` (as in `PanelVm.cs`).
- Commands: lightweight `RelayCommand : ICommand` (create once in `Model/RelayCommand.cs`; addin has none yet).
- Theme load: call `CopilotTheme.EnsureLoaded()` at the top of `CopilotPanel`'s constructor before `InitializeComponent` references resources — actually merge in the host constructor before the panel is built, mirroring `JkrTheme`.
- All Document mutations go through `App.AIExternalEvent.Raise()` with `App.AIHandler.CodeToExecute` + `OnCompleted`. **Never** touch `Document` from the UI thread.
- No emojis in C# print/log lines (CLAUDE.md). The greeting "Hi {name} 👋" is UI copy from the design — keep it (it's user-facing text, not a log line).
- Geist font: attempt to load embedded Geist `.woff2`; fall back to `Segoe UI`. (Geist not bundled yet — Task 2 adds a `FontFamily` resource with fallback chain; embedding the actual woff2 is optional polish noted in Task 17.)

---

## Build Sequence (mirrors the design's 9 suggested steps)

1. **Plumbing** — Tasks 1–3 (host, command, App.cs wiring, theme/tokens/icons, panel shell + tabs).
2. **Catalog + models** — Tasks 4–5 (data port, models, store, interpreter; macOS-testable).
3. **Vetted tools end-to-end** — Tasks 6–8 (ToolForm, Running, Result, executor wiring).
4. **Library + History + Saved** — Tasks 9–10.
5. **Chat empty + prompt bar** — Task 11.
6. **Chat proposal flow (Tier-2 + AIService)** — Task 12.
7. **Clarification cards** — Task 13.
8. **Mention input** — Task 14.
9. **Highlights overlay** — Task 15.
10. **Retire old window + polish** — Tasks 16–17.

---

## Task 1: Dockable pane host + ribbon command + App.cs wiring

**Files:**
- Create: `UI/Copilot/CopilotPaneHost.cs`
- Create: `Commands/OpenCopilotCommand.cs`
- Modify: `App.cs` (static prop, registration, ribbon button)
- Create: `UI/Copilot/CopilotPanel.xaml` + `.xaml.cs` (stub: `<TextBlock Text="Copilot"/>` for now)

- [ ] **Step 1 — Stub panel.** Create `CopilotPanel` as a `UserControl` with `void SetRevitContext(UIApplication app)` storing `_uiApp`/`_uidoc` and (later) seeding the VM. Body: a placeholder `Border`.

- [ ] **Step 2 — Host.** Port `CostDashboardHost.cs` verbatim, renamed:
```csharp
public class CopilotPaneHost : Page, IDockablePaneProvider
{
    private CopilotPanel _panel;
    public static readonly DockablePaneId PaneId =
        new DockablePaneId(new Guid("B1A4C057-0001-4000-8000-00000000C0P1")); // pick a fresh GUID
    public CopilotPaneHost()
    {
        _panel = new CopilotPanel();
        this.Content = new Frame { Content = _panel,
            NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden };
    }
    public CopilotPanel Panel => _panel;
    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = this;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        data.VisibleByDefault = false;
    }
}
```
(Use a real, unique GUID — the placeholder above is not valid hex.)

- [ ] **Step 3 — Register in `App.cs`.** Add static prop near line 32; add a try/catch `RegisterDockablePane(CopilotPaneHost.PaneId, "BINA Revit Copilot", CopilotPaneHost)` block alongside the others (~line 88).

- [ ] **Step 4 — Command.** Port `CostDashboardCommand` → `OpenCopilotCommand`: get pane, `Show()`, call `App.CopilotPaneHost.Panel.SetRevitContext(uiApp)`.

- [ ] **Step 5 — Ribbon.** In `CreateRibbonTab`, replace the `AskAI` PushButtonData target class with `RevitWebAppSync.Commands.OpenCopilotCommand`, button name `OpenCopilot`, keep `microchip.png`. Leave `OpenAssistantCommand`/`AIAssistantWindow` in place for now (deleted in Task 16).

- [ ] **Step 6 — Verify (Windows).** Build; launch Revit; click AI Assistant button → an empty right-docked "BINA Revit Copilot" pane appears. Commit: `feat(copilot): dockable pane host + ribbon command (plumbing)`.

## Task 2: Design tokens, styles, theme loader

**Files:** Create `UI/Copilot/CopilotTokens.xaml`, `CopilotStyles.xaml`, `CopilotTheme.cs`.

- [ ] **Step 1 — Tokens.** Port `COPILOT_TOKENS` (shared.jsx:3-21) to `SolidColorBrush` resources, keys `Cp.Blue` `#2563eb`, `Cp.BlueHover` `#1d4ed8`, `Cp.BlueSoft` `#eff6ff`, `Cp.BlueText` `#1e40af`, `Cp.Ink` `#0b0d12`, `Cp.Ink2` `#1f2430`, `Cp.Text` `#374151`, `Cp.Muted` `#6b7280`, `Cp.Faint` `#9ca3af`, `Cp.Line` `#e5e7eb`, `Cp.LineSoft` `#f1f3f5`, `Cp.Bg` `#ffffff`, `Cp.PanelBg` `#fafafa`, `Cp.Hover` `#f3f4f6`, `Cp.Green` `#16a34a`, `Cp.Amber` `#d97706`, `Cp.Red` `#dc2626`. Plus purple `Cp.Purple` `#7c3aed`, `Cp.PurpleSoft` `#f5f3ff`, tier greens `#dcfce7`/`#15803d`. Add the 8 icon-tile palette pairs (README "Tool icon palettes"). Fonts: `Cp.Font` = `Geist, Segoe UI, sans-serif`; `Cp.FontMono` = `Geist Mono, Cascadia Mono, Consolas, monospace`.

- [ ] **Step 2 — Styles.** ControlTemplate-based styles (existing addin uses templated buttons in `Styles.xaml`):
  - `Cp.TabButton` (10×12 pad, fontsize 13, active = ink text + 2px bottom border).
  - `Cp.Chip` (radius 999, active = ink bg/white text).
  - `Cp.RunButton` (green `#16a34a`, white, radius 9) and `Cp.RunDark` (ink bg) and `Cp.Ghost` (transparent, 1px line).
  - `Cp.Card` border style (radius 10, 1px line, hover bg `#fafafa`).
  - `Cp.CodeBlock` (bg `#0b0d12`, fg `#e5e7eb`, mono, 10.5–11px).
  - `Cp.PillBase`.

- [ ] **Step 3 — Theme loader.** Copy `JkrTheme.cs` → `CopilotTheme.cs`, merge `UI/Copilot/CopilotTokens.xaml` then `CopilotStyles.xaml`. Call `CopilotTheme.EnsureLoaded()` in `CopilotPaneHost` ctor before building the panel.

- [ ] **Step 4 — Verify (Windows).** Build; pane still loads (no resource-not-found). Commit: `feat(copilot): design tokens + styles + theme loader`.

## Task 3: Icon set

**Files:** Create `UI/Copilot/CopilotIcons.cs`.

- [ ] **Step 1.** A `static readonly Dictionary<string, string>` of the 30 icon names → SVG path `d` strings from `shared.jsx:31-68` (send, sparkle, sparkleSolid, close, chevronDown/Right/Left, plus, search, code, play, copy, history, bookmark, layers, cube, door, wall, table, chart, filter, link, menu, more, check, warning, pin, undo, selection, attach, mic, bot). Provide `Geometry Get(string name)` via `Geometry.Parse`. Multi-path icons (e.g. `search`, `cube`) → combine into a `GeometryGroup` or store the composite path. Stroke vs fill noted per icon (most stroke 1.6; `play`/`sparkleSolid`/`more` are filled).

- [ ] **Step 2 — Verify.** Used by `IconTile` in Task 5; no standalone test. Commit: `feat(copilot): SVG icon geometry set`.

## Task 4: Catalog, models, store (macOS-testable)

**Files:** Create `UI/Copilot/Model/CopilotModels.cs`, `CopilotCatalog.cs`, `CopilotStateStore.cs`, `Model/RelayCommand.cs`. Test: `Tests/CopilotCatalogTests.cs`.

- [ ] **Step 1 — Models.** Define `ToolDef { Id, Title, Desc, IconGlyph, TileBg, TileFg, Category, Tier (1|2), List<FieldDef> Fields, List<string> Plan, string Code, Func<IDictionary<string,object>,string> RunLabel, Func<IDictionary<string,object>,string> PlanText, Func<IDictionary<string,object>,ResultModel> Result }`. `FieldDef { Id, Label, FieldKind Kind, string[] Options, object Default, Hint }`. `ResultModel { ResultKind Kind, Headline, Unit, Sub, Path, List<BarItem> Bars, List<IssueItem> Items, List<DiffItem> Diffs }`. Enums `Screen{Home,ToolForm,ToolReview,Running,Result}`, `Tab{Chat,Library,History,Saved}`, `MsgKind{User,Thinking,Clarify,Proposal,Running,Result}`, `ResultKind{Count,Issues,List,File,Plain}`, `FieldKind{Select,Text,Seg}`.

- [ ] **Step 2 — Catalog.** Port all 14 tools, `CATEGORIES`, `SEED_HISTORY` from `data.jsx`. Map prototype tool ids → existing backend tool names where wiring matters: `rename`→`rename_elements`, `set-param`→`set_parameter`, `export-sched`→`export_schedule`, `open-view`→`open_view`, `select`→`select_elements` (so Task 7 can call `VettedToolCode.TryBuild`).

- [ ] **Step 3 — Store.** `CopilotStateStore.Load()/Save(pinned, history)` → JSON at `%APPDATA%\RevitWebAppSync\copilot-state.json`. Seed from `SEED_HISTORY` + `{"count-doors"}` pinned on first run.

- [ ] **Step 4 — RelayCommand.** Standard `ICommand` with `Action<object>` + `Func<object,bool>`.

- [ ] **Step 5 — Test (macOS OK).**
```csharp
[Fact] public void Catalog_has_5_vetted_and_9_ai() {
    Assert.Equal(5, CopilotCatalog.Vetted.Count);
    Assert.Equal(9, CopilotCatalog.Ai.Count);
    Assert.All(CopilotCatalog.All, t => Assert.False(string.IsNullOrEmpty(t.Title)));
}
[Fact] public void Category_counts_match_tools() {
    foreach (var c in CopilotCatalog.Categories.Where(c => c.Id != "all"))
        Assert.Equal(c.Count, CopilotCatalog.All.Count(t => t.Category == c.Id));
}
```
Run: `dotnet test Tests/Tests.csproj --filter CopilotCatalog`. Expected: PASS. Commit: `feat(copilot): catalog + models + state store (+tests)`.

## Task 5: ViewModel + reusable controls (TierBadge, IconTile, ToolCard)

**Files:** Create `CopilotViewModel.cs`, `Controls/TierBadge.xaml(.cs)`, `Controls/IconTile.xaml(.cs)`, `Controls/ToolCard.xaml(.cs)`.

- [ ] **Step 1 — VM skeleton.** Properties mirroring `initialState` (app.jsx:4-17): `Screen`, `Tab`, `ToolId`, `ObservableCollection<...> Thread/History`, `HashSet<string> Pinned`, `Query`, `Category`, `Dictionary<string,object> FormValues`, `ResultModel RunResult`, `ObservableCollection<HighlightMarker> Highlights`, `Screen Prev`. Commands: `GoTabCommand`, `OpenToolCommand`, `BackCommand`, `BackHomeCommand`, `RunCommand`, `CancelRunCommand`, `SetCategoryCommand`, `PinCommand`/`UnpinCommand`, `ChatSendCommand`, `ClearChatCommand`, `ClearHighlightsCommand`. Port reducer logic into command handlers + a private `FinishRun()`.

- [ ] **Step 2 — IconTile.** `UserControl` with `Glyph`,`TileBg`,`TileFg`,`Size`,`Stroke` DPs → renders a `System.Windows.Shapes.Path` from `CopilotIcons.Get(Glyph)` inside a rounded `Border`.

- [ ] **Step 3 — TierBadge.** `Tier` DP (1/2). Tier1 = `#dcfce7`/`#15803d` "Vetted" + check-circle; Tier2 = `#f5f3ff`/`#7c3aed` "AI" + sparkle. Sizes sm/md.

- [ ] **Step 4 — ToolCard.** Binds a `ToolDef`. Vetted variant: 3px green left stripe + play glyph; AI variant: chevron. Optional pinned bookmark + "Saved" pill. Hover via trigger. `Click` → `OpenToolCommand`.

- [ ] **Step 5 — Verify (Windows).** Temporarily host one `ToolCard` + `TierBadge` in the stub panel to eyeball. Commit: `feat(copilot): viewmodel skeleton + IconTile/TierBadge/ToolCard`.

## Task 6: Panel chrome (header, tabs, breadcrumb, body switch)

**Files:** Modify `CopilotPanel.xaml(.cs)`; create a `ScreenTemplateSelector` (in `CopilotPanel.xaml.cs`).

- [ ] **Step 1 — Header** (README "Panel Header"): 28×28 gradient bot avatar (blue→purple) with star, title "Revit Copilot" 14/600, status line "● Connected · {modelName}" (green dot), `+` (→ `BackHomeCommand`) and `⋯` icon buttons.

- [ ] **Step 2 — Tabs** (app.jsx:315-338): Chat (sparkle, purple when active) · Library `{14}` · History `{n}` · Saved `{n}`. Hidden when `Screen` is a sub-screen (`ToolForm/ToolReview/Running/Result`) — use a `BoolToVisibility` (reuse `UI/Jkr/Converters.cs`).

- [ ] **Step 3 — Breadcrumb** (app.jsx:341-354): shown on `ToolForm`/`ToolReview` only: "{Chat|Library} › {tool.Title}".

- [ ] **Step 4 — Body.** `ContentControl Content="{Binding}"` + a template selector choosing the screen `UserControl` from `Screen`+`Tab` exactly like app.jsx:273-284 (sub-screen overrides tab).

- [ ] **Step 5 — Verify (Windows).** Tabs switch; sub-screen hides tabs. Commit: `feat(copilot): panel chrome — header, tabs, breadcrumb, body switch`.

## Task 7: Vetted tool form + executor wiring

**Files:** Create `Screens/ToolFormView.xaml(.cs)`; create form controls (`Controls/SegBar`, `FieldSelect`, `FieldInput` — inline in ToolFormView resources is fine).

- [ ] **Step 1 — Form UI** (screens.jsx:111-148, 694-794): ToolHeader (back, icon tile, title, Tier-1 badge, desc), one `FormField` per `tool.Fields` (select dropdown / text / segmented), live **Preview** card (`tool.PlanText(values)`), footer Cancel + green Run button labelled `tool.RunLabel(values)`.

- [ ] **Step 2 — Field controls.** `FieldSelect` (popup list w/ check), `FieldInput` (focus-border), `SegBar` (segmented). Two-way bind into `VM.FormValues` via `SetFormCommand`.

- [ ] **Step 3 — Execute.** On Run → `VM.RunCommand`: build C# via `VettedToolCode.TryBuild(tool.BackendName, FormValues)`; for `open_view`/`select_elements` use the native synthesizers already in `AIAssistantWindow.xaml.cs` (extract `BuildNativeSelectionCode`/open-view synth into a shared `Services/NativeToolCode.cs` so both old window and pane can call — or copy minimally). Set `App.AIHandler.CodeToExecute`, `OnCompleted`, `App.AIExternalEvent.Raise()`. Transition `Screen=Running`.

- [ ] **Step 4 — Verify (Windows + Revit).** Open "Set a parameter" on Walls, Run → real param set, transaction undoable. Commit: `feat(copilot): tier-1 tool form + executor wiring`.

## Task 8: Running + Result screens

**Files:** Create `Screens/RunningView.xaml(.cs)`, `Screens/ResultView.xaml(.cs)`.

- [ ] **Step 1 — Running** (screens.jsx:232-301): ToolHeader (no back, blue "Running…"), step rows (`RunStep`: 18×18 status circle ✓/pulse/•), info chip (vetted vs AI text), Cancel. Steps advance off the real `OnCompleted` callback (not fake timers): show "Starting transaction / Applying {tool} / Committing" for vetted; the 5 AI steps for Tier-2. On callback → `FinishRun()` → `Screen=Result`.

- [ ] **Step 2 — Result** (screens.jsx:304-465): Done header (green "Done · {elapsed}"), icon+title+tier, `ResultBody` selecting by `ResultKind` (count → total card + breakdown bars; issues → red list + Zoom-to; list → diff rows mono; file → xlsx tile; plain → gradient headline). "Next steps" rows (Save→Pin / View history / Undo→raise undo via `App.AIHandler.Action="undo"`). Follow-up `PromptBar`. Populate `Highlights` (Task 15) on finish.

- [ ] **Step 3 — Verify (Windows + Revit).** Run a vetted tool → see real result mapped into the right variant. Commit: `feat(copilot): running + result screens`.

## Task 9: Library tab

**Files:** Create `Screens/LibraryView.xaml(.cs)`; `Controls/SearchBar` inline.

- [ ] **Step 1** (screens.jsx:7-108): SearchBar (focus border), horizontal category chips (`CATEGORIES` w/ counts, active ink), scroll body: Recent row (when no query+all), Vetted section (TierBadge1 + "one click, no review") of `ToolCard vetted`, AI section (TierBadge2) of `ToolCard`, Ask-Copilot fallback when `Query` non-empty. Filter by `Category` + `Query` substring on title/desc. Bottom `PromptBar` (submitting switches to Chat).

- [ ] **Step 2 — Verify (Windows).** Search filters; category chips filter; clicking vetted → ToolForm, AI → ToolReview. Commit: `feat(copilot): library tab`.

## Task 10: History + Saved tabs + persistence

**Files:** Create `Screens/HistoryView.xaml(.cs)`, `Screens/SavedView.xaml(.cs)`. Wire `CopilotStateStore`.

- [ ] **Step 1 — History** (screens.jsx:487-526): header + run rows (status dot, icon tile, title, "{time} · {summary}", chevron). Click → `OpenToolCommand`.
- [ ] **Step 2 — Saved** (screens.jsx:529-562): empty state (bookmark illustration + copy) or `ToolCard` list of pinned tools.
- [ ] **Step 3 — Persist.** VM loads store on init; `Pin`/`Unpin`/`FinishRun` save. History capped (e.g. 100).
- [ ] **Step 4 — Verify (Windows).** Pin a result, restart Revit → still pinned; history persists. Commit: `feat(copilot): history + saved tabs with persistence`.

## Task 11: Chat empty state + prompt bar (no AI yet)

**Files:** Create `Screens/ChatView.xaml(.cs)`, `Controls/PromptBar.xaml(.cs)` (MentionInput stubbed as a plain `TextBox` until Task 14).

- [ ] **Step 1 — Empty state** (chat.jsx:198-294): greeting "Hi {firstName} 👋" (name from `Application.Username` / Revit user context, fallback "there"), subtitle, "Try one of these" 6 prompt cards (icons/colors per chat.jsx:199-206), topic chips (`doors walls fire rating rooms sheets levels`), Library CTA, "How runs work" info box (Vetted green / AI purple). Each prompt card / chip → `ChatSendCommand`.

- [ ] **Step 2 — Active thread** (chat.jsx:148-196): subheader ("Conversation · N messages", "+ New chat"→`ClearChatCommand`), `ItemsControl` of thread bound to `MessageTemplates.xaml` (Task 12 fills proposal/clarify; here render user + thinking). Auto-scroll to bottom on add.

- [ ] **Step 3 — PromptBar** (screens.jsx:812-839): bordered input + AI pill + dark send button + `@` hint. Enter / send → `ChatSendCommand(text, mentions)`.

- [ ] **Step 4 — Verify (Windows).** Chat empty state renders; typing a prompt appends a user bubble + thinking dots. Commit: `feat(copilot): chat empty state + prompt bar`.

## Task 12: Chat proposal flow (Tier-2 via AIService)

**Files:** Modify `CopilotViewModel.cs`, `Controls/MessageTemplates.xaml`.

- [ ] **Step 1 — Message templates.** Add `DataTemplate`s: thinking (bouncing dots), proposal (`InlineProposal`: header w/ tool icon+title+"Proposed command"+TierBadge2, Plan ordered list, "View code (N lines)" disclosure → dark code block, action row Regenerate/Open editor/Run), running (spinner bar), result (`CompactResult`: count/issues/file/plain compact). All per chat.jsx:414-621.

- [ ] **Step 2 — Send → route.** `ChatSendCommand`: append user msg; run `QueryInterpreter.Interpret(text)`. If `direct`: append `thinking`, then call `AIService.RouteAsync(text, BuildModelContext(), userId, sessionId, templateId, accessToken)` (async, off UI thread; marshal back via `Dispatcher`). On response, replace thinking with a `proposal` whose Plan = `RouteResponse.reply`/action descriptions and Code = first action's `Code` (or `VettedToolCode` synth preview). Keep the prototype's deterministic `pickResponseTool` as offline fallback when backend unreachable (mirrors README "Clarification Logic" guidance).

- [ ] **Step 3 — Proposal Run.** `ChatRunCommand(idx)`: set msg→running; resolve code (reuse Task 7 `ResolveActionCode` logic — extract into a shared `Services/CopilotDispatcher.cs`), raise ExternalEvent; on `OnCompleted` set msg→result + push history + highlights. Regenerate → re-call route. "Open editor" → `OpenToolCommand` (ToolReview).

- [ ] **Step 4 — Context.** `BuildModelContext()` from live `Document`: levels, categories present, active view name/type, selected ids (same shape as existing `ModelContext` in `AIAssistantWindow.xaml.cs`). Extract that builder into `Services/ModelContextBuilder.cs` and reuse.

- [ ] **Step 5 — Verify (Windows + Revit + backend).** Type "count doors by level" → thinking → proposal card with plan+code → Run → result count card; viewport highlights appear. Commit: `feat(copilot): tier-2 chat proposal flow via /route`.

## Task 13: Clarification cards

**Files:** Modify `Model/QueryInterpreter.cs`, `Controls/MessageTemplates.xaml`, VM.

- [ ] **Step 1.** Port `CLARIFICATIONS` matrix + vague-noun/verb logic (chat.jsx:31-146). `Interpret` returns `Direct(toolId)` or `Clarify(question, options[])`.
- [ ] **Step 2.** `clarify` `DataTemplate`: gradient header "I need a bit more detail", question, option buttons (icon tile + label + hint + chevron), footer "Or just rephrase…". Option click → `ChatSendCommand(option.Prompt)`.
- [ ] **Step 3 — Verify (Windows).** Type bare "doors" → clarify card with 4 options; clicking one proceeds to proposal. Commit: `feat(copilot): clarification cards`.

## Task 14: Mention input

**Files:** Replace `Controls/MentionInput` stub with full `RichTextBox` implementation.

- [ ] **Step 1.** `RichTextBox` (single-line styled) with placeholder overlay. On `@` → show a `Popup` above the input, grouped Levels/Categories/Views/Current selection (data from live `Document`; fallback to the static lists in README "Mention Picker Data"). Filter by substring after `@`. Selecting inserts a non-editable styled `InlineUIContainer` pill (colors per `MENTION_PILL_STYLE`, chat.jsx:636-641) + trailing space. Enter (picker closed) → submit `(text, mentions[])`. Dismiss on blur (150ms), selection, or `@` removed. (README "The Mention Input".)
- [ ] **Step 2.** Wire `ModelContextBuilder` to populate picker groups.
- [ ] **Step 3 — Verify (Windows + Revit).** Type "tag @Level 1 walls" → pill renders; submit yields mentions list; user bubble shows pill. Commit: `feat(copilot): @-mention input + picker`.

## Task 15: Viewport highlight overlay

**Files:** Create `Highlights/HighlightOverlay.cs`; wire from VM `FinishRun`.

- [ ] **Step 1 — Markers.** Port `highlightsFor(toolId, formValues)` (app.jsx:19-61) → `CopilotHighlights.For(toolId, values)` returning `HighlightMarker { XPct, YPct, OldLabel, NewLabel, Color, Dot, Warn }`.
- [ ] **Step 2 — Overlay.** Render markers over the active Revit view. Approach: a transparent topmost `Window` (or `Adorner` on the Revit main window) sized to `uidoc.GetOpenUIViews()[0].GetWindowRectangle()`. Project element bbox centers via `View` transform → screen (best-effort; for v1 use the prototype's %-based placement relative to the view rect since exact projection is the "last 10%"). Two marker modes: dot (glow + pulse) and label (old→new pill + dot), animations per README "Marker visual spec".
- [ ] **Step 3 — Clear chip.** Floating "N elements highlighted in model" chip at view top-center with Clear button → `ClearHighlightsCommand` + removes overlay. (README "Highlighted in model pill".)
- [ ] **Step 4 — Verify (Windows + Revit).** Run rename → yellow old→new pills float over viewport; Clear removes them. Commit: `feat(copilot): viewport highlight overlay + clear chip`.

## Task 16: Retire old AIAssistantWindow

**Files:** Delete `AIAssistantWindow.xaml(.cs)`, `Commands/OpenAssistantCommand.cs`; remove `App.AIHandler`/`AIExternalEvent` ONLY if unused elsewhere (they're reused by the pane — keep them). Remove dead refs.

- [ ] **Step 1.** Confirm pane reaches feature parity (all 5 vetted + chat + library + history + saved verified on Windows).
- [ ] **Step 2.** Delete the two old UI files + old command; ensure no remaining `new AIAssistantWindow(` references (grep).
- [ ] **Step 3 — Verify (Windows).** Build clean; ribbon button opens only the pane. Commit: `refactor(copilot): retire floating AIAssistantWindow`.

## Task 17: Polish — fonts, animations, edge states

- [ ] **Step 1.** Embed Geist/Geist Mono `.woff2` (or `.ttf`) under `Resources/fonts/`, register as `pack://` `FontFamily`, set `Cp.Font`. (Optional; fallback already works.)
- [ ] **Step 2.** Storyboards: thinking dots, spinner, marker pulse/fade, highlight chip slide-down (README "Animations").
- [ ] **Step 3.** Error states: backend unreachable → inline error card + offline `pickResponseTool` fallback; AI run failure → auto-retry once then surface raw exception + "Send to support" (README "Failure / retry"). Selection-awareness chip in PromptBar ("N selected · clear").
- [ ] **Step 4 — Verify (Windows + Revit).** Full clickthrough vs `prototype.html`. Commit: `feat(copilot): polish — fonts, animations, error/selection states`.

---

## Self-Review notes

- **Spec coverage:** Header/tabs (T6), Chat empty+thread (T11), clarify (T13), proposal+code disclosure (T12), running/result + 5 result variants (T8), library+search+categories+recent+fallback (T9), tool form + 5 vetted + preview (T7), tool review + amber strip (T12 templates / T7-style screen — ToolReviewView built in T7's pattern, used by "Open editor"), history (T10), saved (T10), mention input (T14), highlights + clear chip (T15), tier badges (T5), tokens/typography (T2), icons (T3), state machine/reducer (T5+T12), persistence (T10), failure/retry + selection-awareness (T17). ✅
- **ToolReviewView gap:** built alongside ToolForm — add its construction to Task 7 Step 1b (same ToolHeader + plan/code/amber + Run as the inline proposal, full-panel). Reuses MessageTemplates' proposal sub-pieces.
- **Threading:** every mutate path → ExternalEvent; async route calls marshalled via `Dispatcher.Invoke`. ✅
- **Build reality:** no macOS build; Tests project covers catalog/interpreter only. Windows verification gates each commit. ✅
</content>
</invoke>
