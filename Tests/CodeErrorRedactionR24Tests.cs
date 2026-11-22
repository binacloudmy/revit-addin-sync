#nullable enable
// 0.0.174 (r24 privacy): an exception or failure text from the model's own run_revit_code reaches the envelope redacted (paths, files,
// emails, worksharing names: DialogRedaction.RedactPaths). The model's own printed strings are NOT redacted (coordinator decision).

using System;
using System.IO;
using System.Reflection;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CodeErrorRedactionR24Tests
    {
        private const string Recipe = @"Unable to open C:\Users\aminah\Desktop\MRSM3-run.rvt, owned by aminah";

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }

        [Fact]
        public void TheLiveRecipeString_IsRedacted()
        {
            Assert.Equal("Unable to open <path> owned by <user>", BinaVibe.Copilot.Writes.DialogRedaction.RedactPaths(Recipe, "nobody-here", null));
        }

        [Fact]
        public void ARuntimeExceptionFromTheModelsCode_ReachesMessageAndSummaryRedacted()
        {
            var f = ErrorMapper.FromException(new InvalidOperationException(Recipe), null, readMode: true);
            Assert.EndsWith("Unable to open <path> owned by <user>", f.Message);
            Assert.StartsWith("InvalidOperationException", f.Message);
            Assert.DoesNotContain("aminah", f.Message + f.Summary);
            Assert.DoesNotContain("MRSM3", f.Message + f.Summary);
        }

        [Fact]
        public void AnInnerException_IsUnwrapped_AndRedacted()
        {
            var wrapped = new TargetInvocationException(new InvalidOperationException(Recipe));
            var f = ErrorMapper.FromException(wrapped, null, readMode: false);
            Assert.DoesNotContain("aminah", f.Message + f.Summary);
            Assert.Contains("<path>", f.Message);
            var agg = ErrorMapper.FromException(new AggregateException(new IOException(@"Cannot read \\binacloudpc\share\a.xlsx")), null, readMode: true);
            Assert.DoesNotContain("binacloudpc", agg.Message + agg.Summary);
        }

        [Fact]
        public void APrintedException_IsRedacted_ButAPrintedString_IsNot()
        {
            var o = new OutputBuffer();
            o.Print(new InvalidOperationException(Recipe));
            o.Print(@"model said C:\Users\aminah\Desktop\MRSM3-run.rvt");
            Assert.Contains("InvalidOperationException: Unable to open <path> owned by <user>", o.Text);
            Assert.Contains(@"model said C:\Users\aminah\Desktop\MRSM3-run.rvt", o.Text);   // the model's own output stays as printed
        }

        [Fact]
        public void EverySiteThatTurnsAnExceptionIntoEnvelopeText_Redacts()
        {
            const string r = "BinaVibe.Copilot.Writes.DialogRedaction.RedactPaths(ex.Message)";
            Assert.Contains("var message = Clip(" + r + ", MaxMessageChars);", Src("BinaVibe/Copilot/Code/ErrorMapper.cs"));
            Assert.Contains("ex.GetType().Name + \": \" + " + r, Src("BinaVibe/Copilot/Code/OutputBuffer.cs"));
            Assert.Contains("Internal(prepared, ex.GetType().Name + \": \" + " + r + ")", Src("BinaVibe/Copilot/Code/CodeRunner.cs"));
            Assert.Contains("return Busy(" + r + ", EnvelopePhase.Read);", Src("BinaVibe/Copilot/Code/RevitCodeHost.cs"));
            Assert.DoesNotContain("Clip(ex.Message", Src("BinaVibe/Copilot/Code/ErrorMapper.cs"));
        }
    }
}
