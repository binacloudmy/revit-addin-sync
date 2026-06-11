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

        [Fact]
        public void BuildRenameElements_requires_params()
        {
            Assert.Null(VettedToolCode.BuildRenameElements(P(("target_category", "Walls"))));
            Assert.Null(VettedToolCode.BuildRenameElements(P(("find", "A"), ("replace", "B"))));
        }

        [Fact]
        public void BuildRenameElements_emits_expected()
        {
            var c = VettedToolCode.BuildRenameElements(
                P(("target_category", "Walls"), ("find", "EXT_"), ("replace", "E_"), ("scope", "Level 1")));
            Assert.NotNull(c);
            Assert.Contains("Walls", c);
            Assert.Contains("EXT_", c);
            Assert.Contains("E_", c);
            Assert.Contains("Level 1", c);
            Assert.Contains(".Name", c);
        }

        [Fact]
        public void BuildSetParameter_requires_params()
        {
            Assert.Null(VettedToolCode.BuildSetParameter(P(("target_category", "Doors"))));
            Assert.Null(VettedToolCode.BuildSetParameter(P(("parameter_name", "X"), ("value", "1"))));
        }

        [Fact]
        public void BuildSetParameter_emits_storage_type_branches()
        {
            var c = VettedToolCode.BuildSetParameter(
                P(("target_category", "Doors"), ("parameter_name", "Fire Rating"), ("value", "2 HR")));
            Assert.NotNull(c);
            Assert.Contains("Doors", c);
            Assert.Contains("Fire Rating", c);
            Assert.Contains("LookupParameter", c);
            Assert.Contains("StorageType.String", c);
            Assert.Contains("StorageType.Integer", c);
            Assert.Contains("StorageType.Double", c);
        }

        [Fact]
        public void BuildExportSchedule_requires_name()
        {
            Assert.Null(VettedToolCode.BuildExportSchedule(P(("format", "csv"))));
        }

        [Fact]
        public void BuildExportSchedule_csv_and_xlsx()
        {
            var csv = VettedToolCode.BuildExportSchedule(P(("schedule_name", "Door Schedule")));
            Assert.NotNull(csv);
            Assert.Contains("ViewSchedule", csv);
            Assert.Contains("Door Schedule", csv);
            Assert.Contains("WriteAllLines", csv);

            var xl = VettedToolCode.BuildExportSchedule(
                P(("schedule_name", "Door Schedule"), ("format", "xlsx")));
            Assert.NotNull(xl);
            Assert.Contains("WriteExcel", xl);
        }
    }
}
