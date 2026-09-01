# Saved Commands J1 — Addin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the Revit copilot pane, let a drafter save a completed turn as a private slash command with typed inputs, see it under a "Mine" group in the `/` palette, and re-run it with inline input chips.

**Architecture:** The static `ToolCatalog.All` becomes the union of the hardcoded curated list and a remote list fetched from `GET /revit-copilot/commands` (group `mine`, listed first). A "Save as command" footer action on a completed AI reply opens a `SaveCommandSheet` where the user marks `{inputs}` by selecting text; the sheet POSTs to `/revit-copilot/commands`. Picking a Mine command in the palette renders one `InputChip` per input in the prompt bar; values ride the existing `PendingCommandArgs` → `command_args` wire. No sharing, no versions, no LLM.

**Tech Stack:** C# / WPF (net48 + net8.0-windows multi-target), Newtonsoft.Json, xUnit (`Tests/Tests.csproj`). Backend contract from `bina-ai/docs/superpowers/plans/2026-08-30-saved-commands-j1-backend.md`.

**Spec:** `bina-ai/docs/command_builder/2026-08-30-saved-commands-J1-PRD.md` (§3 flow, §4.2 A1–A6)

## Global Constraints

- Match `UI/Copilot` visual tokens exactly: `Cp.Font` (Inter/Segoe UI), `Cp.Accent` `#2a69c6`, `Cp.PanelBg`, `Cp.Line`; composer `CornerRadius=14`; chips `CornerRadius=8` / `FontSize=11.5` / weight 680 (`Controls/CommandChip.cs`); palette `CornerRadius=13`, rows `CornerRadius=9`, tiles 30×30 `CornerRadius=8`, section header `FontSize=10 Bold` uppercase (`Controls/CommandPalette.cs`). Use `SetResourceReference` with `Cp.*` keys, never hex literals, so dark mode follows.
- Design reference: Claude Design canvas "Saved Commands" (4 artboards) — https://claude.ai/code/artifact/d3e9edca-2465-4a88-9bbe-b17446c4ff7f
- Compile-gate every task locally on the Mac before moving on: `~/.dotnet/dotnet build RevitWebAppSync.csproj -c Debug -f net48 --no-restore 2>&1 | tail -3` (0 errors). Trap: net48 `ElementId.Value` is `int`. C# language level supports target-typed `new()` (see `ToolLoopOutcome`).
- Tests: `~/.dotnet/dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SavedCommands" 2>&1 | tail -5`.
- Backend wire: `GET /revit-copilot/commands` returns `{version, commands:[{id, group, engine, name_en, name_ms, description_en, icon, keywords[], args:[{name,type,source,required,label_en}], tools[], credit_cost, status}]}` with `ETag`. `POST /revit-copilot/commands` body `{name_en, name_ms, prompt_template, args[], tools_called[], source_run_id}` → 201 `{command, prompt_template, source_run_id, run_count}`. `PATCH /commands/{id}` same body. `DELETE /commands/{id}` → 204. Bearer = the copilot access token (same as `GetPromptLibraryAsync`).
- Input hole syntax in a template: `{snake_name}` — `[a-z][a-z0-9_]{0,39}`. Max 8 inputs.
- Commit after every task (Conventional Commits). Do not push. Ships in the **same batch** as backend `0015_user_commands`; sequence after vibe R0 (#119) reaches staging.

---

## File map

| File | Responsibility |
|---|---|
| `UI/Copilot/Model/ToolCatalog.cs` | `SlashTool` gains `Inputs`, `PromptTemplate`, `Editable`; `"Mine"` category first; `MergeRemote(...)` builds `All` = mine + curated. |
| `UI/Copilot/Model/SavedCommandDraft.cs` (new) | Pure draft model: template text, inputs, `MarkInput`/`UnmarkInput`, `ToRequest()`. Unit-tested. |
| `Models/SavedCommandDtos.cs` (new) | Wire DTOs for the catalog + CRUD. |
| `Services/AiService.cs` | `GetCommandsAsync`, `SaveCommandAsync`, `UpdateCommandAsync`, `DeleteCommandAsync`. |
| `UI/Copilot/Model/ChatRouter.cs`, `RevitChatRouter.cs`, `Model/CopilotModels.cs` | `RunId` + `SourcePrompt` flow onto the AI reply message. |
| `UI/Copilot/Controls/SaveCommandSheet.xaml(.cs)` (new) | The save/edit sheet. |
| `UI/Copilot/Controls/InputChip.cs` (new) | Inline input chip for the prompt bar. |
| `UI/Copilot/Controls/CommandPalette.cs` | Mine section, input-count badge, ⋯ menu. |
| `UI/Copilot/Controls/PromptBar.xaml.cs` | Input chips strip; required-empty blocks send; args → `PendingCommandArgs`. |
| `UI/Copilot/Screens/ChatView.xaml.cs`, `Controls/CopilotMessageBubble.cs` | Footer "Save as command" action. |
| `UI/Copilot/CopilotViewModel.cs` | Catalog refresh on sign-in / after save+delete; opens the sheet; passes args. |
| `Tests/SavedCommandsDraftTests.cs`, `Tests/SavedCommandsCatalogTests.cs` (new) | xUnit. |

---

### Task 1: `SlashTool` inputs + remote merge in `ToolCatalog`

**Files:**
- Modify: `UI/Copilot/Model/ToolCatalog.cs:13-43` (`SlashTool`, `Categories`, `All`)
- Test: `Tests/SavedCommandsCatalogTests.cs` (new)

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SlashInput { public string Name; public string Type = "text"; public bool Required = true; public string Label; }
  // on SlashTool:
  public List<SlashInput> Inputs = new();      // empty for curated tools
  public string PromptTemplate;                // user tier only
  public bool Editable;                        // true = user tier (Rename/Edit/Delete)
  // on ToolCatalog:
  public static readonly string[] Categories = { "Mine", "Actions", "General", "Architecture", "Structure", "MEP" };
  public static IReadOnlyList<SlashTool> All { get; }          // now a property: Mine + Curated
  public static IReadOnlyList<SlashTool> Curated { get; }      // the old hardcoded list
  public static void MergeRemote(IEnumerable<SlashTool> mine);  // replaces the Mine set, rebuilds All + id map
  public static SlashTool FromCatalogEntry(Models.CatalogCommandDto d); // group=="mine" only; else null
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/SavedCommandsCatalogTests.cs
using System.Linq;
using RevitWebAppSync.Models;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class SavedCommandsCatalogTests
    {
        private static CatalogCommandDto Mine(string id = "my-walls-from-cad") => new CatalogCommandDto
        {
            Id = id, Group = "mine", Engine = "ai", NameEn = "Walls from CAD",
            DescriptionEn = "walls on {level}",
            Args = new() { new CatalogArgDto { Name = "level", Type = "text", Required = true, LabelEn = "Level" } },
            Tools = new() { "list_levels" },
        };

        [Fact]
        public void MergeRemote_puts_mine_first_and_keeps_curated()
        {
            var before = ToolCatalog.Curated.Count;
            ToolCatalog.MergeRemote(new[] { ToolCatalog.FromCatalogEntry(Mine()) });
            Assert.Equal(before + 1, ToolCatalog.All.Count);
            Assert.Equal("my-walls-from-cad", ToolCatalog.All[0].Id);
            Assert.Equal("Mine", ToolCatalog.All[0].Category);
            Assert.True(ToolCatalog.All[0].Editable);
            Assert.Single(ToolCatalog.All[0].Inputs);
            Assert.Equal("Level", ToolCatalog.All[0].Inputs[0].Label);
            Assert.NotNull(ToolCatalog.ById("my-walls-from-cad"));
            ToolCatalog.MergeRemote(System.Array.Empty<SlashTool>());
            Assert.Equal(before, ToolCatalog.All.Count);
            Assert.Null(ToolCatalog.ById("my-walls-from-cad"));
        }

        [Fact]
        public void FromCatalogEntry_ignores_curated_groups()
        {
            var d = Mine(); d.Group = "architecture";
            Assert.Null(ToolCatalog.FromCatalogEntry(d));
        }

        [Fact]
        public void Mine_is_first_category()
        {
            Assert.Equal("Mine", ToolCatalog.Categories[0]);
        }
    }
}
```

- [ ] **Step 2: Create the DTOs** (`Models/SavedCommandDtos.cs`) — the test needs them:

```csharp
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitWebAppSync.Models
{
    public class CatalogArgDto
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("type")] public string Type { get; set; } = "text";
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("required")] public bool Required { get; set; }
        [JsonProperty("label_en")] public string LabelEn { get; set; } = "";
    }

    public class CatalogCommandDto
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("engine")] public string Engine { get; set; }
        [JsonProperty("name_en")] public string NameEn { get; set; }
        [JsonProperty("name_ms")] public string NameMs { get; set; }
        [JsonProperty("description_en")] public string DescriptionEn { get; set; } = "";
        [JsonProperty("icon")] public string Icon { get; set; }
        [JsonProperty("keywords")] public List<string> Keywords { get; set; } = new();
        [JsonProperty("args")] public List<CatalogArgDto> Args { get; set; } = new();
        [JsonProperty("tools")] public List<string> Tools { get; set; } = new();
        [JsonProperty("status")] public string Status { get; set; } = "live";
    }

    public class CatalogResponseDto
    {
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("commands")] public List<CatalogCommandDto> Commands { get; set; } = new();
    }

    public class SaveCommandRequestDto
    {
        [JsonProperty("name_en")] public string NameEn { get; set; }
        [JsonProperty("name_ms")] public string NameMs { get; set; } = "";
        [JsonProperty("prompt_template")] public string PromptTemplate { get; set; }
        [JsonProperty("args")] public List<CatalogArgDto> Args { get; set; } = new();
        [JsonProperty("tools_called")] public List<string> ToolsCalled { get; set; } = new();
        [JsonProperty("source_run_id")] public string SourceRunId { get; set; }
    }

    public class SaveCommandResponseDto
    {
        [JsonProperty("command")] public CatalogCommandDto Command { get; set; }
        [JsonProperty("prompt_template")] public string PromptTemplate { get; set; }
        [JsonProperty("run_count")] public int RunCount { get; set; }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `~/.dotnet/dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SavedCommandsCatalog" 2>&1 | tail -5`
Expected: build error — `SlashInput`/`MergeRemote`/`Curated` missing.

- [ ] **Step 4: Implement** — in `ToolCatalog.cs`:

Add before `SlashTool`:

```csharp
    public sealed class SlashInput
    {
        public string Name;
        public string Type = "text";   // text | number
        public bool Required = true;
        public string Label;
    }
```

Add to `SlashTool`:

```csharp
        /// <summary>User-tier (Mine) commands: typed inputs rendered as chips in the
        /// prompt bar; the template the Save sheet reopens for Edit; Editable gates
        /// the ⋯ menu. Curated tools leave these empty/false.</summary>
        public List<SlashInput> Inputs = new();
        public string PromptTemplate;
        public bool Editable;
```

Replace `Categories` and the `All` declaration:

```csharp
        public static readonly string[] Categories = { "Mine", "Actions", "General", "Architecture", "Structure", "MEP" };

        // The hardcoded curated list (unchanged content) — was `All`.
        public static readonly IReadOnlyList<SlashTool> Curated = new List<SlashTool>
        {
            // ... existing entries verbatim ...
        };

        private static List<SlashTool> _mine = new();
        private static List<SlashTool> _all = new(Curated);
        private static Dictionary<string, SlashTool> _byId = Curated.ToDictionary(t => t.Id);

        /// <summary>Mine first, then curated. Rebuilt by MergeRemote.</summary>
        public static IReadOnlyList<SlashTool> All => _all;

        /// <summary>Replace the user tier with the rows the catalog just returned
        /// (group == "mine"). Curated entries are never touched; id map rebuilt so
        /// palette pins and ById keep working for both tiers.</summary>
        public static void MergeRemote(IEnumerable<SlashTool> mine)
        {
            _mine = (mine ?? Enumerable.Empty<SlashTool>()).Where(t => t != null).ToList();
            _all = _mine.Concat(Curated).ToList();
            _byId = new Dictionary<string, SlashTool>();
            foreach (var t in _all) _byId[t.Id] = t;
        }

        public static SlashTool FromCatalogEntry(Models.CatalogCommandDto d)
        {
            if (d == null || d.Group != "mine" || d.Status == "disabled") return null;
            return new SlashTool
            {
                Id = d.Id, CommandId = d.Id, Category = "Mine",
                Name = d.NameEn, Subtitle = d.DescriptionEn ?? "",
                Badge = ToolBadge.AiAssisted, IconKey = "ti-user",
                Keywords = string.Join(" ", d.Keywords ?? new List<string>()),
                Editable = true, PromptTemplate = d.DescriptionEn,
                Inputs = (d.Args ?? new List<Models.CatalogArgDto>()).Select(a => new SlashInput
                {
                    Name = a.Name, Type = a.Type == "number" ? "number" : "text",
                    Required = a.Required, Label = string.IsNullOrEmpty(a.LabelEn) ? a.Name : a.LabelEn,
                }).ToList(),
            };
        }
```

Update `ById` to read `_byId` (it currently reads the static ctor's map — point it at `_byId`). Ensure `SectionIconKey("Mine")` returns `"ti-user"` (add a case).

Add three entries to `IconData` (`ToolCatalog.cs:146`, stroke-drawn like the others; none of these keys exist today):

```csharp
            ["ti-user"]                = "M12,7 m-4,0 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M6,21 v-2 a4,4 0 0 1 4,-4 h4 a4,4 0 0 1 4,4 v2",
            ["ti-device-floppy"]       = "M6,4 h10 l4,4 v10 a2,2 0 0 1 -2,2 H6 a2,2 0 0 1 -2,-2 V6 a2,2 0 0 1 2,-2 M12,14 m-2,0 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M14,4 v4 H8 V4",
            ["ti-dots-vertical"]       = "M12,12 m-1,0 a1,1 0 1 0 2,0 a1,1 0 1 0 -2,0 M12,19 m-1,0 a1,1 0 1 0 2,0 a1,1 0 1 0 -2,0 M12,5 m-1,0 a1,1 0 1 0 2,0 a1,1 0 1 0 -2,0",
```

- [ ] **Step 5: Run tests + compile gate**

Run: `~/.dotnet/dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SavedCommandsCatalog" 2>&1 | tail -5`
Expected: `Passed! - Failed: 0, Passed: 3`.
Run: `~/.dotnet/dotnet build RevitWebAppSync.csproj -c Debug -f net48 --no-restore 2>&1 | tail -3` → 0 errors.

- [ ] **Step 6: Commit**

```bash
git add UI/Copilot/Model/ToolCatalog.cs Models/SavedCommandDtos.cs Tests/SavedCommandsCatalogTests.cs
git commit -m "feat(commands): SlashTool inputs + Mine tier merged into ToolCatalog"
```

---

### Task 2: `AiService` — catalog GET + CRUD

**Files:**
- Modify: `Services/AiService.cs` (add after `GetPromptLibraryAsync`, `:351`)
- Test: `Tests/AiServiceUrlTests.cs` (append)

**Interfaces:**
- Produces:
  ```csharp
  public async Task<CatalogResponseDto> GetCommandsAsync(string accessToken, string etag, CancellationToken ct); // null on 304/failure; sets LastCommandsEtag
  public string LastCommandsEtag { get; private set; }
  public async Task<SaveCommandResponseDto> SaveCommandAsync(SaveCommandRequestDto body, string accessToken, CancellationToken ct);   // throws InvalidOperationException(detail) on 422
  public async Task<SaveCommandResponseDto> UpdateCommandAsync(string commandId, SaveCommandRequestDto body, string accessToken, CancellationToken ct);
  public async Task<bool> DeleteCommandAsync(string commandId, string accessToken, CancellationToken ct);  // false on 404
  public static string CommandsUrl(string baseUrl) => $"{baseUrl}/revit-copilot/commands";
  ```

- [ ] **Step 1: Write the failing test** (append to `Tests/AiServiceUrlTests.cs`)

```csharp
        [Fact]
        public void CommandsUrl_uses_revit_copilot_prefix()
        {
            Assert.Equal("https://x.test/revit-copilot/commands", AiService.CommandsUrl("https://x.test"));
        }
```

- [ ] **Step 2: Run to verify it fails**

Run: `~/.dotnet/dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CommandsUrl" 2>&1 | tail -3`
Expected: build error — `CommandsUrl` missing.

- [ ] **Step 3: Implement** — copy the request/auth shape of `GetPromptLibraryAsync` (`:351`): `HttpRequestMessage`, `Authorization = Bearer accessToken`, `_httpClient.SendAsync`, Newtonsoft deserialize.

```csharp
        public static string CommandsUrl(string baseUrl) => $"{baseUrl}/revit-copilot/commands";
        public string LastCommandsEtag { get; private set; }

        public async Task<CatalogResponseDto> GetCommandsAsync(string accessToken, string etag, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, CommandsUrl(_baseUrl));
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.StatusCode == System.Net.HttpStatusCode.NotModified) return null;
                if (!res.IsSuccessStatusCode) return null;
                LastCommandsEtag = res.Headers.ETag?.Tag;
                var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<CatalogResponseDto>(json);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Warn("GetCommandsAsync failed: " + ex.Message);
                return null;
            }
        }

        private async Task<SaveCommandResponseDto> SendCommandAsync(HttpMethod method, string url,
            SaveCommandRequestDto body, string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)res.StatusCode == 422)
            {
                var err = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                throw new InvalidOperationException(err != null && err.TryGetValue("detail", out var d) ? d?.ToString() : "invalid command");
            }
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            res.EnsureSuccessStatusCode();
            return JsonConvert.DeserializeObject<SaveCommandResponseDto>(json);
        }

        public Task<SaveCommandResponseDto> SaveCommandAsync(SaveCommandRequestDto body, string accessToken, CancellationToken ct)
            => SendCommandAsync(HttpMethod.Post, CommandsUrl(_baseUrl), body, accessToken, ct);

        public Task<SaveCommandResponseDto> UpdateCommandAsync(string commandId, SaveCommandRequestDto body, string accessToken, CancellationToken ct)
            => SendCommandAsync(new HttpMethod("PATCH"), $"{CommandsUrl(_baseUrl)}/{Uri.EscapeDataString(commandId)}", body, accessToken, ct);

        public async Task<bool> DeleteCommandAsync(string commandId, string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{CommandsUrl(_baseUrl)}/{Uri.EscapeDataString(commandId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
            res.EnsureSuccessStatusCode();
            return true;
        }
```

(Use whatever logger `AiService` already uses in place of `Log.Warn`; add `using RevitWebAppSync.Models;` and `using System.Text;` if missing.)

- [ ] **Step 4: Run test + compile gate**

Run: `~/.dotnet/dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AiServiceUrl" 2>&1 | tail -3` → all pass.
Run: build net48 → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Services/AiService.cs Tests/AiServiceUrlTests.cs
git commit -m "feat(commands): AiService catalog GET with ETag + save/update/delete"
```

---

### Task 3: `SavedCommandDraft` — pure model for the sheet

**Files:**
- Create: `UI/Copilot/Model/SavedCommandDraft.cs`
- Test: `Tests/SavedCommandsDraftTests.cs` (new)

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SavedCommandDraft {
      public string Name; public string Template; public List<SlashInput> Inputs; public List<string> ToolsCalled; public string SourceRunId; public string EditingId; // null = new
      public static SavedCommandDraft FromReply(string userPrompt, IEnumerable<string> toolsCalled, string runId);
      public static SavedCommandDraft FromTool(SlashTool t);   // Edit
      public bool MarkInput(int selStart, int selLength, string name, out string error);  // replaces the span with {name}, adds input
      public void UnmarkInput(string name);                       // restores? No — replaces {name} with the label text
      public static string DefaultName(string userPrompt);        // first 6 words, trimmed, Title Case-ish
      public static string SuggestInputName(string selectedText); // snake_case, ascii, ≤ 24 chars
      public SaveCommandRequestDto ToRequest();
      public const int MaxInputs = 8;
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/SavedCommandsDraftTests.cs
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class SavedCommandsDraftTests
    {
        [Fact]
        public void FromReply_seeds_name_template_tools()
        {
            var d = SavedCommandDraft.FromReply("Bina dinding dari CAD di Level 2, guna 150mm brick",
                new[] { "list_levels", "create_walls_batch", "list_levels" }, "run-1");
            Assert.Equal("Bina dinding dari CAD di Level", d.Name);
            Assert.Equal("Bina dinding dari CAD di Level 2, guna 150mm brick", d.Template);
            Assert.Equal(new[] { "list_levels", "create_walls_batch" }, d.ToolsCalled);
            Assert.Equal("run-1", d.SourceRunId);
            Assert.Empty(d.Inputs);
        }

        [Fact]
        public void MarkInput_replaces_selection_with_hole_and_adds_input()
        {
            var d = SavedCommandDraft.FromReply("walls on Level 2 please", new string[0], null);
            var start = d.Template.IndexOf("Level 2");
            Assert.True(d.MarkInput(start, "Level 2".Length, "level", out var err), err);
            Assert.Equal("walls on {level} please", d.Template);
            Assert.Single(d.Inputs);
            Assert.Equal("Level 2", d.Inputs[0].Label);
            Assert.True(d.Inputs[0].Required);
        }

        [Fact]
        public void MarkInput_rejects_bad_name_overlap_and_cap()
        {
            var d = SavedCommandDraft.FromReply("a b c d e f g h i j", new string[0], null);
            Assert.False(d.MarkInput(0, 1, "Bad Name", out var e1)); Assert.Contains("snake_case", e1);
            Assert.True(d.MarkInput(0, 1, "a", out _));
            Assert.False(d.MarkInput(0, 3, "x", out var e2)); Assert.Contains("overlaps", e2);
            for (int i = 0; i < 7; i++)
            {
                var tok = ((char)('b' + i)).ToString();
                Assert.True(d.MarkInput(d.Template.IndexOf(" " + tok) + 1, 1, tok, out _));
            }
            Assert.False(d.MarkInput(d.Template.LastIndexOf('j'), 1, "j", out var e3)); Assert.Contains("8", e3);
        }

        [Fact]
        public void UnmarkInput_restores_label_text()
        {
            var d = SavedCommandDraft.FromReply("walls on Level 2", new string[0], null);
            d.MarkInput(9, 7, "level", out _);
            d.UnmarkInput("level");
            Assert.Equal("walls on Level 2", d.Template);
            Assert.Empty(d.Inputs);
        }

        [Fact]
        public void SuggestInputName_is_snake_ascii_short()
        {
            Assert.Equal("level_2", SavedCommandDraft.SuggestInputName("Level 2"));
            Assert.Equal("brick_150mm", SavedCommandDraft.SuggestInputName("Brick 150mm!"));
            Assert.Equal("x", SavedCommandDraft.SuggestInputName("###"));
        }

        [Fact]
        public void ToRequest_maps_everything()
        {
            var d = SavedCommandDraft.FromReply("walls on Level 2", new[] { "list_levels" }, "run-9");
            d.MarkInput(9, 7, "level", out _);
            d.Name = "Walls from CAD";
            var r = d.ToRequest();
            Assert.Equal("Walls from CAD", r.NameEn);
            Assert.Equal("walls on {level}", r.PromptTemplate);
            Assert.Equal("level", r.Args[0].Name); Assert.Equal("Level 2", r.Args[0].LabelEn);
            Assert.Equal(new[] { "list_levels" }, r.ToolsCalled);
            Assert.Equal("run-9", r.SourceRunId);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `~/.dotnet/dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SavedCommandsDraft" 2>&1 | tail -3`
Expected: build error — `SavedCommandDraft` missing.

- [ ] **Step 3: Implement**

```csharp
// UI/Copilot/Model/SavedCommandDraft.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Edit model behind SaveCommandSheet. Pure: no WPF, no HTTP.
    /// The template holds {name} holes; Inputs carries one entry per hole with
    /// the ORIGINAL selected text as its label so Unmark can restore it.</summary>
    public sealed class SavedCommandDraft
    {
        public const int MaxInputs = 8;
        private static readonly Regex NameRe = new Regex("^[a-z][a-z0-9_]{0,39}$");
        private static readonly Regex HoleRe = new Regex(@"\{([a-z][a-z0-9_]{0,39})\}");

        public string Name = "";
        public string Template = "";
        public List<SlashInput> Inputs = new();
        public List<string> ToolsCalled = new();
        public string SourceRunId;
        public string EditingId;   // null = creating

        public static SavedCommandDraft FromReply(string userPrompt, IEnumerable<string> toolsCalled, string runId)
        {
            var p = (userPrompt ?? "").Trim();
            return new SavedCommandDraft
            {
                Name = DefaultName(p), Template = p, SourceRunId = runId,
                ToolsCalled = (toolsCalled ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList(),
            };
        }

        public static SavedCommandDraft FromTool(SlashTool t) => new SavedCommandDraft
        {
            EditingId = t.Id, Name = t.Name, Template = t.PromptTemplate ?? "",
            Inputs = t.Inputs.Select(i => new SlashInput { Name = i.Name, Type = i.Type, Required = i.Required, Label = i.Label }).ToList(),
        };

        public static string DefaultName(string userPrompt)
        {
            var words = (userPrompt ?? "").Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Take(6)).TrimEnd(',', '.', ';', ':');
        }

        public static string SuggestInputName(string selectedText)
        {
            var s = (selectedText ?? "").ToLowerInvariant();
            s = Regex.Replace(s, "[^a-z0-9]+", "_").Trim('_');
            if (s.Length == 0 || !char.IsLetter(s[0])) s = "x" + (s.Length == 0 ? "" : "_" + s);
            return s.Length > 24 ? s.Substring(0, 24).TrimEnd('_') : s;
        }

        public bool MarkInput(int selStart, int selLength, string name, out string error)
        {
            error = null;
            if (!NameRe.IsMatch(name ?? "")) { error = "Input name must be snake_case (a-z, 0-9, _)."; return false; }
            if (Inputs.Count >= MaxInputs) { error = $"At most {MaxInputs} inputs per command."; return false; }
            if (Inputs.Any(i => i.Name == name)) { error = $"An input named {name} already exists."; return false; }
            if (selStart < 0 || selLength <= 0 || selStart + selLength > Template.Length) { error = "Select some text first."; return false; }
            foreach (Match m in HoleRe.Matches(Template))
                if (selStart < m.Index + m.Length && m.Index < selStart + selLength) { error = "Selection overlaps an existing input."; return false; }
            var label = Template.Substring(selStart, selLength);
            Template = Template.Substring(0, selStart) + "{" + name + "}" + Template.Substring(selStart + selLength);
            Inputs.Add(new SlashInput { Name = name, Type = "text", Required = true, Label = label });
            return true;
        }

        public void UnmarkInput(string name)
        {
            var i = Inputs.FirstOrDefault(x => x.Name == name);
            if (i == null) return;
            Template = Template.Replace("{" + name + "}", i.Label ?? name);
            Inputs.Remove(i);
        }

        public SaveCommandRequestDto ToRequest() => new SaveCommandRequestDto
        {
            NameEn = (Name ?? "").Trim(), PromptTemplate = Template,
            Args = Inputs.Select(i => new CatalogArgDto { Name = i.Name, Type = i.Type, Required = i.Required, LabelEn = i.Label ?? i.Name }).ToList(),
            ToolsCalled = ToolsCalled.ToList(), SourceRunId = SourceRunId,
        };
    }
}
```

- [ ] **Step 4: Run tests + compile gate**

Run: `~/.dotnet/dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SavedCommandsDraft" 2>&1 | tail -3` → `Passed: 6`.
Build net48 → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add UI/Copilot/Model/SavedCommandDraft.cs Tests/SavedCommandsDraftTests.cs
git commit -m "feat(commands): SavedCommandDraft — mark/unmark inputs, request mapping"
```

---

### Task 4: Run id + source prompt on the AI reply

**Files:**
- Modify: `UI/Copilot/Model/ChatRouter.cs:8-31` (`RouteResult`), `UI/Copilot/RevitChatRouter.cs` (where `RouteResult` is built from `ToolLoopOutcome`, near `:280-300`), `UI/Copilot/Model/CopilotModels.cs:244-275` (`ChatMessage`), `UI/Copilot/CopilotViewModel.cs` (where the `AiReply` `ChatMessage` is created from a `RouteResult`).

**Interfaces:**
- Produces: `RouteResult.RunId` (string), `RouteResult.ToolsUsed` (List<string>, from `ToolLoopOutcome.ToolsUsed`); `ChatMessage.RunId`, `ChatMessage.SourcePrompt` (the user text of the turn), `ChatMessage.ToolsUsed`.

- [ ] **Step 1: Add fields**

`RouteResult`:
```csharp
        public string RunId;                 // backend run id of the completed turn (Save as command lineage)
        public List<string> ToolsUsed;       // distinct tool names the addin executed for this turn
```
`ChatMessage`:
```csharp
        public string RunId;          // AiReply: backend run id (saved-command lineage)
        public string SourcePrompt;   // AiReply: the user prompt that produced it (Save sheet seed)
        public List<string> ToolsUsed; // AiReply: executed tool names (saved-command allowlist)
```

- [ ] **Step 2: Wire in `RevitChatRouter`** — every place a `RouteResult` is built from a `ToolLoopOutcome outcome` (success path and `AwaitingUserInput` path), add `RunId = outcome.RunId, ToolsUsed = outcome.ToolsUsed.Distinct().ToList(),`. Find them: `grep -n "new RouteResult" UI/Copilot/RevitChatRouter.cs`.

- [ ] **Step 3: Wire in `CopilotViewModel`** — where the `AiReply` message is added after `RouteAsync` returns (`grep -n "CpMsgKind.AiReply" UI/Copilot/CopilotViewModel.cs`), set `RunId = rr.RunId, ToolsUsed = rr.ToolsUsed, SourcePrompt = <the text passed to ChatSend for this turn>`. For a slash-command turn the source prompt is the chip's template already filled (skip Save there — see Task 5 visibility rule).

- [ ] **Step 4: Compile gate** — build net48 → 0 errors.

- [ ] **Step 5: Commit**

```bash
git add UI/Copilot/Model/ChatRouter.cs UI/Copilot/RevitChatRouter.cs UI/Copilot/Model/CopilotModels.cs UI/Copilot/CopilotViewModel.cs
git commit -m "feat(commands): carry run id, tools used and source prompt onto the AI reply"
```

---

### Task 5: Footer action "Save as command"

**Files:**
- Modify: `UI/Copilot/Screens/ChatView.xaml.cs` (AiReply row build, near `:572-600`), `UI/Copilot/Controls/CopilotMessageBubble.cs` (add `SaveCommandButton`), `UI/Copilot/CopilotViewModel.cs` (`OpenSaveCommandSheet(ChatMessage m)`).

**Interfaces:**
- Consumes: `ChatMessage.RunId/ToolsUsed/SourcePrompt` (Task 4), `SavedCommandDraft.FromReply` (Task 3).
- Produces: `CopilotViewModel.OpenSaveCommandSheet(ChatMessage)` → raises `event Action<SavedCommandDraft> SaveCommandRequested` that `ChatView` handles by showing the sheet (Task 6).

- [ ] **Step 1: Visibility rule (A1)** — in `ChatView` where the AiReply row's action strip (copy / 👍 / 👎) is built, append the button only when:

```csharp
bool canSave = m.Kind == CpMsgKind.AiReply
            && m.ToolsUsed != null && m.ToolsUsed.Count > 0
            && !string.IsNullOrWhiteSpace(m.SourcePrompt)
            && !m.Interrupted /* or the equivalent flag on the message */
            && _vm.IsSignedIn;
```

- [ ] **Step 2: Button** — in `CopilotMessageBubble.cs` add, matching the design (26px tall, `CornerRadius=8`, `Cp.BlueSoft` background, `Cp.BlueText` foreground, 11.5px weight 600, save icon 12px + "Save as command"):

```csharp
        public static FrameworkElement SaveCommandButton(Action onClick)
        {
            var bd = new Border { Height = 26, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 9, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            bd.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(CommandPalette.IconEl("ti-device-floppy", 12, "Cp.BlueText"));   // CommandPalette.IconEl — make it internal static
            var t = new TextBlock { Text = "Save as command", FontSize = 11.5, FontWeight = FontWeight.FromOpenTypeWeight(600), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
            sp.Children.Add(t);
            bd.Child = sp;
            bd.MouseLeftButtonUp += (_, __) => onClick();
            return bd;
        }
```

(`IconEl` is private static in `CommandPalette.cs` — change it to `internal static`.)

- [ ] **Step 3: ViewModel hook**

```csharp
        public event Action<SavedCommandDraft> SaveCommandRequested;
        public void OpenSaveCommandSheet(ChatMessage m)
        {
            if (m == null) return;
            SaveCommandRequested?.Invoke(SavedCommandDraft.FromReply(m.SourcePrompt, m.ToolsUsed, m.RunId));
        }
```

- [ ] **Step 4: Compile gate** → 0 errors. Commit.

```bash
git add UI/Copilot/Screens/ChatView.xaml.cs UI/Copilot/Controls/CopilotMessageBubble.cs UI/Copilot/CopilotViewModel.cs
git commit -m "feat(commands): Save-as-command action on completed tool replies"
```

---

### Task 6: `SaveCommandSheet` control

**Files:**
- Create: `UI/Copilot/Controls/SaveCommandSheet.xaml`, `SaveCommandSheet.xaml.cs`
- Modify: `UI/Copilot/Screens/ChatView.xaml(.cs)` (overlay host — same in-panel layer the `/` palette uses), `UI/Copilot/CopilotViewModel.cs` (`SaveDraftAsync`)

**Interfaces:**
- Consumes: `SavedCommandDraft`, `AiService.SaveCommandAsync/UpdateCommandAsync`, `ToolCatalog.MergeRemote` refresh (Task 8).
- Produces: `SaveCommandSheet.Show(SavedCommandDraft d, Func<SavedCommandDraft, Task<string>> onSave)` — `onSave` returns null on success or an error string to display; `Hide()`.

Layout per the design artboard "2 · Save sheet" (`Cp.Menu` surface, `CornerRadius=14`, 1px `Cp.Line`, 12px inset from pane edges, docked bottom):
- Header: 20px accent tile + "Save as command" 13px/600 + `MINE` tag (10px bold, `Cp.TabBadgeBg`).
- NAME label (10px bold, `Cp.Muted`, letter-spacing) + `TextBox` (radius 10, 13px). Helper line: `Runs as /my-<kebab>` (11px `Cp.Faint`, mono for the slug) — compute client-side with the same kebab rule as the backend: lower, non-alnum→`-`, trim `-`, max 40.
- PROMPT label + right-aligned hint "Select text → Make input" + a `RichTextBox`-free approach: a plain `TextBox` (`AcceptsReturn`, wrap) whose text is `draft.Template`. Holes render as literal `{level}` text in v1 (chips inside a WPF TextBox are not worth it). A floating `Make input` button (accent, radius 7, 11.5px/600) appears above the selection when `SelectionLength > 0` (`TextBox.GetRectFromCharacterIndex` for position). Click → `PromptDialog`-style inline row: name (prefilled `SuggestInputName(selection)`), OK → `draft.MarkInput(...)`, reload text.
- INPUTS · n list: one row per input — `{name}` (mono, `Cp.CodeFg`), label `TextBox`, type `ComboBox` Text/Number, Required toggle, × (→ `UnmarkInput`). Rows: radius 10, `Cp.PanelBg`.
- TOOLS USED · n: read-only chips (mono 10.5px, `Cp.Tool.DetBg`/`Cp.Tool.Det`), line "Re-runs use only these tools." (11px `Cp.Faint`). Hidden when editing an existing command (tools stay as saved).
- Footer: `Cancel` (12.5px/600 `Cp.Muted`) + `Save command` (accent gradient `Cp.AccentGrad`, radius 9, white). While saving: button disabled + "Saving…". Error string shown in `Cp.Red` above the footer.
- Esc = Cancel. Enter inside Name = Save.

- [ ] **Step 1: Build the XAML + code-behind** following the layout above. All colors via `DynamicResource Cp.*`. Public API:

```csharp
public partial class SaveCommandSheet : UserControl
{
    private SavedCommandDraft _draft; private Func<SavedCommandDraft, Task<string>> _onSave;
    public void Show(SavedCommandDraft d, Func<SavedCommandDraft, Task<string>> onSave) { _draft = d; _onSave = onSave; Render(); Visibility = Visibility.Visible; NameBox.Focus(); NameBox.SelectAll(); }
    public void Hide() { Visibility = Visibility.Collapsed; }
    private void Render() { /* bind NameBox, TemplateBox, inputs list, tool chips, slug helper from _draft */ }
    private void OnMakeInput() { var start = TemplateBox.SelectionStart; var len = TemplateBox.SelectionLength; var name = SavedCommandDraft.SuggestInputName(TemplateBox.SelectedText); /* inline name prompt */ if (!_draft.MarkInput(start, len, name, out var err)) { ShowError(err); return; } Render(); }
    private async void OnSave() { _draft.Name = NameBox.Text; _draft.Template = TemplateBox.Text; if (string.IsNullOrWhiteSpace(_draft.Name)) { ShowError("Give the command a name."); return; } SetBusy(true); var err = await _onSave(_draft); SetBusy(false); if (err != null) { ShowError(err); return; } Hide(); }
}
```

Note: `TemplateBox` is editable, so on Save re-sync `Template` from the box; if the user typed over a hole, the backend `build_spec` rejects orphan args with `args not in template` — surface that 422 detail verbatim.

- [ ] **Step 2: Host it** — in `ChatView.xaml` add `<local:SaveCommandSheet x:Name="SaveSheet" Visibility="Collapsed" VerticalAlignment="Bottom" Margin="12"/>` inside the same overlay grid that hosts the palette (dim the chat behind it with a `Cp.Sunken` 35% opacity `Border` shown together with the sheet). In `ChatView.xaml.cs` subscribe: `_vm.SaveCommandRequested += d => SaveSheet.Show(d, _vm.SaveDraftAsync);`.

- [ ] **Step 3: ViewModel save**

```csharp
        public async Task<string> SaveDraftAsync(SavedCommandDraft d)
        {
            var token = CurrentAccessToken();   // whatever GetPromptLibraryAsync's caller uses
            if (string.IsNullOrEmpty(token)) return "Sign in to save commands.";
            try
            {
                var body = d.ToRequest();
                var res = d.EditingId == null
                    ? await _ai.SaveCommandAsync(body, token, CancellationToken.None)
                    : await _ai.UpdateCommandAsync(d.EditingId, body, token, CancellationToken.None);
                if (res == null) return "That command no longer exists.";
                await RefreshCommandCatalogAsync(force: true);   // Task 8
                Toast($"Saved /{res.Command.Id}");                // reuse the pane's existing toast/attention helper
                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
            catch (Exception ex) { return "Could not save: " + ex.Message; }
        }
```

- [ ] **Step 4: Compile gate** → 0 errors. Commit.

```bash
git add UI/Copilot/Controls/SaveCommandSheet.xaml UI/Copilot/Controls/SaveCommandSheet.xaml.cs UI/Copilot/Screens/ChatView.xaml UI/Copilot/Screens/ChatView.xaml.cs UI/Copilot/CopilotViewModel.cs
git commit -m "feat(commands): SaveCommandSheet — name, prompt, Make input, inputs list, save"
```

---

### Task 7: Palette — Mine section, input badge, ⋯ menu

**Files:**
- Modify: `UI/Copilot/Controls/CommandPalette.cs:183-232` (grouping), `:276-330` (`ToolRow`)

**Interfaces:**
- Consumes: `ToolCatalog.All/Categories` (Task 1).
- Produces: `event Action<SlashTool> EditRequested; event Action<SlashTool> DeleteRequested; event Action<SlashTool> RenameRequested;`

- [ ] **Step 1: Grouping** — `ToolCatalog.Categories` now starts with `"Mine"`, so the existing `foreach (var cat in ToolCatalog.Categories)` loop already places Mine first, after Quick access. Change the Mine header only: in `SectionHeader(cat)`, when `cat == "Mine"` colour the label `Cp.Accent` instead of `Cp.Muted` and use icon `ti-user`.

- [ ] **Step 2: Row badge** — in `ToolRow(tool)`, when `tool.Editable`: replace the badge text with `$"{tool.Inputs.Count} INPUT" + (tool.Inputs.Count == 1 ? "" : "S")` (hide when 0), colours `Cp.BlueSoft` / `Cp.BlueText`; replace the ★ pin button with a ⋯ button (26×26, `ti-dots-vertical` 14px `Cp.Muted`) that opens a `ContextMenu` with items **Rename**, **Edit**, **Delete** raising the three events. Curated rows unchanged.

- [ ] **Step 3: Keyboard** — ⋯ menu also on `Shift+F10` / `Apps` key when a Mine row is highlighted.

- [ ] **Step 4: Compile gate** → 0 errors. Commit.

```bash
git add UI/Copilot/Controls/CommandPalette.cs
git commit -m "feat(commands): palette Mine section with input badge and Rename/Edit/Delete menu"
```

---

### Task 8: Catalog refresh + Rename/Edit/Delete handlers

**Files:**
- Modify: `UI/Copilot/CopilotViewModel.cs`

**Interfaces:**
- Produces: `Task RefreshCommandCatalogAsync(bool force = false)` — GET with cached ETag; on 200 → `ToolCatalog.MergeRemote(commands.Where(c => c.Group == "mine").Select(ToolCatalog.FromCatalogEntry))`; persists the last Mine list + ETag in `CopilotPrefs` (`SavedCommandsJson`, `SavedCommandsEtag`) so an offline/signed-out start still shows Mine (A5).

- [ ] **Step 1: Refresh**

```csharp
        public async Task RefreshCommandCatalogAsync(bool force = false)
        {
            var token = CurrentAccessToken();
            if (string.IsNullOrEmpty(token)) { RestoreCachedMine(); return; }
            var res = await _ai.GetCommandsAsync(token, force ? null : _prefs.SavedCommandsEtag, CancellationToken.None);
            if (res == null) { RestoreCachedMine(); return; }   // 304 or failure → keep what we have
            var mine = res.Commands.Where(c => c.Group == "mine").ToList();
            ToolCatalog.MergeRemote(mine.Select(ToolCatalog.FromCatalogEntry));
            _prefs.SavedCommandsJson = JsonConvert.SerializeObject(mine);
            _prefs.SavedCommandsEtag = _ai.LastCommandsEtag;
            _prefs.Save();
            PaletteInvalidated?.Invoke();   // palette rebuilds its list on next open
        }

        private void RestoreCachedMine()
        {
            if (string.IsNullOrEmpty(_prefs.SavedCommandsJson)) return;
            try
            {
                var mine = JsonConvert.DeserializeObject<List<CatalogCommandDto>>(_prefs.SavedCommandsJson);
                ToolCatalog.MergeRemote(mine.Select(ToolCatalog.FromCatalogEntry));
            }
            catch { /* stale cache — ignore */ }
        }
```

Call sites: pane load (after prefs load), after sign-in succeeds, after `SaveDraftAsync`, after delete. Add `SavedCommandsJson`/`SavedCommandsEtag` string fields to `CopilotPrefs` (`UI/Copilot/Model/CopilotPrefs.cs`) following its existing persisted-field pattern.

- [ ] **Step 2: Handlers** (subscribed in `ChatView` where the palette is constructed):

```csharp
        public void OnEditCommand(SlashTool t) => SaveCommandRequested?.Invoke(SavedCommandDraft.FromTool(t));
        public void OnRenameCommand(SlashTool t) { var d = SavedCommandDraft.FromTool(t); /* same sheet, focus name */ SaveCommandRequested?.Invoke(d); }
        public async Task OnDeleteCommandAsync(SlashTool t)
        {
            if (!ConfirmInline($"Delete /{t.Id}? This cannot be undone.")) return;   // reuse the pane's Ya/Tidak card or MessageBox
            var token = CurrentAccessToken(); if (string.IsNullOrEmpty(token)) return;
            var ok = await _ai.DeleteCommandAsync(t.Id, token, CancellationToken.None);
            await RefreshCommandCatalogAsync(force: true);
            Toast(ok ? $"Deleted /{t.Id}" : "Already gone.");
        }
```

- [ ] **Step 3: Signed-out** — `SaveCommandSheet.Show` guard: if `!IsSignedIn`, `OpenSaveCommandSheet` instead shows the existing sign-in card with body "Sign in to save commands." (A5).

- [ ] **Step 4: Compile gate** → 0 errors. Commit.

```bash
git add UI/Copilot/CopilotViewModel.cs UI/Copilot/Model/CopilotPrefs.cs UI/Copilot/Screens/ChatView.xaml.cs
git commit -m "feat(commands): catalog refresh with ETag cache, rename/edit/delete, offline Mine"
```

---

### Task 9: Prompt-bar input chips (A4)

**Files:**
- Create: `UI/Copilot/Controls/InputChip.cs`
- Modify: `UI/Copilot/Controls/PromptBar.xaml.cs:237-262` (`OnToolPicked`, `RebuildCommandStrip`, send path), `UI/Copilot/CopilotViewModel.cs:905-910` (args → `PendingCommandArgs`)

**Interfaces:**
- Produces: `InputChip.Build(SlashInput input, Action onChanged) : FrameworkElement` with `public string Value`, `public bool IsEmpty`, `public void FlagRequired(bool on)`, `public void FocusValue()`; `PromptBar.PendingCommandArgs : Dictionary<string, object>` (null when no inputs); `PromptBar.SlashToolSubmitted` now carries the args: change to `Action<SlashTool, string, Dictionary<string, object>>`.

- [ ] **Step 1: Chip** — per design artboard "4": `Border` radius 8, `Cp.PurpleSoft` bg / `Cp.PurpleLine` border, padding `8,3`; label `TextBlock` 10.5px/600 `Cp.PurpleDeep`; value `TextBox` borderless, 11.5px/600, `MinWidth=52`, bottom `BorderBrush Cp.PurpleLine 0,0,0,1`; `number` type → reject non-numeric keys. `FlagRequired(true)` swaps border to `Cp.Red` 1.5px, label to `Cp.Red`, placeholder "required".

- [ ] **Step 2: Strip** — in `RebuildCommandStrip`, after adding the `CommandChip`, add one `InputChip` per `_pendingTool.Inputs` (6px gap; wrap with `WrapPanel`). Keep the chips in a `List<InputChip> _inputChips`.

- [ ] **Step 3: Send gate** — in the submit path (where `SlashToolSubmitted` fires): 

```csharp
            var missing = _inputChips.Where(c => c.Input.Required && c.IsEmpty).ToList();
            if (missing.Count > 0)
            {
                foreach (var c in _inputChips) c.FlagRequired(c.Input.Required && c.IsEmpty);
                missing[0].FocusValue();
                ShowComposerHint($"Fill {missing[0].Input.Label} to send");   // 11px Cp.Red left of the send button; clears on next edit
                return;
            }
            var args = _inputChips.Count == 0 ? null
                : _inputChips.ToDictionary(c => c.Input.Name, c => (object)(c.Input.Type == "number" ? (object)double.Parse(c.Value) : c.Value));
            SlashToolSubmitted?.Invoke(_pendingTool, Input.Text, args);
```

Send button visual: disabled outline (`Cp.Line` 1.5px, `Cp.Faint` arrow) while any required chip is empty (`UpdateSendVisual`).

- [ ] **Step 4: ViewModel** — `ChatSendSlashCommand(SlashTool tool, string args)` becomes `ChatSendSlashCommand(SlashTool tool, string note, Dictionary<string, object> inputs)`; at `:907` set `_rr.PendingCommandArgs = inputs;` next to `PendingCommandId`. The user bubble shows the chip plus a faint line of `label: value` pairs.

- [ ] **Step 5: Compile gate** → 0 errors. Commit.

```bash
git add UI/Copilot/Controls/InputChip.cs UI/Copilot/Controls/PromptBar.xaml.cs UI/Copilot/CopilotViewModel.cs
git commit -m "feat(commands): inline input chips for saved commands; required inputs gate send"
```

---

### Task 10: Windows build + smoke (A6)

**Files:** none (verification). Use `COPILOT-TESTING.md` conventions.

- [ ] **Step 1: Build on the Windows rig** with the backend branch deployed to the same environment (backend migration `0015_user_commands` applied). Dev box trap: unset `BINA_SYNC_PLUGIN_DIR` or you load the repo `bin\` build.

- [ ] **Step 2: Smoke checklist** (record pass/fail in `COPILOT-TESTING.md` under a new "Saved commands" heading):
  1. Run "Bina dinding dari CAD di Level 2, guna 150mm brick" → reply footer shows **Save as command**; a pure Q&A reply ("berapa pintu?") shows none.
  2. Save sheet opens with name "Bina dinding dari CAD di Level", template prefilled, 4 tool chips.
  3. Select "Level 2" → Make input → template shows `{level_2}`; rename input to `level`; Save → toast `Saved /my-bina-dinding-dari-cad-di-level`.
  4. Open `/` → MINE section first, row shows `1 INPUT`.
  5. Pick it → prompt bar shows command chip + `Level` input chip; Enter with it empty → chip turns red, "Fill Level to send", nothing sent.
  6. Type "Level 3", Enter → walls created on Level 3; Langfuse trace tagged `command_tier=mine`.
  7. ⋯ → Rename → change name → palette updates. ⋯ → Delete → confirm → gone from palette; `GET /commands` no longer lists it.
  8. Sign out → MINE still listed from cache; Save on a new reply shows the sign-in card.
  9. Dark mode: sheet, chips, badge all follow `Cp.*`.

- [ ] **Step 3: Tag + release** per `docs/…release` conventions (same batch as backend). Do not tag from this Mac.

---

## Self-review

- **Spec coverage:** A1 → T5; A2 → T3+T6; A3 → T1+T7; A4 → T9; A5 → T8; A6 → T10. Backend contract mirrors the backend plan's Task 6 (`GET/POST/PATCH/DELETE`, 422 detail, `group:"mine"`).
- **Placeholders:** none. Icon keys `ti-user`, `ti-device-floppy`, `ti-dots-vertical` are added to `IconData` in Task 1 (verified absent on 2026-08-30). `IconEl` is `CommandPalette.IconEl(key, size, brushKey)` (`CommandPalette.cs:59`) — Task 5 makes it `internal static` rather than referencing a `CommandChip.IconEl` that does not exist.
- **Type consistency:** `SlashInput{Name,Type,Required,Label}` used identically in T1/T3/T9; `SavedCommandDraft.FromReply(prompt, tools, runId)` in T3/T5; `SaveCommandRequested` event in T5/T6/T8; `PendingCommandArgs` dictionary in T9 matches `RevitChatRouter.cs:171`.
