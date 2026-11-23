#nullable enable
// r25 audit (SHOULD): the release host allowlist covered only the turn client. AIService sent the Bearer token to whatever host the
// config named: credits, the prompt library, the command catalog and its save/update/delete. In a release (and Staging) build each
// call now asks CopilotHostPolicy.IsAllowed first, the rule CopilotTurnClient uses, and a refused host gets the pane's existing
// offline answer (null, or the library's Unreachable load) without anything being sent. AIService is not compiled into Tests.csproj
// (BinaConfig file reads, a static HttpClient), so its gate is pinned by source; the shared rule is CopilotTurnClientTests'.

using System.IO;
using System.Text.RegularExpressions;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R25ReleaseHostGateTests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }

        [Theory]
        [InlineData("https://bina-ai-stg-onebistro.binacloud.ai", true)]
        [InlineData("https://evil.example.com", false)]
        [InlineData("http://bina-ai.binacloud.ai", false)]
        [InlineData("https://binacloud.ai.evil.example", false)]
        public void TheReleaseRuleIsTheTurnClients(string url, bool allowed) =>
            Assert.Equal(allowed, CopilotHostPolicy.IsAllowed(url, releaseBuild: true));

        [Fact]
        public void EveryTokenBearingCallInAIService_AsksTheHostPolicyFirst()
        {
            var src = Src("Services/AIService.cs");
            Assert.Contains("public static bool MaySend(string url) => CopilotHostPolicy.IsAllowed(url, ReleaseBuild);", src);
            Assert.Matches(new Regex(@"#if DEBUG\s+private const bool ReleaseBuild = false;\s+#else\s+private const bool ReleaseBuild = true;\s+#endif"), src);
            // One gate per method that sets the Authorization header: credits, library, catalog, save/update (one sender), delete.
            var sendsToken = Regex.Matches(src, @"Headers\.Authorization = new AuthenticationHeaderValue\(""Bearer""").Count;
            Assert.Equal(5, sendsToken);
            Assert.Equal(sendsToken, Regex.Matches(src, @"if \(!MaySend\(").Count);
            foreach (var method in new[] { "GetCreditsAsync", "FetchPromptLibraryAsync", "GetCommandsAsync", "SendCommandAsync", "DeleteCommandAsync" })
            {
                var body = src.Substring(src.IndexOf(" " + method + "(", System.StringComparison.Ordinal));
                var gate = body.IndexOf("if (!MaySend(", System.StringComparison.Ordinal);
                var send = body.IndexOf("_httpClient.SendAsync(", System.StringComparison.Ordinal);
                Assert.True(gate >= 0 && gate < send, method + " must ask the host policy before it sends");
            }
        }
    }
}
