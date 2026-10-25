#nullable enable
// W5 Task 4: "Save as command" keeps {tool, args, intent} of a successful
// set_parameters or run_revit_code commit, and re-runs it through
// CopilotToolHost (preview + confirm card) without the model. Revit-free.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ToolCommandStoreTests
    {
        private static CommittedWrite Write(string tool, object args, string intent = "") => new CommittedWrite
        {
            Tool = tool,
            Args = JsonSerializer.SerializeToElement(args),
            Intent = intent,
            Summary = "Set Mark on 12 doors.",
        };

        [Fact]
        public void SavesTypedAndCodeWrites_RefusesSessionHandles()
        {
            Assert.True(ToolCommandStore.CanSave(Write("set_parameters",
                new { target = new { filter = new { category = "Doors", level = "Level 2" } }, param = "Fire Rating", value = "FRR-60" }), out _));
            Assert.True(ToolCommandStore.CanSave(Write("run_revit_code", new { code = "Print(1);", intent = "Renumber sheets" }), out _));

            Assert.False(ToolCommandStore.CanSave(Write("set_parameters",
                new { target = new { handle = "h_7f3a91c2" }, param = "Mark", value = "A" }), out var why));
            Assert.Contains("handle", why);
            Assert.False(ToolCommandStore.CanSave(Write("find_elements", new { queries = new[] { new { category = "Doors" } } }), out _));
        }

        [Fact]
        public void RoundTripsAndBuildsAFreshCall()
        {
            var dir = Path.Combine(Path.GetTempPath(), "w5-cmds-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "copilot-tool-commands.json");
            try
            {
                var cmd = ToolCommandStore.Create(Write("run_revit_code", new { code = "Print(1);", intent = "Renumber sheets" }, "Renumber sheets"));
                Assert.Equal("Renumber sheets", cmd.Name);

                ToolCommandStore.Save(new[] { cmd }, path);
                var loaded = ToolCommandStore.Load(path);

                var only = Assert.Single(loaded);
                Assert.Equal(cmd.Id, only.Id);
                Assert.Equal("run_revit_code", only.Tool);

                var call1 = ToolCommandStore.ToCall(only);
                var call2 = ToolCommandStore.ToCall(only);
                Assert.Equal("run_revit_code", call1.Tool);
                Assert.Equal("Print(1);", call1.Args.GetProperty("code").GetString());
                Assert.NotEqual(call1.IdempotencyKey, call2.IdempotencyKey);   // each re-run is a new write
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void NameForATypedWrite_ComesFromItsArgs()
        {
            var cmd = ToolCommandStore.Create(Write("set_parameters",
                new { target = new { filter = new { category = "Doors" } }, param = "Fire Rating", value = "FRR-60" }));

            Assert.Equal("Set Fire Rating = FRR-60", cmd.Name);
        }

        [Fact]
        public void MissingOrBrokenFile_LoadsEmpty()
        {
            var path = Path.Combine(Path.GetTempPath(), "w5-missing-" + Guid.NewGuid().ToString("N") + ".json");
            Assert.Empty(ToolCommandStore.Load(path));
            File.WriteAllText(path, "{not json");
            try { Assert.Empty(ToolCommandStore.Load(path)); }
            finally { File.Delete(path); }
        }
    }
}
