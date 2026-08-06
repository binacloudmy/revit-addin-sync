using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class SlashToolCatalogTests
    {
        [Fact]
        public void Catalog_has_31_tools_and_no_duplicate_ids()
        {
            Assert.Equal(31, ToolCatalog.All.Count);
            Assert.Equal(31, ToolCatalog.All.Select(t => t.Id).Distinct().Count());
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
        public void Mep_command_tiles_map_to_backend_command_ids()
        {
            // Both backend commands shipped with no tile, so "/" never showed
            // them and the only way in was free text — which is how the ask
            // ended up at codegen instead of suggest_circuits.
            Assert.Equal("circuit-and-route", ToolCatalog.ById("circuit").BackendId);
            Assert.Equal("lighting-by-requirement", ToolCatalog.ById("light-req").BackendId);
            Assert.Equal("MEP", ToolCatalog.ById("circuit").Category);
            Assert.Equal("MEP", ToolCatalog.ById("light-req").Category);
        }

        [Fact]
        public void Circuit_tile_is_findable_by_the_tool_name_a_drafter_types()
        {
            // The palette filter matches Name + Subtitle + Keywords only, so the
            // tool name has to be IN the keywords — a drafter searching "/" for
            // "suggest circuits" is searching for the tool, not the tile.
            var haystack = new System.Func<SlashTool, string>(t =>
                (t.Name + " " + t.Subtitle + " " + t.Keywords).ToLowerInvariant());
            foreach (var q in new[] { "suggest circuits", "litar", "conduit", "wiring" })
                Assert.Contains(ToolCatalog.All, t => haystack(t).Contains(q));
            foreach (var q in new[] { "w/m2", "lighting", "lampu", "keperluan" })
                Assert.Contains(ToolCatalog.All, t => haystack(t).Contains(q));
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
            // 3 quick-command tools landed in General, 2 command tiles in MEP.
            Assert.Equal(20, ToolCatalog.All.Count(t =>
                t.Category == "General" || t.Category == "Architecture" ||
                t.Category == "Structure" || t.Category == "MEP") - 3 - 2);
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
