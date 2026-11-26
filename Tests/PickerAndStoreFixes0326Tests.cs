using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    /// <summary>26 Nov code review: same-name views stay separate rows with their own id and folder; chips with different ids are not merged
    /// on the wire; a saved-commands file that cannot be read is never overwritten; a failed delete reports false.</summary>
    public class PickerAndStoreFixes0326Tests
    {
        private static MentionNode View(string name, long id) => new MentionNode("view", name) { ElementId = id };

        [Fact]
        public void Two_views_with_one_name_stay_two_search_rows_with_their_folders_shown()
        {
            var tree = new[] { MentionNode.Group("Views (00a_All)", new[]
            {
                MentionNode.Group("07d_Dokumen", new[]
                {
                    MentionNode.Group("Floor Plans", new[] { View("Aras 02", 347825) }),
                    MentionNode.Group("Ceiling Plans", new[] { View("Aras 02", 1795175) }),
                }),
            }) };
            var items = MentionSearch.Index(tree).Where(i => i.Kind == "view").ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal(new long?[] { 347825, 1795175 }, items.Select(i => i.Node.ElementId));
            Assert.Equal("07d_Dokumen › Floor Plans", items[0].Detail);
            Assert.Equal("07d_Dokumen › Ceiling Plans", items[1].Detail);
        }

        [Fact]
        public void The_same_view_listed_twice_still_merges()
        {
            var tree = new[] { View("Aras 02", 5), View("Aras 02", 5) };
            Assert.Single(MentionSearch.Index(tree).Where(i => i.Kind == "view"));
        }

        [Fact]
        public void Chips_with_the_same_name_and_different_ids_are_both_sent()
        {
            var dto = MentionWire.For(new[] { new Mention("view", "Aras 02", 1), new Mention("view", "Aras 02", 2), new Mention("view", "Aras 02", 2) });
            Assert.Equal(new long?[] { 1, 2 }, dto!.Select(d => d.Id));
        }

        private string _dir = Path.Combine(Path.GetTempPath(), "bina-store-" + System.Guid.NewGuid().ToString("N"));

        [Fact]
        public void A_locked_saved_commands_file_is_not_overwritten_and_delete_reports_false()
        {
            Directory.CreateDirectory(_dir);
            try
            {
                var path = Path.Combine(_dir, LocalCommandStore.FileName);
                var store = new LocalCommandStore(path);
                var draft = new SavedCommandDraft { Name = "Keep me", Template = "Do the thing", ToolsCalled = new System.Collections.Generic.List<string> { "find_elements" } };
                var (saved, err) = store.Add(draft);
                Assert.Null(err);
                var before = File.ReadAllText(path);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))   // another process holds the file
                {
                    var (c, error) = store.Add(new SavedCommandDraft { Name = "Second", Template = "Another", ToolsCalled = new System.Collections.Generic.List<string> { "find_elements" } });
                    Assert.Null(c);
                    Assert.Contains("busy", error);
                    Assert.False(store.Delete(saved!.Id));
                }
                Assert.Equal(before, File.ReadAllText(path));
                Assert.True(store.Delete(saved!.Id));   // readable again: the delete works
            }
            finally { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }
        }
        [Fact]
        public void A_locked_file_reads_as_unreadable_and_is_not_marked_corrupt()
        {
            Directory.CreateDirectory(_dir);
            try
            {
                var path = Path.Combine(_dir, LocalCommandStore.FileName);
                var store = new LocalCommandStore(path);
                Assert.Null(store.Add(new SavedCommandDraft { Name = "One", Template = "Do one", ToolsCalled = new System.Collections.Generic.List<string> { "find_elements" } }).Error);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.Empty(store.ToTools());
                    Assert.True(store.Unreadable);
                    Assert.False(File.Exists(path + ".bad"));
                }
                Assert.Single(store.ToTools());
                Assert.False(store.Unreadable);
            }
            finally { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }
        }
    }
}
