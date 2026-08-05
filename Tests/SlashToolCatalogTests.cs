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
