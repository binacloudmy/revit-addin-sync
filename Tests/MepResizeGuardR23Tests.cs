#nullable enable
// r23 (0.0.169), live laptop bfa7d410 on merged54: the model's own run_revit_code resized a duct (RBS_CURVE_WIDTH_PARAM /
// RBS_CURVE_HEIGHT_PARAM) and committed with no card. The add-in's preview now refuses such a call structurally (MepResizeGuard):
// the rule, the refusal's shape, and the engine wiring.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MepResizeGuardR23Tests
    {
        private static ToolCallDto Code(string args) => new ToolCallDto { Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse(args).RootElement.Clone() };

        private static MepResizeGuard.SizeValue V(long id, string param, double? value, string? display, string category = "Ducts") =>
            new MepResizeGuard.SizeValue { Id = id, Category = category, Param = param, Value = value, Display = display };

        [Fact]
        public void OnlyTheModelsOwnCode_IsGuarded_NeverAStoredPlaybookOrSetParameters()
        {
            Assert.True(MepResizeGuard.Applies(Code("{\"code\":\"duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).Set(1.0);\",\"expected\":{\"modifies\":1}}")));
            Assert.True(MepResizeGuard.Applies(Code("{\"code\":\"x\",\"playbook\":\"\"}")));
            Assert.False(MepResizeGuard.Applies(Code("{\"code\":\"x\",\"playbook\":\"set-parameter\"}")));
            Assert.False(MepResizeGuard.Applies(new ToolCallDto { Tool = ToolNames.SetParameters, Args = JsonDocument.Parse("{\"param\":\"Width\"}").RootElement.Clone() }));
            Assert.False(MepResizeGuard.Applies(null));
        }

        [Fact]
        public void TheGuardedCategoriesAndSizeParameters_AreExactlyTheOnesAsked()
        {
            Assert.Equal(new[] { "OST_DuctCurves", "OST_PipeCurves", "OST_FlexDuctCurves", "OST_FlexPipeCurves", "OST_Conduit", "OST_CableTray" }, MepResizeGuard.Categories);
            Assert.Equal(new[]
            {
                "RBS_CURVE_WIDTH_PARAM", "RBS_CURVE_HEIGHT_PARAM", "RBS_CURVE_DIAMETER_PARAM", "RBS_PIPE_DIAMETER_PARAM",
                "RBS_CONDUIT_DIAMETER_PARAM", "RBS_CABLETRAY_WIDTH_PARAM", "RBS_CABLETRAY_HEIGHT_PARAM",
            }, MepResizeGuard.Parameters);
            // The engine's typed lists are the same names (BuiltInCategory / BuiltInParameter members).
            var engine = Src("BinaVibe/Copilot/Writes/RevitWriteEngine.cs");
            foreach (var c in MepResizeGuard.Categories) Assert.Contains("BuiltInCategory." + c, engine);
            foreach (var p in MepResizeGuard.Parameters) Assert.Contains("BuiltInParameter." + p, engine);
        }

        [Fact]
        public void BFA7d410_ADuctWhoseWidthAndHeightChanged_IsRefused_WithOneRowPerValue()
        {
            const double mm = 1 / 304.8;
            var after = new[] { V(1382112, "RBS_CURVE_WIDTH_PARAM", 300 * mm, "300 mm"), V(1382112, "RBS_CURVE_HEIGHT_PARAM", 200 * mm, "200 mm") };
            var before = new[] { V(1382112, "RBS_CURVE_WIDTH_PARAM", 355.6 * mm, "356 mm"), V(1382112, "RBS_CURVE_HEIGHT_PARAM", 101.6 * mm, "102 mm") };
            var rows = MepResizeGuard.Changed(after, before);
            Assert.Equal(2, rows.Count);
            Assert.Equal(1382112L, rows[0]["id"]);
            Assert.Equal("Ducts", rows[0]["category"]);
            Assert.Equal("RBS_CURVE_WIDTH_PARAM", rows[0]["param"]);
            Assert.Equal("356 mm", rows[0]["before"]);
            Assert.Equal("300 mm", rows[0]["after"]);

            var e = MepResizeGuard.Refusal(rows);
            Assert.False(e.Ok);
            Assert.Equal(EnvelopeStatus.Rejected, e.Status);
            Assert.Equal(EnvelopePhase.Preview, e.Phase);
            Assert.Equal(ErrorCodes.PolicyRejected, e.Error!.Code);
            Assert.Equal(MepResizeGuard.OwnResize, e.Error.Message);
            var resize = e.Data!.Value.GetProperty("mep_resize");
            Assert.Equal(2, resize.GetArrayLength());
            Assert.Equal("RBS_CURVE_HEIGHT_PARAM", resize[1].GetProperty("param").GetString());
            Assert.Equal("102 mm", resize[1].GetProperty("before").GetString());
            Assert.Empty(EnvelopeInvariants.Violations(e));
        }

        [Fact]
        public void AnUnchangedSize_RoundingNoise_OrAnotherParameter_IsNotAResize()
        {
            var same = new[] { V(5, "RBS_PIPE_DIAMETER_PARAM", 0.25, "76 mm", "Pipes") };
            Assert.Empty(MepResizeGuard.Changed(same, same));
            Assert.Empty(MepResizeGuard.Changed(new[] { V(5, "RBS_PIPE_DIAMETER_PARAM", 0.25 + 1e-12, "76 mm", "Pipes") }, same));
            // A duct whose Comments the code set: only size parameters are read, so there is nothing to compare.
            Assert.Empty(MepResizeGuard.Changed(new MepResizeGuard.SizeValue[0], new MepResizeGuard.SizeValue[0]));
            // A value that appeared (or went) is a change.
            Assert.Single(MepResizeGuard.Changed(new[] { V(7, "RBS_CONDUIT_DIAMETER_PARAM", 0.1, "30 mm", "Conduits") }, new[] { V(7, "RBS_CONDUIT_DIAMETER_PARAM", null, null, "Conduits") }));
        }

        [Fact]
        public void TheMessage_IsTheBackendsOwnResizeText()
        {
            // Byte-identical with OWN_RESIZE in app/services/copilot_unitask.py (fix/copilot-r23-units4 a1fbd320), checked when written.
            Assert.StartsWith("Not run: nothing was changed. The size of a duct, pipe, conduit or cable tray is never changed by your own code:", MepResizeGuard.OwnResize);
            Assert.EndsWith("The backend asks the unit if it is open and shows the confirm card.", MepResizeGuard.OwnResize);
            Assert.Equal(461, MepResizeGuard.OwnResize.Length);
        }

        [Fact]
        public void TheEngine_ReadsAfterTheCodeRanAndBeforeOnceRolledBack_AndRefusesBeforeAnyCommit()
        {
            var engine = Src("BinaVibe/Copilot/Writes/RevitWriteEngine.cs");
            // Preview, code only, no playbook: the sizes the code left, read inside the preview group.
            Assert.Contains("if (MepResizeGuard.Applies(call)) run.MepAfter = MepSizes(doc, labelled.Modified.Where(id => !benign.Contains(id)));", engine);
            // After the rollback: the same values again (before), compared; a change is the call's failure, so the runner commits nothing.
            var rollback = engine.IndexOf("st.Mark(\"rollback\");", System.StringComparison.Ordinal);
            var check = engine.IndexOf("MepResizeGuard.Changed(kv.Value.MepAfter!, MepSizes(doc, kv.Value.MepAfter!.Select(v => v.Id).Distinct()))", System.StringComparison.Ordinal);
            Assert.True(rollback > 0 && check > rollback);
            Assert.Contains("if (rows.Count > 0) { outcome.Failures[kv.Key] = MepResizeGuard.Refusal(rows); break; }", engine);
            Assert.True(check < engine.IndexOf("private CommitOutcome CommitBatch(", System.StringComparison.Ordinal));
            // A preview failure stops the batch in the runner (no commit request is made).
            var runner = Src("BinaVibe/Copilot/Writes/WriteBatchRunner.cs");
            Assert.Contains("if (j < preview.Failures.Length && preview.Failures[j] != null) { failed = j; break; }", runner);
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }
    }
}
