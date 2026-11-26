using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class BrowserTreeTests
    {
        private static (IList<string>, MentionNode) Row(string name, params string[] path) => (path, new MentionNode("view", name) { ElementId = name.Length });

        [Fact]
        public void Folders_nest_by_the_browsers_path_folders_first_then_items_sorted()
        {
            var tree = BrowserTree.Nest(new[] { Row("B plan", "07d", "Floor Plans"), Row("A plan", "07d", "Floor Plans"), Row("Loose"), Row("3D", "01w", "3D Views"), Row("Direct", "07d") });
            Assert.Equal(new[] { "01w", "07d", "Loose" }, tree.Select(n => n.Name));
            var d07 = tree[1];
            Assert.Equal(new[] { "Floor Plans", "Direct" }, d07.Children.Select(n => n.Name));
            Assert.Equal(new[] { "A plan", "B plan" }, d07.Children[0].Children.Select(n => n.Name));
            Assert.False(tree[0].Pickable);
        }

        [Fact]
        public void View_type_folders_keep_the_browsers_order_and_the_empty_value_folder_is_last()
        {
            var tree = BrowserTree.Nest(new[] { Row("a", "???"), Row("b", "07d", "Sections"), Row("c", "07d", "Floor Plans"), Row("d", "07d", "3D Views"), Row("e", "07d", "Elevations"), Row("f", "01w"), Row("g", "07d", "Ceiling Plans") });
            Assert.Equal(new[] { "01w", "07d", "???" }, tree.Select(n => n.Name).Where(n => n != "f"));
            Assert.Equal(new[] { "Floor Plans", "Ceiling Plans", "3D Views", "Elevations", "Sections" }, tree.Single(n => n.Name == "07d").Children.Select(n => n.Name));
        }

        [Fact]
        public void Same_name_in_two_folders_keeps_both_with_their_own_ids()
        {
            var tree = BrowserTree.Nest(new[] { Row("Aras 01", "a"), Row("Aras 01", "b") });
            Assert.Equal(2, tree.SelectMany(g => g.Children).Count(n => n.Name == "Aras 01"));
        }

        [Fact]
        public void Nothing_in_nothing_out() => Assert.Empty(BrowserTree.Nest(null));
    }
}
