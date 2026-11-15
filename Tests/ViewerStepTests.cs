using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-59: a sync's viewer step must use the CURRENT token (the one the sync
    /// client swapped in after a mid-sync 401), and a viewer failure must be
    /// reported rather than silently dropping the URN.
    /// </summary>
    public class ViewerStepTests
    {
        [Fact]
        public async Task Uses_the_token_the_provider_returns_right_now()
        {
            string used = null;
            var (urn, message) = await ViewerStep.PrepareAsync(
                () => Task.FromResult("fresh"),
                token => { used = token; return Task.FromResult("urn-1"); });

            Assert.Equal("fresh", used);
            Assert.Equal("urn-1", urn);
            Assert.Null(message);
        }

        [Fact]
        public async Task A_missing_token_reports_a_message_and_never_uploads()
        {
            bool uploaded = false;
            var (urn, message) = await ViewerStep.PrepareAsync(
                () => Task.FromResult<string>(null),
                _ => { uploaded = true; return Task.FromResult("urn"); });

            Assert.False(uploaded, "the upload must not run without a token");
            Assert.Null(urn);
            Assert.Equal(ViewerStep.FailedMessage, message);
        }

        [Fact]
        public async Task A_throwing_upload_reports_a_message()
        {
            var (urn, message) = await ViewerStep.PrepareAsync(
                () => Task.FromResult("t"),
                _ => throw new HttpRequestException("401"));

            Assert.Null(urn);
            Assert.Equal(ViewerStep.FailedMessage, message);
        }

        [Fact]
        public async Task An_empty_urn_reports_a_message()
        {
            var (urn, message) = await ViewerStep.PrepareAsync(
                () => Task.FromResult("t"),
                _ => Task.FromResult<string>(null));

            Assert.Null(urn);
            Assert.Equal(ViewerStep.FailedMessage, message);
        }

        // The end-to-end token hand-off: a real SyncApiClient refreshes on a 401
        // mid-sync, and the viewer step then receives the NEW token.
        [Fact]
        public async Task Viewer_step_receives_the_token_after_a_mid_sync_refresh()
        {
            int calls = 0;
            var handler = new Handler(_ =>
            {
                calls++;
                return calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            });

            using var api = new SyncApiClient(
                "https://be.test", "old", new HttpClient(handler),
                refreshToken: () => Task.FromResult("fresh"));

            // The first request 401s, the client refreshes and retries with the new token.
            await api.GetFoldersAsync(1);
            Assert.Equal("fresh", api.CurrentAccessToken);

            // The viewer step reads that current token — not the "old" captured earlier.
            string used = null;
            var (urn, message) = await ViewerStep.PrepareAsync(
                () => Task.FromResult(api.CurrentAccessToken),
                token => { used = token; return Task.FromResult("urn"); });

            Assert.Equal("fresh", used);
            Assert.Equal("urn", urn);
            Assert.Null(message);
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
            public Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
                => Task.FromResult(_reply(r));
        }
    }
}
