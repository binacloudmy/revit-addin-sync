#nullable enable
// 0.0.140 (from the 0.0.139 dark pass): (1) the Working card's dot, bars and spinner use the brand blue of the Z mark, not lime; (2) "Hide marks" is off, with
// a reason, when nothing of the result is drawn in the view; (3) a Revit-code turn that declares no change speaks neutrally ("What I will run", "Checking the
// code in Revit"), not write wording. Wording and presentation only: preview, Stop window, gate and Undo are untouched.
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class NeutralAndBrand0140Tests
    {
        private static ToolCallDto Code(string argsJson) => new ToolCallDto { CallId = "c1", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse(argsJson).RootElement.Clone() };

        [Fact]
        public void CodeThatDeclaresNoChange_SpeaksNeutrally()
        {
            var read = new List<ToolCallDto> { Code("{\"code\":\"var x = 1;\",\"intent\":\"List walls\"}") };
            var zero = new List<ToolCallDto> { Code("{\"code\":\"var x = 1;\",\"intent\":\"List walls\",\"expected\":{\"adds\":0,\"modifies\":0,\"deletes\":0}}") };
            foreach (var calls in new[] { read, zero })
            {
                Assert.True(WritePreviewText.DeclaresNoChange(calls));
                Assert.Equal("What I will run", WritePreviewText.Title(calls, false));
                Assert.Equal("Checking the code in Revit…", WritePreviewText.PreviewStepLabel(calls, false));
                Assert.DoesNotContain("change", WritePreviewText.Lines(calls, false).ToLowerInvariant());
            }
        }

        [Fact]
        public void CodeThatDeclaresAChange_AndOtherTools_KeepWriteWording()
        {
            var write = new List<ToolCallDto> { Code("{\"code\":\"var x = 1;\",\"intent\":\"Move\",\"expected\":{\"modifies\":3}}") };
            Assert.False(WritePreviewText.DeclaresNoChange(write));
            Assert.Equal("What I will write", WritePreviewText.Title(write, false));
            Assert.Equal("Previewing the change in Revit…", WritePreviewText.PreviewStepLabel(write, false));
            var set = new List<ToolCallDto> { new ToolCallDto { CallId = "c2", Tool = ToolNames.SetParameters, Args = JsonDocument.Parse("{}").RootElement.Clone() } };
            Assert.False(WritePreviewText.DeclaresNoChange(set));
            Assert.False(WritePreviewText.DeclaresNoChange(new List<ToolCallDto>()));
        }

        [Fact]
        public void TheWorkingCardIsBrandBlue_AndHideMarksSaysWhyItIsOff()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            string Src(string rel) => File.ReadAllText(Path.Combine(dir!.FullName, rel));
            Assert.Contains("[\"Cp.Run.Accent\"]    = (\"#2a69c6\", \"#5EA2FF\")", Src("UI/Copilot/Model/ThemeTokens.cs"));
            Assert.DoesNotContain("D2F65A", Src("UI/Copilot/Model/ThemeTokens.cs").Substring(Src("UI/Copilot/Model/ThemeTokens.cs").IndexOf("Cp.Run.Accent\"]")).Split('\n')[0]);
            var btn = Src("UI/Copilot/Controls/MarksButton.cs");
            Assert.Contains("b.IsEnabled = !nothing;", btn);
            Assert.Contains("Nothing is drawn in this view", btn);
            Assert.Contains("public bool AnyDrawn => _marks.Count > 0 || _controls.Count > 0;", Src("UI/Copilot/Highlights/InModelResult.cs"));
        }
    }
}
