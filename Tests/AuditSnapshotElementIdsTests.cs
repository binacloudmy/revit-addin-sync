using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The audit snapshot keeps every element id of an issue (an optional field);
    /// snapshots written before it existed load with the single RevitElementId.</summary>
    public class AuditSnapshotElementIdsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bina-snap-" + Guid.NewGuid().ToString("N"));
        private string Rvt => Path.Combine(_dir, "model.rvt");
        public AuditSnapshotElementIdsTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void All_element_ids_round_trip_through_the_audit_file()
        {
            var vm = new IssueVm
            {
                Id = "f1", Title = "t", Status = IssueStatus.Fixed, RevitElementId = 11,
                RevitElementIds = new List<long> { 11, 22, 33 },
                Element = new ElementRef { Id = "11", Name = "3 elements" },
            };
            JkrAuditStore.Save(Rvt, vm);
            var back = JkrAuditStore.LoadFixedFor(Rvt).Single(i => i.Id == "f1");
            Assert.Equal(new long[] { 11, 22, 33 }, back.RevitElementIds.ToArray());
            Assert.Equal(11, back.RevitElementId);
        }

        [Fact]
        public void An_old_snapshot_without_the_field_falls_back_to_the_single_id()
        {
            const string old = "{\"title\":\"t\",\"element_id_label\":\"77\",\"revit_element_id\":77}";
            var snap = JsonConvert.DeserializeObject<IssueSnapshot>(old);
            Assert.Null(snap.RevitElementIds);
            var vm = snap.ToIssueVm("old1");
            Assert.Equal(new long[] { 77 }, vm.RevitElementIds.ToArray());
            Assert.Equal(77, vm.RevitElementId);
            var none = JsonConvert.DeserializeObject<IssueSnapshot>("{\"title\":\"t\"}").ToIssueVm("old2");
            Assert.Empty(none.RevitElementIds);
        }

        [Fact]
        public void The_field_is_left_out_of_the_file_when_the_issue_has_only_one_element()
        {
            var json = JsonConvert.SerializeObject(IssueSnapshot.From(new IssueVm { Id = "x", RevitElementId = 5 }));
            Assert.DoesNotContain("revit_element_ids", json);
        }
    }
}
