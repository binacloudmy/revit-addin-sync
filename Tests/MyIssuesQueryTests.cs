using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    // "My issues" is a server-side filter: the panel must send mine=true, and only then.
    public class MyIssuesQueryTests
    {
        private sealed class Capture : HttpMessageHandler
        {
            public string Url;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            {
                Url = r.RequestUri.ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"projectId\":1,\"count\":0,\"hasMore\":false,\"issues\":[]}")
                });
            }
        }

        private static async Task<string> UrlFor(bool mine)
        {
            var h = new Capture();
            var api = new SyncApiClient("https://be.test", "token", new HttpClient(h));
            await api.GetIssuesAsync(1, designId: null, mine: mine);
            return h.Url;
        }

        [Fact]
        public async Task Mine_adds_mine_true_to_the_query() =>
            Assert.Contains("mine=true", await UrlFor(true));

        [Fact]
        public async Task All_issues_sends_no_mine_param() =>
            Assert.DoesNotContain("mine=", await UrlFor(false));
    }
}
