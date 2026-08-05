using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class MentionTreeTests
    {
        // Project-Browser-shaped fixture: Families → Walls → Basic Wall → types.
        private static List<MentionNode> Tree() => new List<MentionNode>
        {
            MentionNode.Group("Levels", new[]
            {
                MentionNode.Leaf("level", "Level 1"),
                MentionNode.Leaf("level", "Level 2"),
            }),
            MentionNode.Group("Families", new[]
            {
                new MentionNode("category", "Walls", new[]
                {
                    new MentionNode("family", "Basic Wall", new[]
                    {
                        MentionNode.Leaf("type", "Concrete 8\""),
                        MentionNode.Leaf("type", "Concrete 12\""),
                        MentionNode.Leaf("type", "Exterior - Brick on CMU"),
                    }, pickable: true),
                }, pickable: true),
                new MentionNode("category", "Doors", new[]
                {
                    MentionNode.Leaf("type", "Single-Flush 0915 x 2134mm"),
                }, pickable: true),
            }),
        };

        // ── Empty query: everything kept, nothing auto-expanded ─────────────

        [Fact]
        public void EmptyQuery_KeepsAllRoots_CollapsedByDefault()
        {
            var expand = new HashSet<string>();
            var result = MentionTree.Filter(Tree(), "", expand);
            Assert.Equal(2, result.Count);
            Assert.Empty(expand);
        }

        // ── Descendant match: branch pruned to matches and auto-expanded ────

        [Fact]
        public void DescendantMatch_PrunesToMatchingBranch_AndExpandsPath()
        {
            var expand = new HashSet<string>();
            var result = MentionTree.Filter(Tree(), "Concrete", expand);

            var families = Assert.Single(result);
            Assert.Equal("Families", families.Name);
            var walls = Assert.Single(families.Children);
            Assert.Equal("Walls", walls.Name);
            var basic = Assert.Single(walls.Children);
            Assert.Equal(2, basic.Children.Count);   // Brick sibling pruned
            Assert.Equal(3, expand.Count);           // Families, Walls, Basic Wall all opened
        }

        // ── Self match: node kept with FULL subtree, collapsed ──────────────

        [Fact]
        public void SelfMatch_KeepsFullSubtree_Collapsed()
        {
            var expand = new HashSet<string>();
            var result = MentionTree.Filter(Tree(), "Doors", expand);

            var families = Assert.Single(result);
            var doors = Assert.Single(families.Children);
            Assert.Equal("Doors", doors.Name);
            Assert.Single(doors.Children);           // subtree intact for drill-in
            // Families expanded (to reveal Doors); Doors itself stays collapsed.
            Assert.Single(expand);
        }

        [Fact]
        public void NoMatch_EmptyResult()
        {
            var result = MentionTree.Filter(Tree(), "zzz-nothing", new HashSet<string>());
            Assert.Empty(result);
        }

        // ── Filter does not mutate the cached source tree ───────────────────

        [Fact]
        public void Filter_DoesNotMutateSource()
        {
            var tree = Tree();
            MentionTree.Filter(tree, "Concrete", new HashSet<string>());
            var basic = tree[1].Children[0].Children[0];
            Assert.Equal(3, basic.Children.Count);
        }

        // ── Flatten: pickable only, group headers skipped ───────────────────

        [Fact]
        public void Flatten_ReturnsPickableNodesOnly()
        {
            var flat = MentionTree.Flatten(Tree()).ToList();
            Assert.DoesNotContain(flat, n => n.Kind == "group");
            Assert.Contains(flat, n => n.Kind == "level" && n.Name == "Level 1");
            Assert.Contains(flat, n => n.Kind == "category" && n.Name == "Walls");
            Assert.Contains(flat, n => n.Kind == "family" && n.Name == "Basic Wall");
            Assert.Contains(flat, n => n.Kind == "type" && n.Name == "Concrete 8\"");
        }

        // ── Paths are unique per branch (expansion state can't collide) ─────

        [Fact]
        public void PathOf_DistinguishesSameNameUnderDifferentParents()
        {
            var a = MentionTree.PathOf(MentionTree.PathOf("", MentionNode.Group("Views", null)), MentionNode.Leaf("view", "WIP"));
            var b = MentionTree.PathOf(MentionTree.PathOf("", MentionNode.Group("Sheets", null)), MentionNode.Leaf("sheet", "WIP"));
            Assert.NotEqual(a, b);
        }
    }
}
