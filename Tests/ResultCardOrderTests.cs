using System;
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>AI-278: ReplaceLastThinking rebuilds the thread synchronously, so the result
    /// card must be on the reply before it is published. CopilotViewModel is WPF/Revit-bound
    /// and not linked into this project, so the order is pinned on the source.</summary>
    public class ResultCardOrderTests
    {
        private static string Here([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;

        [Fact]
        public void Reply_ResultCard_is_set_before_the_reply_is_published()
        {
            var src = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "UI", "Copilot", "CopilotViewModel.cs"));
            int set = src.IndexOf("reply.ResultCard = ResultCardModel.From", StringComparison.Ordinal);
            int publish = src.IndexOf("ReplaceLastThinking(reply);", StringComparison.Ordinal);
            Assert.True(set > 0, "result card assignment not found");
            Assert.True(publish > 0, "reply publish not found");
            Assert.True(set < publish, "ResultCard must be attached before ReplaceLastThinking(reply)");
            Assert.Equal(publish, src.LastIndexOf("ReplaceLastThinking(reply);", StringComparison.Ordinal));
        }
    }
}
