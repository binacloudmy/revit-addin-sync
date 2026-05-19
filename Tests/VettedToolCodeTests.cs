using System.Collections.Generic;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class VettedToolCodeTests
    {
        private static Dictionary<string, object> P(params (string, object)[] kv)
        {
            var d = new Dictionary<string, object>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        [Fact]
        public void Get_returns_first_non_empty_by_precedence()
        {
            var p = P(("a", ""), ("b", "x"), ("c", "y"));
            Assert.Equal("x", VettedToolCode.Get(p, "a", "b", "c"));
            Assert.Null(VettedToolCode.Get(p, "z"));
            Assert.Null(VettedToolCode.Get(null, "a"));
        }

        [Fact]
        public void IsAutoRunSafe_only_open_view()
        {
            Assert.True(VettedToolCode.IsAutoRunSafe("open_view", ""));
            Assert.True(VettedToolCode.IsAutoRunSafe("", "open_view"));
            Assert.False(VettedToolCode.IsAutoRunSafe("rename_elements", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("set_parameter", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("export_schedule", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("select_elements", ""));
            Assert.False(VettedToolCode.IsAutoRunSafe("", "execute_code"));
        }

        [Fact]
        public void TryBuild_null_for_non_new_tools()
        {
            Assert.Null(VettedToolCode.TryBuild("open_view", P(("view_name", "L1"))));
            Assert.Null(VettedToolCode.TryBuild("select_elements", P(("target_category", "Walls"))));
            Assert.Null(VettedToolCode.TryBuild("code", null));
            Assert.Null(VettedToolCode.TryBuild("", null));
            Assert.Null(VettedToolCode.TryBuild("bogus", null));
        }
    }
}
