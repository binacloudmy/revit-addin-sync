using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class MentionTreeCacheTests
    {
        // A tree with every MentionNode field set somewhere, shaped like the provider's (active view, levels, views, families).
        private static List<MentionNode> Sample(int doorCount = 24, int? inView = 3, string activeName = "Level 1")
        {
            var type = new MentionNode("type", "915 x 2134mm")
            {
                ClassName = "FamilySymbol", Family = "Single-Flush", Category = "Doors", CategoryKey = "OST_Doors",
                TypeMark = "D1", Count = doorCount, InView = inView,
            };
            return new List<MentionNode>
            {
                new MentionNode("view", activeName) { IsActive = true, ElementId = 311 },
                MentionNode.Group("Levels", new[] { new MentionNode("level", "Level 1") { ElementId = 30 } }),
                MentionNode.Group("Views (all)", new[]
                {
                    MentionNode.Group("Floor Plans", new[] { new MentionNode("view", "Level 1") { IsActive = activeName == "Level 1", ElementId = 311 } }),
                }),
                MentionNode.Group("Families", new[]
                {
                    new MentionNode("category", "Doors", new[]
                    {
                        new MentionNode("family", "Single-Flush", new[] { type }, pickable: true) { Category = "Doors", CategoryKey = "OST_Doors" },
                    }, pickable: true) { CategoryKey = "OST_Doors" },
                }),
            };
        }

        // Every public field of MentionNode, children recursively, so a field added later is covered too.
        private static void AssertSame(IList<MentionNode> expected, IList<MentionNode> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.NotSame(expected[i], actual[i]);
                foreach (var f in typeof(MentionNode).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.Name == nameof(MentionNode.Children)) continue;
                    Assert.True(Equals(f.GetValue(expected[i]), f.GetValue(actual[i])), f.Name + " differs on " + expected[i].Name);
                }
                AssertSame(expected[i].Children, actual[i].Children);
            }
        }

        [Fact]
        public void Same_stamp_builds_once_and_returns_an_identical_tree()
        {
            var cache = new MentionTreeCache();
            int builds = 0;
            (List<MentionNode>, bool) Build() { builds++; return (Sample(), true); }

            var first = cache.Get("doc|7|311|", Build);
            var second = cache.Get("doc|7|311|", Build);
            var third = cache.Get("doc|7|311|", Build);

            Assert.Equal(1, builds);
            AssertSame(Sample(), second);
            AssertSame(Sample(), third);
            AssertSame(first, second);
        }

        [Theory]
        [InlineData("other-session|7|311|")]   // the document was closed and reopened (new session nonce)
        [InlineData("doc|8|311|")]             // a model event (DocumentChanged, undo, sync, reload latest ...)
        [InlineData("doc|7|412|")]             // another active view
        [InlineData("doc|7|311|1,2")]          // a workset opened or closed
        public void Any_change_in_the_stamp_rebuilds_and_shows_the_new_model(string changed)
        {
            var cache = new MentionTreeCache();
            cache.Get("doc|7|311|", () => (Sample(doorCount: 24), true));

            int builds = 0;
            var after = cache.Get(changed, () => { builds++; return (Sample(doorCount: 25), true); });

            Assert.Equal(1, builds);
            Assert.Equal(25, Find(after, "915 x 2134mm").Count);
            // And the new build is what is kept from now on.
            Assert.Equal(25, Find(cache.Get(changed, () => throw new InvalidOperationException("must not rebuild")), "915 x 2134mm").Count);
        }

        [Fact]
        public void Going_back_to_an_older_stamp_rebuilds()
        {
            var cache = new MentionTreeCache();
            cache.Get("doc|7|311|", () => (Sample(), true));
            cache.Get("doc|7|412|", () => (Sample(activeName: "Level 2"), true));
            int builds = 0;
            cache.Get("doc|7|311|", () => { builds++; return (Sample(), true); });
            Assert.Equal(1, builds);
        }

        [Fact]
        public void A_null_stamp_never_reuses_and_never_keeps()
        {
            var cache = new MentionTreeCache();
            int builds = 0;
            (List<MentionNode>, bool) Build() { builds++; return (Sample(), true); }
            cache.Get(null, Build);
            cache.Get(null, Build);
            Assert.Equal(2, builds);
            // Nothing was kept under null, and a later real stamp builds.
            cache.Get("doc|7|311|", Build);
            Assert.Equal(3, builds);
        }

        [Fact]
        public void A_null_stamp_does_not_serve_the_last_kept_tree()
        {
            // Temporary hide/isolate: the provider passes null, and the tree kept before must not come back for it.
            var cache = new MentionTreeCache();
            cache.Get("doc|7|311|", () => (Sample(inView: 3), true));
            var during = cache.Get(null, () => (Sample(inView: 1), true));
            Assert.Equal(1, Find(during, "915 x 2134mm").InView);
        }

        [Fact]
        public void A_build_marked_not_storable_is_returned_but_not_kept()
        {
            var cache = new MentionTreeCache();
            var shown = cache.Get("doc|7|311|", () => (Sample(doorCount: 24), false));   // the count ran over its budget this time
            Assert.Equal(24, Find(shown, "915 x 2134mm").Count);

            int builds = 0;
            var next = cache.Get("doc|7|311|", () => { builds++; return (Sample(doorCount: 0), true); });
            Assert.Equal(1, builds);
            Assert.Equal(0, Find(next, "915 x 2134mm").Count);
        }

        [Fact]
        public void A_build_that_throws_keeps_nothing_and_the_next_call_builds()
        {
            var cache = new MentionTreeCache();
            Assert.Throws<InvalidOperationException>(() => cache.Get("doc|7|311|", () => throw new InvalidOperationException()));
            int builds = 0;
            cache.Get("doc|7|311|", () => { builds++; return (Sample(), true); });
            Assert.Equal(1, builds);
        }

        [Fact]
        public void Editing_a_returned_tree_does_not_change_the_next_one()
        {
            var cache = new MentionTreeCache();
            var first = cache.Get("doc|7|311|", () => (Sample(), true));
            first[0].Name = "edited";
            first[3].Children.Clear();
            var second = cache.Get("doc|7|311|", () => throw new InvalidOperationException("must not rebuild"));
            second[1].Children[0].Name = "edited too";
            var third = cache.Get("doc|7|311|", () => throw new InvalidOperationException("must not rebuild"));

            Assert.Equal("Level 1", second[0].Name);            // the first caller's edits did not reach the kept tree
            Assert.NotEmpty(second[3].Children);
            AssertSame(Sample(), third);                         // nor did the second caller's
        }

        [Fact]
        public void Clone_copies_every_field_deeply()
        {
            var source = Sample();
            var copy = MentionTreeCache.Clone(source);
            AssertSame(source, copy);
            Assert.Empty(MentionTreeCache.Clone(null));
        }

        private static MentionNode Find(IEnumerable<MentionNode> nodes, string name)
        {
            foreach (var n in nodes)
            {
                if (n.Name == name && n.Kind == "type") return n;
                var hit = Find(n.Children, name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
