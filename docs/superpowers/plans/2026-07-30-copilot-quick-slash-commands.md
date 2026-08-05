# Copilot Quick Slash Commands (9 new "/" tools) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add 9 slash commands to the Copilot palette — 6 generic verbs (`/create /delete /change /rename /open-view /count`) in a new "Actions" category plus 3 task tools (`/clone /place /audit`) — with `/open-view` executing locally in the addin (no backend round-trip).

**Architecture:** All 9 are `SlashTool` entries in the existing `ToolCatalog` (UI already renders any catalog entry — palette, chips, routing need no structural change). 8 route to the backend via `command_id` exactly like the existing 20. `/open-view` gets a new `Local` flag: `ChatSendSlashCommand` short-circuits it to the existing Tier-1 vetted executor path (`CopilotCatalog` "open-view" → `RevitCopilotExecutor` `open_view` synthesized snippet), which already handles exact + substring view matching and `RequestViewChange` outside a transaction.

**Tech Stack:** C# / WPF Revit addin (net10.0-windows), xUnit tests.

## Global Constraints

- Branch: `feat/copilot-mention-all-categories` (current work branch; user commits on feat/* only, never develop).
- Build on macOS: `~/.dotnet/dotnet build <proj> -p:EnableWindowsTargeting=true` (official SDK 10.0.302 in `~/.dotnet`; homebrew dotnet lacks WindowsDesktop). Tests COMPILE locally but only EXECUTE on Windows CI — local "test" steps mean build the Tests project and expect `Build succeeded`.
- Do not rename or restructure existing files; no parallel v2 files.
- Backend `command_id` definitions (bina-ai repo `app/commands/*.md`) are OUT OF SCOPE here — see final section for the follow-up list.
- Existing behavior of the 20 shipped tools must not change (ids, backend ids, categories, order).

---

### Task 1: Catalog — `Local` flag, "Actions" category, 9 new SlashTool entries, icons

**Files:**
- Modify: `UI/Copilot/Model/ToolCatalog.cs`
- Create: `Tests/SlashToolCatalogTests.cs`
- Modify: `Tests/Tests.csproj` (add test file is automatic — project has explicit Compile items ONLY for linked addin sources; test files in `Tests/` are globbed. Verify `ToolCatalog.cs` is already linked at line ~81 — it is; change nothing.)

**Interfaces:**
- Consumes: existing `SlashTool`, `ToolCatalog.All`, `ToolCatalog.Categories`, `_backendIds`, `IconData`.
- Produces: `SlashTool.Local` (public bool, default false); 9 new catalog ids: `create`, `delete`, `change`, `rename`, `open-view`, `count`, `clone`, `place`, `audit`; category `"Actions"` first in `ToolCatalog.Categories`. Task 2 relies on `ToolCatalog.ById("open-view").Local == true`.

- [x] **Step 1: Write the failing test**

Create `Tests/SlashToolCatalogTests.cs`:

```csharp
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class SlashToolCatalogTests
    {
        [Fact]
        public void Catalog_has_29_tools_and_no_duplicate_ids()
        {
            Assert.Equal(29, ToolCatalog.All.Count);
            Assert.Equal(29, ToolCatalog.All.Select(t => t.Id).Distinct().Count());
        }

        [Fact]
        public void Actions_category_exists_first_with_the_6_verbs()
        {
            Assert.Equal("Actions", ToolCatalog.Categories[0]);
            var actions = ToolCatalog.All.Where(t => t.Category == "Actions").Select(t => t.Id).ToArray();
            Assert.Equal(new[] { "create", "delete", "change", "rename", "open-view", "count" }, actions);
        }

        [Fact]
        public void New_tools_map_to_backend_command_ids()
        {
            Assert.Equal("quick-create", ToolCatalog.ById("create").BackendId);
            Assert.Equal("quick-delete", ToolCatalog.ById("delete").BackendId);
            Assert.Equal("quick-change", ToolCatalog.ById("change").BackendId);
            Assert.Equal("quick-rename", ToolCatalog.ById("rename").BackendId);
            Assert.Equal("model-count", ToolCatalog.ById("count").BackendId);
            Assert.Equal("clone-sheet", ToolCatalog.ById("clone").BackendId);
            Assert.Equal("place-family", ToolCatalog.ById("place").BackendId);
            Assert.Equal("name-audit", ToolCatalog.ById("audit").BackendId);
        }

        [Fact]
        public void OpenView_is_the_only_local_tool()
        {
            Assert.True(ToolCatalog.ById("open-view").Local);
            Assert.Single(ToolCatalog.All.Where(t => t.Local));
        }

        [Fact]
        public void Existing_20_tools_unchanged()
        {
            // Guard: the original ids all still present with original backend ids.
            Assert.Equal("level-visualiser", ToolCatalog.ById("level-vis").BackendId);
            Assert.Equal("ff-from-picked-cad", ToolCatalog.ById("ff-pick").BackendId);
            Assert.Equal(20, ToolCatalog.All.Count(t =>
                t.Category == "General" || t.Category == "Architecture" ||
                t.Category == "Structure" || t.Category == "MEP") - 3); // 3 new non-Actions tools land in General
        }

        [Fact]
        public void Every_new_tool_has_name_subtitle_keywords_icon()
        {
            var ids = new[] { "create", "delete", "change", "rename", "open-view", "count", "clone", "place", "audit" };
            foreach (var id in ids)
            {
                var t = ToolCatalog.ById(id);
                Assert.NotNull(t);
                Assert.False(string.IsNullOrEmpty(t.Name));
                Assert.False(string.IsNullOrEmpty(t.Subtitle));
                Assert.False(string.IsNullOrEmpty(t.Keywords));
                Assert.False(string.IsNullOrEmpty(t.IconKey));
            }
        }
    }
}
```

- [x] **Step 2: Build Tests project to verify it fails**

Run: `~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true 2>&1 | tail -5`
Expected: FAIL — `'SlashTool' does not contain a definition for 'Local'` (compile error is this task's red state; count assertions go red on CI only).

- [x] **Step 3: Implement catalog changes**

In `UI/Copilot/Model/ToolCatalog.cs`:

3a. Add to `SlashTool` class (after `Keywords`):

```csharp
        /// <summary>True = handled entirely in the addin (no backend command).
        /// The chat send path short-circuits these — see ChatSendSlashCommand.</summary>
        public bool Local;
```

3b. Categories — replace the array line:

```csharp
        // Category order in the palette (Quick access is prepended dynamically).
        public static readonly string[] Categories = { "Actions", "General", "Architecture", "Structure", "MEP" };
```

3c. Insert the 9 entries at the TOP of the `All` list (before `level-vis`), keeping the existing 20 untouched below:

```csharp
            // ── Actions: generic verb commands (2026-07 Langfuse prod mining) ──
            new SlashTool { Id="create",    Category="Actions", Name="Create",             Subtitle="Create elements from description",              Badge=ToolBadge.AiAssisted,    IconKey="ti-plus",            Keywords="create make add new build buat bina tambah letak" },
            new SlashTool { Id="delete",    Category="Actions", Name="Delete",             Subtitle="Delete by selection / filter — preview first",  Badge=ToolBadge.Deterministic, IconKey="ti-trash",           Keywords="delete remove erase clear padam buang hapus" },
            new SlashTool { Id="change",    Category="Actions", Name="Change",             Subtitle="Swap type / material / parameter",              Badge=ToolBadge.Deterministic, IconKey="ti-exchange",        Keywords="change swap replace type material parameter tukar ganti" },
            new SlashTool { Id="rename",    Category="Actions", Name="Rename",             Subtitle="Bulk rename by pattern",                        Badge=ToolBadge.Deterministic, IconKey="ti-pencil",          Keywords="rename name pattern batch namakan nama semula" },
            new SlashTool { Id="open-view", Category="Actions", Name="Open View",          Subtitle="Open view / sheet by name",                     Badge=ToolBadge.Deterministic, IconKey="ti-eye",             Keywords="open view sheet goto jump navigate buka papar", Local=true },
            new SlashTool { Id="count",     Category="Actions", Name="Count / List",       Subtitle="Count & list model contents + loaded families", Badge=ToolBadge.Report,        IconKey="ti-sum",             Keywords="count list how many total inventory berapa kira senarai family loaded" },
            // ── Task tools from the same mining round ──
            new SlashTool { Id="clone",     Category="General", Name="Clone Sheet",        Subtitle="Duplicate template sheet per room / level",     Badge=ToolBadge.Deterministic, IconKey="ti-copy",            Keywords="clone duplicate sheet template copy salin" },
            new SlashTool { Id="place",     Category="General", Name="Place at Selection", Subtitle="Place family relative to selection",            Badge=ToolBadge.AiAssisted,    IconKey="ti-map-pin",         Keywords="place put position spacing selection letak jarak bawah tepi" },
            new SlashTool { Id="audit",     Category="General", Name="Name Audit",         Subtitle="JKR name audit → fix via /rename",              Badge=ToolBadge.Report,        IconKey="ti-clipboard-check", Keywords="audit naming standard jkr compliance check semak nama" },
```

3d. Add to `_backendIds` (before the closing brace comment):

```csharp
            ["create"] = "quick-create",
            ["delete"] = "quick-delete",
            ["change"] = "quick-change",
            ["rename"] = "quick-rename",
            ["count"] = "model-count",
            ["clone"] = "clone-sheet",
            ["place"] = "place-family",
            ["audit"] = "name-audit",
            // open-view is Local — never sent to the backend; BackendId falls back to Id.
```

3e. `SectionIconKey` — add case above `"General"`:

```csharp
                case "Actions": return "ti-command";
```

3f. Add to `IconData` (Tabler 24×24 stroke paths, same conversion style as existing):

```csharp
            ["ti-plus"]            = "M12,5 v14 M5,12 h14",
            ["ti-trash"]           = "M4,7 h16 M10,11 v6 M14,11 v6 M5,7 l1,12 a2,2 0 0 0 2,2 h8 a2,2 0 0 0 2,-2 l1,-12 M9,7 V4 a1,1 0 0 1 1,-1 h4 a1,1 0 0 1 1,1 v3",
            ["ti-exchange"]        = "M21,7 H3 M18,10 l3,-3 -3,-3 M3,17 h18 M6,20 l-3,-3 3,-3",
            ["ti-pencil"]          = "M4,20 h4 L18.5,9.5 a2.828,2.828 0 1 0 -4,-4 L4,16 v4 M13.5,6.5 l4,4",
            ["ti-eye"]             = "M10,12 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M21,12 c-2.4,4 -5.4,6 -9,6 c-3.6,0 -6.6,-2 -9,-6 c2.4,-4 5.4,-6 9,-6 c3.6,0 6.6,2 9,6",
            ["ti-sum"]             = "M18,16 v2 a1,1 0 0 1 -1,1 H6 l6,-7 -6,-7 h11 a1,1 0 0 1 1,1 v2",
            ["ti-copy"]            = "M10,8 h8 a2,2 0 0 1 2,2 v8 a2,2 0 0 1 -2,2 h-8 a2,2 0 0 1 -2,-2 v-8 a2,2 0 0 1 2,-2 z M16,8 V6 a2,2 0 0 0 -2,-2 H6 a2,2 0 0 0 -2,2 v8 a2,2 0 0 0 2,2 h2",
            ["ti-map-pin"]         = "M9,11 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 M17.657,16.657 L13.414,20.9 a2,2 0 0 1 -2.827,0 l-4.244,-4.243 a8,8 0 1 1 11.314,0 z",
            ["ti-clipboard-check"] = "M9,5 H7 a2,2 0 0 0 -2,2 v12 a2,2 0 0 0 2,2 h10 a2,2 0 0 0 2,-2 V7 a2,2 0 0 0 -2,-2 h-2 M9,3 h6 a1,1 0 0 1 1,1 v1 a1,1 0 0 1 -1,1 H9 a1,1 0 0 1 -1,-1 V4 a1,1 0 0 1 1,-1 z M9,14 l2,2 4,-4",
```

- [x] **Step 4: Build Tests project to verify it compiles**

Run: `~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true 2>&1 | tail -3`
Expected: `Build succeeded` (assertion execution happens on Windows CI).

- [x] **Step 5: Build the addin project**

Run: `~/.dotnet/dotnet build RevitWebAppSync.csproj -p:EnableWindowsTargeting=true 2>&1 | tail -3`
Expected: `0 Error(s)`.

- [x] **Step 6: Commit**

```bash
git add UI/Copilot/Model/ToolCatalog.cs Tests/SlashToolCatalogTests.cs
git commit -m "feat(copilot): 9 quick slash commands in new Actions category"
```

---

### Task 2: Local `/open-view` dispatch (no backend round-trip)

**Files:**
- Modify: `UI/Copilot/CopilotViewModel.cs:873-880` (`ChatSendSlashCommand`)

**Interfaces:**
- Consumes: `ToolCatalog`/`SlashTool.Local` from Task 1; existing `CopilotCatalog.Vetted` ToolDef `Id="open-view"` (`UI/Copilot/Model/CopilotCatalog.cs:20`); `ICopilotExecutor.Run(ToolDef, values, code, onDone)` (`Executor` property, already wired to `RevitCopilotExecutor` which synthesizes the `open_view` snippet — exact-then-substring view match, `RequestViewChange`, `UI/Copilot/RevitCopilotExecutor.cs:242`); `ChatMessage`/`CpMsgKind` (`UI/Copilot/Model/CopilotModels.cs:9`).
- Produces: nothing consumed later; behavior only.

- [x] **Step 1: Implement the local branch**

Replace `ChatSendSlashCommand` body and add `RunLocalSlash` below it:

```csharp
        /// <summary>Slash command sent from the composer (P2). Routes the picked
        /// command to the backend via `command_id` — the definition's instructions
        /// and tool allowlist are injected server-side — and renders the user turn
        /// as a command chip plus any typed args. Local tools (open-view) never
        /// leave the addin: they reuse the Tier-1 vetted executor snippet.</summary>
        public void ChatSendSlashCommand(SlashTool tool, string args)
        {
            if (tool == null) return;
            if (tool.Local) { RunLocalSlash(tool, (args ?? "").Trim()); return; }
            ChatSend((args ?? "").Trim(), slashChip: tool);
        }

        private void RunLocalSlash(SlashTool tool, string args)
        {
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = args, SlashCommand = tool, Time = System.DateTime.Now.ToString("h:mm tt") });

            if (tool.Id != "open-view") return;   // only local tool today

            if (string.IsNullOrEmpty(args))
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = "Name a view to open — e.g. `/open-view Aras 01 WIP`. Type `@` to pick one." });
                return;
            }

            var def = CopilotCatalog.Vetted.FirstOrDefault(t => t.Id == "open-view");
            if (def == null || Executor == null)
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = "No Revit context — open a document first." });
                return;
            }

            var values = new Dictionary<string, object> { ["view"] = args };
            Executor.Run(def, values, null, outcome =>
            {
                // Same thread contract as the chat codegen path (Done at :1298) —
                // the executor completes on the UI thread via its ExternalEvent.
                string text = outcome != null && outcome.Success
                    ? (string.IsNullOrEmpty(outcome.Message) ? "Opened." : outcome.Message)
                    : "Couldn't open that view" + (string.IsNullOrEmpty(outcome?.Error) ? "." : ": " + outcome.Error);
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, ToolId = tool.Id, Text = text });
                AppendToCurrentSession("/open-view " + args, text, outcome != null && outcome.Success ? "ok" : "warn", new List<string> { tool.Id });
            });
        }
```

Note for implementer: the executor snippet's `SetResult(new { kind = "plain", headline = "Opened …" })` lands in `outcome.Data` (JSON) with `outcome.Message` possibly null — if a quick manual check shows `Message` empty on success, parse `headline` from `outcome.Data` instead:
`Newtonsoft.Json.Linq.JObject.Parse(outcome.Data)["headline"]?.ToString()`. Keep whichever renders "Opened Aras 01 WIP".

- [x] **Step 2: Build the addin project**

Run: `~/.dotnet/dotnet build RevitWebAppSync.csproj -p:EnableWindowsTargeting=true 2>&1 | tail -3`
Expected: `0 Error(s)`.

- [x] **Step 3: Manual smoke script (Windows/Revit — record as follow-up if run later)**

In Revit: open Copilot → type `/` → palette shows "Actions" section first with 6 verbs → pick Open View → type `Aras 01` → Enter. Expected: active view switches, chat shows chip bubble + "Opened Aras 01…" reply, no network call (backend logs quiet).

- [x] **Step 4: Commit**

```bash
git add UI/Copilot/CopilotViewModel.cs
git commit -m "feat(copilot): /open-view runs locally via vetted executor"
```

---

## Self-review notes

- Spec coverage: 9 commands (user list minus rejected `/clearance`) — Task 1; local open-view — Task 2. Palette rendering/search/chips need no change (verified: `CommandPalette.cs:211` iterates `ToolCatalog.Categories`; chips and routing read the `SlashTool` object).
- `rename` id collision check: `ToolCatalog` ids are a separate namespace from `CopilotCatalog` ToolDef ids (`rename` exists there as Tier-1) — `ToolCatalog.ById` only searches `ToolCatalog.All`. No conflict.
- Types: `Local` bool consumed as `tool.Local` in Task 2; backend ids asserted in Task 1 tests match the 3d map exactly.

## Out of scope — bina-ai backend follow-up (separate repo/branch)

8 new command definitions under `app/commands/`: `quick-create.md`, `quick-delete.md`, `quick-change.md`, `quick-rename.md`, `model-count.md`, `clone-sheet.md`, `place-family.md`, `name-audit.md`. Until they exist, the 8 backend-routed commands still work — the router treats an unknown `command_id` as a plain prompt with the chip text (verify this fallback when writing the backend branch). `/open-view` needs nothing server-side.
