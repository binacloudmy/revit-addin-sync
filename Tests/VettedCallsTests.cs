#nullable enable
// W5 Task 5: the library's vetted tools and /open-view run as CopilotToolHost
// calls (one of the 7 add-in tools), not synthesized C# through the deleted
// CodeExecutionHandler. Revit-free.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class VettedCallsTests
    {
        private static Dictionary<string, object?> V(params (string k, string v)[] kv) =>
            kv.ToDictionary(p => p.k, p => (object?)p.v);

        [Fact]
        public void SetParameter_IsATypedWriteWithAFilter()
        {
            var c = VettedCalls.Build("set_parameter", V(("category", "Doors"), ("param", "Fire Rating"), ("value", "FRR-60"), ("scope", "Whole model")), null, out _)!;

            Assert.True(c.Write);
            Assert.Equal(ToolNames.SetParameters, c.Call.Tool);
            Assert.Equal("Doors", c.Call.Args.GetProperty("target").GetProperty("filter").GetProperty("category").GetString());
            Assert.Equal("Fire Rating", c.Call.Args.GetProperty("param").GetString());
            Assert.Equal("FRR-60", c.Call.Args.GetProperty("value").GetString());
            Assert.False(c.Call.Args.GetProperty("target").GetProperty("filter").TryGetProperty("in_view", out _));
            Assert.False(string.IsNullOrEmpty(c.Call.IdempotencyKey));
        }

        [Fact]
        public void SetParameter_ScopesToTheViewOrTheSelection()
        {
            var view = VettedCalls.Build("set_parameter", V(("category", "Walls"), ("param", "Mark"), ("value", "A"), ("scope", "Active view")), null, out _)!;
            Assert.True(view.Call.Args.GetProperty("target").GetProperty("filter").GetProperty("in_view").GetBoolean());

            var sel = new SnapshotSelection { Count = 2, Ids = new List<long> { 11, 12 } };
            var picked = VettedCalls.Build("set_parameter", V(("category", "Walls"), ("param", "Mark"), ("value", "A"), ("scope", "Selection")), sel, out _)!;
            Assert.Equal(new long[] { 11, 12 }, picked.Call.Args.GetProperty("target").GetProperty("ids").EnumerateArray().Select(e => e.GetInt64()));

            var tooMany = new SnapshotSelection { Count = 80, Ids = Enumerable.Range(1, 50).Select(i => (long)i).ToList() };
            Assert.Null(VettedCalls.Build("set_parameter", V(("category", "Walls"), ("param", "Mark"), ("value", "A"), ("scope", "Selection")), tooMany, out var why));
            Assert.Contains("50", why);
        }

        [Fact]
        public void OpenView_IsCodeInAWriteRequest_WithTheNameEscaped()
        {
            var c = VettedCalls.OpenView("Level 1\"); Print(\"pwned", out _)!;

            Assert.True(c.Write);                                   // ruling R13
            Assert.Equal(ToolNames.RunRevitCode, c.Call.Tool);
            var code = c.Call.Args.GetProperty("code").GetString()!;
            Assert.Contains("var name = \"Level 1\\\"); Print(\\\"pwned\";", code);
            Assert.Contains("uidoc.RequestViewChange(target)", code);
            Assert.DoesNotContain("Transaction", code);
            Assert.False(string.IsNullOrEmpty(c.Call.Args.GetProperty("intent").GetString()));
        }

        [Fact]
        public void Select_IsAHighlightingRead_ExportIsAScheduleRead()
        {
            var s = VettedCalls.Build("select_elements", V(("category", "Doors"), ("level", "Level 2")), null, out _)!;
            Assert.False(s.Write);
            Assert.Equal(ToolNames.FindElements, s.Call.Tool);
            Assert.True(s.Call.Args.GetProperty("highlight").GetBoolean());
            Assert.True(s.Call.Args.GetProperty("zoom").GetBoolean());
            Assert.Equal("Level 2", s.Call.Args.GetProperty("queries")[0].GetProperty("level").GetString());

            var any = VettedCalls.Build("select_elements", V(("category", "Doors"), ("level", "Any")), null, out _)!;
            Assert.False(any.Call.Args.GetProperty("queries")[0].TryGetProperty("level", out _));

            var e = VettedCalls.Build("export_schedule", V(("schedule", "Door Schedule"), ("format", "csv")), null, out _)!;
            Assert.False(e.Write);
            Assert.True(e.ExportSchedule);
            Assert.Equal("csv", e.Format);
            Assert.Equal(ToolNames.Inspect, e.Call.Tool);
            Assert.Equal("Door Schedule", e.Call.Args.GetProperty("schedule").GetString());
        }

        [Fact]
        public void ScheduleRows_BecomeAnExportTable_ObjectsOrArrays()
        {
            var call = new ToolCallDto { CallId = "c", Tool = ToolNames.Inspect, Args = JsonSerializer.SerializeToElement(new { schedule = "Door Schedule" }) };
            var req = new ToolRequestDto { Kind = ToolRequestKinds.Read, Calls = new List<ToolCallDto> { call } };

            var arrays = new TurnEvidence();
            arrays.Add(req, new[] { new Envelope { Ok = true, Status = "done", Phase = "read", Summary = "Door Schedule: 2 rows",
                Data = JsonSerializer.SerializeToElement(new { columns = new[] { "Mark", "Width" }, rows = new object[] { new object[] { "D-01", "900 mm" }, new object[] { "D-02" } } }) } });
            var t = Assert.Single(arrays.Tables);
            Assert.Equal(new[] { "Mark", "Width" }, t.Columns);
            Assert.Equal(new[] { "D-02", "" }, t.Rows[1]);

            var objects = new TurnEvidence();
            objects.Add(req, new[] { new Envelope { Ok = true, Status = "done", Phase = "read", Summary = "34 ceilings",
                Data = JsonSerializer.SerializeToElement(new { rows = new[] { new { @ref = new { id = 312045 }, type = "CL-02A" } } }) } });
            var o = Assert.Single(objects.Tables);
            Assert.Equal(new[] { "id", "type" }, o.Columns);
            Assert.Equal(new[] { "312045", "CL-02A" }, o.Rows[0]);
        }

        [Fact]
        public void Rename_IsCodeWithoutTransactions()
        {
            var c = VettedCalls.Build("rename_elements", V(("category", "Views"), ("find", "Level"), ("replace", "L"), ("scope", "Selection")), null, out _)!;

            Assert.True(c.Write);
            var code = c.Call.Args.GetProperty("code").GetString()!;
            Assert.Contains("uidoc.Selection.GetElementIds()", code);
            Assert.DoesNotContain("Transaction", code);
            Assert.DoesNotContain("SetResult", code);
        }

        [Fact]
        public void MissingValues_SayWhat()
        {
            Assert.Null(VettedCalls.OpenView("  ", out var why));
            Assert.NotEmpty(why);
            Assert.Null(VettedCalls.Build("nope", V(), null, out why));
            Assert.NotEmpty(why);
        }
    }
}
