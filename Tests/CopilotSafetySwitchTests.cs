#nullable enable
// The Copilot safety switch (AI-423): default OFF, BINA_COPILOT_SAFETY=1 turns it on.
// Every other test in this assembly runs with the switch ON (the module initializer below), so
// the whole existing policy suite is the on-state coverage. The tests here pin both states
// explicitly through CodePolicySet.Enforce, so they never depend on the process flag.

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitWebAppSync.Tests
{
    internal static class SafetyOnForTests
    {
        // Runs before any test: the process flag is read once, so it must be set first.
        [ModuleInitializer]
        internal static void TurnSafetyOn() => Environment.SetEnvironmentVariable(CopilotSafety.EnvVar, "1");
    }

    public class CopilotSafetySwitchTests
    {
        private static readonly CodePolicySet On = new CodePolicySet(CodePolicyTable.Data, enforce: true);
        private static readonly CodePolicySet Off = new CodePolicySet(CodePolicyTable.Data, enforce: false);

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("0", false)]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData("on", true)]
        [InlineData("yes", true)]
        [InlineData("off", false)]
        public void Flag_parses_and_defaults_to_off(string? value, bool expected) =>
            Assert.Equal(expected, CopilotSafety.Parse(value));

        [Fact]
        public void Default_policy_follows_the_process_flag() =>
            Assert.Equal(CopilotSafety.Enabled, new CodePolicySet(CodePolicyTable.Data).Enforce);

        // One snippet per rejection family: each is rejected with safety on, and passes with it off.
        [Theory]
        [InlineData("System.IO.File.ReadAllText(\"C:\\\\x.txt\");")]                       // path / namespace (system_default)
        [InlineData("var t = System.Type.GetType(\"System.String\");")]                     // reflection
        [InlineData("var f = new System.Diagnostics.Stopwatch();")]                         // namespace outside the allow-list
        [InlineData("doc.Application.SharedParametersFilename = \"C:\\\\sp.txt\";")]        // SharedParametersFilename
        [InlineData("doc.Application.OpenSharedParameterFile();")]                          // shared parameter file
        [InlineData("goto done; done: ;")]                                                  // banned statement form
        [InlineData("var q = from x in new[] { 1 } select x;")]                             // query expression
        public void Policy_rejects_with_safety_on_and_allows_it_off(string code)
        {
            var on = CodeGate.Analyze(code, CodeTestSupport.BroadRefs(), WrapperProfile.Revit, On);
            Assert.Contains(on.Stage, new[] { CodeStage.PolicyRejected, CodeStage.Rejected });

            var off = CodeGate.Analyze(code, CodeTestSupport.BroadRefs(), WrapperProfile.Revit, Off);
            Assert.True(off.Allowed, CodeTestSupport.Describe(off));
        }

        [Fact]
        public void Write_only_clock_is_denied_on_and_allowed_off()
        {
            const string code = "var t = DateTime.Now; var w = Wall.Create(doc, Line.CreateBound(XYZ.Zero, new XYZ(1,0,0)), ElementId.InvalidElementId, false);";
            var on = CodeGate.Analyze(code, CodeTestSupport.BroadRefs(), WrapperProfile.Revit, On);
            Assert.Equal(CodeStage.PolicyRejected, on.Stage);

            var off = CodeGate.Analyze(code, CodeTestSupport.BroadRefs(), WrapperProfile.Revit, Off);
            Assert.True(off.Allowed, CodeTestSupport.Describe(off));
            Assert.Equal(CodeMode.Write, off.Mode!.Mode);   // classification stays: still a write, still previewed
            Assert.Empty(off.Mode.WriteOnly);
        }

        [Fact]
        public void Compile_errors_stay_with_safety_off()
        {
            var off = CodeGate.Analyze("var x = notDefinedAnywhere + 1;", CodeTestSupport.BroadRefs(), WrapperProfile.Revit, Off);
            Assert.Equal(CodeStage.CompileError, off.Stage);
        }

        [Fact]
        public void Structure_stays_with_safety_off()
        {
            var off = CodeGate.Analyze("} class X { void M() {", CodeTestSupport.BroadRefs(), WrapperProfile.Revit, Off);
            Assert.False(off.Allowed);
        }

        [Fact]
        public void Mode_classification_is_the_same_on_and_off()
        {
            const string code = "doc.Delete(ElementId.InvalidElementId);";
            var on = CodeGate.Analyze(code, CodeTestSupport.BroadRefs(), WrapperProfile.Revit, On);
            var off = CodeGate.Analyze(code, CodeTestSupport.BroadRefs(), WrapperProfile.Revit, Off);
            Assert.True(on.Allowed, CodeTestSupport.Describe(on));
            Assert.Equal(CodeMode.Write, on.Mode!.Mode);
            Assert.Equal(on.Mode.Mode, off.Mode!.Mode);
        }
    }
}
