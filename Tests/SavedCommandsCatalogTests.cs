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
