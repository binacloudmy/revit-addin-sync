using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class MentionTreeDumpTests
    {
        [Fact]
        public void The_dump_keeps_kind_name_id_active_and_children()
        {
            var tree = new[] { MentionNode.Group("Views", new[] { MentionNode.Group("Floor Plans", new[] { new MentionNode("view", "Aras 01") { ElementId = 42, IsActive = true } }) }) };
            var json = MentionTreeDump.ToJson(tree);
            var view = json[0]["children"][0]["children"][0];
            Assert.Equal("Views", (string)json[0]["name"]);
            Assert.Equal("view", (string)view["kind"]);
            Assert.Equal(42, (long)view["id"]);
            Assert.True((bool)view["active"]);
        }

        [Fact]
        public void Unset_variable_writes_nothing_and_never_throws() =>
            Assert.Null(Record.Exception(() => MentionTreeDump.Write(Enumerable.Empty<MentionNode>())));
    }
}
