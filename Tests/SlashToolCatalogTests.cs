using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class SlashToolCatalogTests
    {
        [Fact]
        public void Catalog_has_9_tools_and_no_duplicate_ids()
        {
            Assert.Equal(9, ToolCatalog.All.Count);
            Assert.Equal(9, ToolCatalog.All.Select(t => t.Id).Distinct().Count());
        }

        [Fact]
        public void Actions_category_follows_Mine_with_the_6_verbs()
        {
            Assert.Equal(new[] { "Mine", "Actions", "General" }, ToolCatalog.Categories);
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
        public void CIDB_dev_tools_are_gone_from_the_palette()
        {
            // Removed 2026-09-07: the 20 CIDB dev-tool commands no longer ship in "/".
            foreach (var id in new[] { "level-vis", "level-filter", "level-build", "batch-link", "cad-family", "skata",
                                       "lightvent", "door-sched", "win-sched", "room-views", "walls-cad", "walls-slab",
                                       "sloped-floor", "floor-align", "col-cad", "beam-cad", "split-floor",
                                       "ff-net", "ff-pick", "light-cad" })
                Assert.Null(ToolCatalog.ById(id));
            Assert.Empty(ToolCatalog.All.Where(t => t.Category == "Architecture" || t.Category == "Structure" || t.Category == "MEP"));
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
