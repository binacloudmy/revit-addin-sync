using System;
using System.Net.Http;
using System.Threading.Tasks;
using BinaVibe.Auth;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-57: a failed Cloud Docs token refresh must drop the stored session
    /// only when the server rejected the refresh token itself (HTTP 400/401).
    /// Network errors, timeouts and 5xx must keep the session, so a brief blip
    /// does not sign the user out mid-sync.
    /// </summary>
    public class BinaRefreshPolicyTests
    {
        [Fact]
        public void Unauthorized_401_clears_the_session() =>
            Assert.True(BinaRefreshPolicy.ClearsSession(new BinaRefreshException(401, "rejected")));

        [Fact]
        public void Bad_request_400_clears_the_session() =>
            Assert.True(BinaRefreshPolicy.ClearsSession(new BinaRefreshException(400, "rejected")));

        [Fact]
        public void Server_error_503_keeps_the_session() =>
            Assert.False(BinaRefreshPolicy.ClearsSession(new BinaRefreshException(503, "server error")));

        [Fact]
        public void Network_error_keeps_the_session() =>
            Assert.False(BinaRefreshPolicy.ClearsSession(new HttpRequestException("no such host")));

        [Fact]
        public void Timeout_keeps_the_session() =>
            // HttpClient reports its own timeout as TaskCanceledException.
            Assert.False(BinaRefreshPolicy.ClearsSession(new TaskCanceledException("timed out")));

        [Fact]
        public void Parse_failure_keeps_the_session() =>
            Assert.False(BinaRefreshPolicy.ClearsSession(new Newtonsoft.Json.JsonReaderException("bad body")));
    }
}
