using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-491: an issue row carries every element id (and a label per id) of its result, not
    /// only the first. IssueMapper needs the add-in's DTOs and is not compiled here, so the carrying
    /// is tested through IssueElementIds.Add + SpecModelIssues.ElementIdsOf (the two calls
    /// FromModelResults makes), and a source guard checks FromModelResults, FromDto and
    /// FromAuditRecords make them. The id logic is WPF-free (IssueElementIds) and runs on the Mac harness.</summary>
    public class IssueElementIdsTests
    {
        /// <summary>The ids + labels of the row FromModelResults builds (IssueVm.AddElements delegates to IssueElementIds.Add).</summary>
        private sealed class Row
        {
            public readonly List<long> ElementIds = new List<long>();
            public readonly Dictionary<long, string> ElementLabels = new Dictionary<long, string>();
            public int LocateCount = 1;
            public void AddElements(IEnumerable<long> ids, string label)
            {
                if (IssueElementIds.Add(ElementIds, ElementLabels, ids, label) > 0) LocateCount = ElementIds.Count;
            }
        }

        private static Row RowOf(SpecResult r)
        {
            var vm = new Row();
            vm.AddElements(SpecModelIssues.ElementIdsOf(r), (r.ElementName ?? "").Trim());
            return vm;
        }

        [Fact]
        public void A_spec_row_covering_46_doors_carries_all_46_ids_and_labels()
        {
            var ids = Enumerable.Range(0, 46).Select(i => 5000L + i).ToList();
            var vm = RowOf(new SpecResult { ElementId = ids[0], ElementIds = ids, ElementName = "Doors · PT1p_850x2100" });
            Assert.Equal(ids, vm.ElementIds);
            Assert.Equal(46, vm.LocateCount);
            Assert.Equal(vm.ElementIds.Count, vm.LocateCount);
            Assert.All(ids, id => Assert.Equal("Doors · PT1p_850x2100", vm.ElementLabels[id]));
        }

        [Fact]
        public void Results_merged_into_one_row_add_their_ids_once_each_with_their_own_label()
        {
            var vm = RowOf(new SpecResult { ElementIds = new List<long> { 1, 2 }, ElementName = "Doors · A" });
            vm.AddElements(SpecModelIssues.ElementIdsOf(new SpecResult { ElementIds = new List<long> { 2, 3 }, ElementName = "Doors · B" }), "Doors · B");
            Assert.Equal(new long[] { 1, 2, 3 }, vm.ElementIds);
            Assert.Equal(3, vm.LocateCount);
            Assert.Equal("Doors · A", vm.ElementLabels[2]);   // first label kept
            Assert.Equal("Doors · B", vm.ElementLabels[3]);
        }

        [Fact]
        public void A_model_level_row_has_no_ids_and_keeps_LocateCount_1()
        {
            var vm = RowOf(new SpecResult());
            Assert.Empty(vm.ElementIds);
            Assert.Equal(1, vm.LocateCount);
        }

        [Fact]
        public void The_mapper_fills_ElementIds_for_spec_rows_backend_rows_and_audit_rows()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root");
            var src = File.ReadAllText(Path.Combine(dir.FullName, "UI", "Jkr", "ViewModels", "IssueMapper.cs")).Replace("\r\n", "\n");
            Assert.Equal(4, src.Split(new[] { ".AddElements(" }, StringSplitOptions.None).Length - 1);   // FromDto, model (new + merge), audit: 4 calls
            var from = src.IndexOf("public static List<IssueVm> FromModelResults", StringComparison.Ordinal);
            var to = src.IndexOf("public static List<IssueVm> FromAuditRecords", StringComparison.Ordinal);
            Assert.True(from > 0 && to > from);
            Assert.Equal(2, src.Substring(from, to - from).Split(new[] { ".AddElements(" }, StringSplitOptions.None).Length - 1);
        }
    }
}
