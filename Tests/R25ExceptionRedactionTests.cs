#nullable enable
// r25 audit (SHOULD): raw exception messages still reached the envelopes (and so the backend DB and the model) unredacted: the write
// engine's failures, the unexpected-runner envelope, the after-commit follow-ups, the check tool and its Revit readers, and the
// overview. Each now goes through DialogRedaction.RedactPaths, as CopilotToolHost does. The Revit-free helpers are run; the
// Revit-bound sites are pinned by their source.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R25ExceptionRedactionTests
    {
        private const string Leaky = @"Could not write C:\Users\aminah\Desktop\Tower A-run.rvt, owned by aminah";

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheUnexpectedRunnerFailure_IsRedacted(bool mayHaveApplied)
        {
            var env = WriteEnvelopes.Unexpected(new IOException(Leaky), mayHaveApplied);
            var text = env.Summary + " " + env.Error?.Message;
            Assert.Contains("IOException", text);
            Assert.Contains("<path>", text);
            Assert.DoesNotContain("aminah", text);
            Assert.DoesNotContain(@"C:\Users", text);
        }

        [Fact]
        public void AnAfterCommitFollowUpFailure_IsRedacted()
        {
            var failures = AfterCommitRunner.Run(new List<IReadOnlyList<Action>?> { new List<Action> { () => throw new InvalidOperationException(Leaky) } });
            var f = Assert.Single(failures);
            Assert.StartsWith("InvalidOperationException: ", f.Message);
            Assert.Contains("<path>", f.Message);
            Assert.DoesNotContain("aminah", AfterCommitRunner.WarningText(f));
        }

        [Fact]
        public void TheRevitBoundSitesRedactTheExceptionMessage()
        {
            const string r = "DialogRedaction.RedactPaths(ex.Message)";
            int Count(string rel) => System.Text.RegularExpressions.Regex.Matches(Src(rel), System.Text.RegularExpressions.Regex.Escape(r)).Count;
            Assert.Equal(4, Count("BinaVibe/Copilot/Writes/RevitWriteEngine.cs"));
            Assert.Equal(2, Count("BinaVibe/Copilot/Writes/WriteEnvelopes.cs"));
            Assert.Equal(1, Count("BinaVibe/Copilot/Writes/AfterCommitRunner.cs"));
            Assert.Equal(1, Count("BinaVibe/Copilot/Tools/CheckTool.cs"));
            Assert.Equal(2, Count("BinaVibe/Copilot/Check/RevitCheckSources.cs"));
            Assert.Equal(5, Count("BinaVibe/Copilot/Check/RevitGeometricKinds.cs"));
            Assert.Equal(3, Count("BinaVibe/Copilot/Check/RevitCheckReader.cs"));
            Assert.Equal(1, Count("BinaVibe/Copilot/Read/OverviewService.cs"));
            // r25 verifier: the sites the first pass missed.
            Assert.Equal(1, Count("BinaVibe/Copilot/Check/CheckRunner.cs"));
            Assert.Equal(3, Count("BinaVibe/Copilot/Tools/LookTool.cs"));
            Assert.Equal(3, Count("BinaVibe/Copilot/Read/LookCapture.cs"));
            Assert.Equal(1, Count("BinaVibe/Copilot/Writes/WriteBatchRunner.cs"));
            Assert.Equal(1, Count("BinaVibe/Copilot/Undo/UndoService.cs"));
            Assert.Equal(2, Count("BinaVibe/Copilot/Writes/ViewActivation.cs"));   // its two Debug.WriteLine logs stay on this machine
            Assert.Contains("DialogRedaction.RedactPaths(io.Message) + \" Revit rolled the change back", Src("BinaVibe/Copilot/Writes/RevitWriteEngine.cs"));
            foreach (var rel in new[]
            {
                "BinaVibe/Copilot/Writes/RevitWriteEngine.cs", "BinaVibe/Copilot/Writes/WriteEnvelopes.cs", "BinaVibe/Copilot/Writes/AfterCommitRunner.cs",
                "BinaVibe/Copilot/Tools/CheckTool.cs", "BinaVibe/Copilot/Check/RevitCheckSources.cs", "BinaVibe/Copilot/Check/RevitGeometricKinds.cs",
                "BinaVibe/Copilot/Check/RevitCheckReader.cs", "BinaVibe/Copilot/Read/OverviewService.cs",
                "BinaVibe/Copilot/Check/CheckRunner.cs", "BinaVibe/Copilot/Tools/LookTool.cs", "BinaVibe/Copilot/Read/LookCapture.cs",
                "BinaVibe/Copilot/Writes/WriteBatchRunner.cs", "BinaVibe/Copilot/Undo/UndoService.cs",
            })
            {
                var raw = System.Text.RegularExpressions.Regex.Matches(Src(rel), @"[^.(]\b(ex|io)\.Message\b").Count;
                Assert.True(raw == 0, rel + " still puts a raw ex.Message into its text");
            }
        }
    }
}
