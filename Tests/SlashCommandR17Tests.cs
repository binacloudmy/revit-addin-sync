#nullable enable
// r17 (coordinator, 21 Nov): slash commands end to end. The "/" list's curated tier comes from GET /revit-copilot/commands (the built-in
// list when the fetch fails); a chosen curated command sends its id with the turn (command_id) so the cloud adds its instructions; a
// command saved on this PC and a local command send none.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Models;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SlashCommandR17Tests
    {
        private static CatalogCommandDto Row(string id, string group = "general", string status = "live", string engine = "ai") =>
            new CatalogCommandDto { Id = id, Group = group, Status = status, Engine = engine, NameEn = id + " name", DescriptionEn = id + " desc", Icon = "ti-x" };

        [Fact]
        public void CuratedFrom_TheCatalogue_KeepsKnownIdsAndTheLocalOpenView()
        {
            var tools = ToolCatalog.CuratedFrom(new[] { Row("quick-create"), Row("new-thing", engine: "report"), Row("my-1", group: "mine"), Row("old", status: "disabled") });
            Assert.Contains(tools, t => t.Id == "create" && t.BackendId == "quick-create" && t.Category == "Actions");
            Assert.Contains(tools, t => t.Id == "new-thing" && t.Badge == ToolBadge.Report && t.Category == "General");
            Assert.Contains(tools, t => t.Id == "open-view" && t.Local);
            Assert.DoesNotContain(tools, t => t.Id == "my-1" || t.Id == "old");
        }

        [Fact]
        public void CuratedFrom_NothingLive_IsTheBuiltInList()
        {
            Assert.Same(ToolCatalog.Curated, ToolCatalog.CuratedFrom(null));
            Assert.Same(ToolCatalog.Curated, ToolCatalog.CuratedFrom(new[] { Row("x", status: "disabled") }));
        }

        [Fact]
        public void WireCommandId_OnlyCuratedCommandsSendTheirId()
        {
            var create = ToolCatalog.Curated.First(t => t.Id == "create");
            Assert.Equal("quick-create", ToolCatalog.WireCommandId(create));
            Assert.Null(ToolCatalog.WireCommandId(ToolCatalog.Curated.First(t => t.Id == "open-view")));
            Assert.Null(ToolCatalog.WireCommandId(new SlashTool { Id = "lights", Category = "Mine", Editable = true }));
            Assert.Null(ToolCatalog.WireCommandId(null));
        }

        [Fact]
        public void TurnRequest_SendsCommandId_OnlyWhenSet()
        {
            Assert.DoesNotContain("command_id", JsonSerializer.Serialize(new TurnRequestDto { TurnId = "t", Text = "x" }));
            Assert.Contains("\"command_id\":\"quick-create\"", JsonSerializer.Serialize(new TurnRequestDto { TurnId = "t", Text = "Create: a wall", CommandId = "quick-create" }));
        }
    }
}
