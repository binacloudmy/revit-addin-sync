// "Login to CDE" used to wait six minutes on a server that had no sign-in
// route, and told the browser "You're signed in" before it had checked a
// thing. These tests pin the pre-flight table, the callback checks, and the
// full loopback round trip with a fake browser and a fake token endpoint —
// including that Cancel ends the wait promptly and gives the port back.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Auth;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class BinaOAuthClientTests
    {
        private const string Api = "https://api.example.test";
        private const string Web = "https://web.example.test";

        // ─── Pre-flight table ──────────────────────────────────────────

        [Theory]
        [InlineData(404, SignInAvailability.NotDeployed)]
        [InlineData(405, SignInAvailability.NotDeployed)]
        [InlineData(400, SignInAvailability.Available)]   // bina-be: ["code should not be empty"]
        [InlineData(401, SignInAvailability.Available)]
        [InlineData(403, SignInAvailability.Available)]
        [InlineData(422, SignInAvailability.Available)]
        [InlineData(500, SignInAvailability.ServerError)]
        [InlineData(502, SignInAvailability.ServerError)]
        [InlineData(503, SignInAvailability.ServerError)]
        public void Status_codes_classify(int status, SignInAvailability expected)
        {
            Assert.Equal(expected, SignInPreflight.Classify(status));
        }

        [Fact]
        public void Not_deployed_message_names_the_host()
        {
            var r = SignInPreflight.ForStatus(404, "https://api.binacloud.ai");
            Assert.Equal(404, r.StatusCode);
            Assert.Contains("not available on this server yet (api.binacloud.ai)", r.Message);
        }

        [Fact]
        public void Available_has_no_message()
        {
            Assert.Null(SignInPreflight.ForStatus(400, Api).Message);
        }

        [Fact]
        public void Network_failure_is_unreachable_and_names_the_host()
        {
            var r = SignInPreflight.ForNetworkFailure("http://localhost:3000");
            Assert.Equal(SignInAvailability.Unreachable, r.Availability);
            Assert.Null(r.StatusCode);
            Assert.Equal("Can't reach localhost:3000. Check your internet connection or proxy.", r.Message);
        }

        // ─── Pre-flight over HTTP ──────────────────────────────────────

        [Theory]
        [InlineData(HttpStatusCode.NotFound, SignInAvailability.NotDeployed)]
        [InlineData(HttpStatusCode.BadRequest, SignInAvailability.Available)]
        [InlineData(HttpStatusCode.ServiceUnavailable, SignInAvailability.ServerError)]
        public async Task Preflight_posts_an_empty_body_to_the_token_route(HttpStatusCode status, SignInAvailability expected)
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(status));
            var client = NewClient(handler, BinaOAuthEndpoints.BinaBe());

            var r = await client.CheckSignInAvailableAsync();

            Assert.Equal(expected, r.Availability);
            var req = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal(Api + "/api/auth/user/oauth/token", req.Uri);
            Assert.Equal("{}", req.Body);
        }

        [Fact]
        public async Task Preflight_network_exception_is_unreachable()
        {
            var handler = new FakeHandler(_ => throw new HttpRequestException("No such host is known."));
            var r = await NewClient(handler, BinaOAuthEndpoints.BinaBe()).CheckSignInAvailableAsync();
            Assert.Equal(SignInAvailability.Unreachable, r.Availability);
        }

        [Fact]
        public async Task Preflight_timeout_is_unreachable_not_a_cancel()
        {
            // HttpClient reports its own timeout as TaskCanceledException.
            var handler = new FakeHandler(_ => throw new TaskCanceledException("timed out"));
            var r = await NewClient(handler, BinaOAuthEndpoints.BinaBe()).CheckSignInAvailableAsync();
            Assert.Equal(SignInAvailability.Unreachable, r.Availability);
        }

        [Fact]
        public async Task Preflight_honours_the_callers_cancel()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => NewClient(handler, BinaOAuthEndpoints.BinaBe()).CheckSignInAvailableAsync(cts.Token));
        }

        [Fact]
        public async Task Bina_ai_provider_is_never_probed()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            var r = await NewClient(handler, BinaOAuthEndpoints.BinaAi()).CheckSignInAvailableAsync();
            Assert.Equal(SignInAvailability.Available, r.Availability);
            Assert.Empty(handler.Requests);
        }

        // ─── Callback validation ───────────────────────────────────────

        [Fact]
        public void Callback_with_matching_state_and_code_is_valid()
        {
            var cb = SignInCallback.Parse("?code=abc123&state=s1", "s1");
            Assert.True(cb.IsValid);
            Assert.Equal("abc123", cb.Code);
        }

        [Fact]
        public void Callback_with_foreign_state_is_rejected()
        {
            var cb = SignInCallback.Parse("?code=abc123&state=other", "s1");
            Assert.Equal(SignInFailure.StateMismatch, cb.Failure);
            Assert.Null(cb.Code);
        }

        [Fact]
        public void Callback_without_code_is_rejected()
        {
            Assert.Equal(SignInFailure.MissingCode, SignInCallback.Parse("?state=s1", "s1").Failure);
        }

        [Fact]
        public void Access_denied_reads_as_cancelled_in_the_browser()
        {
            var cb = SignInCallback.Parse("?error=access_denied&error_description=User+cancelled&state=s1", "s1");
            Assert.Equal(SignInFailure.BrowserCancelled, cb.Failure);
            Assert.Equal("Sign-in was cancelled in the browser.", cb.Message);
        }

        [Fact]
        public void Other_provider_errors_carry_their_description()
        {
            var cb = SignInCallback.Parse("?error=server_error&error_description=Something+went%20wrong&state=s1", "s1");
            Assert.Equal(SignInFailure.ProviderError, cb.Failure);
            Assert.Equal("The sign-in page reported an error: Something went wrong", cb.Message);
        }

        [Fact]
        public void Code_is_taken_byte_for_byte()
        {
            // '+' is only prose-decoded in error_description; a code keeps it.
            Assert.Equal("a+b/c=", SignInCallback.Parse("?code=a+b%2Fc%3D&state=s1", "s1").Code);
        }

        // ─── Loopback round trip ───────────────────────────────────────

        [Fact]
        public async Task Round_trip_exchanges_the_code_and_only_then_says_signed_in()
        {
            var token = new FakeHandler(_ => Json(HttpStatusCode.OK,
                "{\"accessToken\":\"at\",\"refreshToken\":\"rt\",\"accessTokenExpiry\":1900000000,\"userId\":7}"));
            var browser = new FakeBrowser(url => $"?code=the-code&state={Query(url, "state")}");
            var client = NewClient(token, BinaOAuthEndpoints.BinaBe(), browser.Open);

            var tokens = await client.InteractiveLoginAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("at", tokens.AccessToken);
            Assert.Equal("rt", tokens.RefreshToken);
            Assert.Equal(7, tokens.UserId);
            var req = Assert.Single(token.Requests);
            Assert.Equal(Api + "/api/auth/user/oauth/token", req.Uri);
            Assert.Contains("\"code\":\"the-code\"", req.Body);
            Assert.Contains("\"code_verifier\":", req.Body);
            Assert.Contains($"\"redirect_uri\":\"{browser.RedirectUri}\"", req.Body);
            Assert.StartsWith("http://127.0.0.1:", browser.RedirectUri);
            Assert.StartsWith(Web + "/login?redirect_uri=", client.LoginUrl);
            Assert.Contains("You&#39;re signed in", await browser.Page);
        }

        [Fact]
        public async Task Wrong_state_fails_and_the_browser_is_told_so()
        {
            var token = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
            var browser = new FakeBrowser(_ => "?code=the-code&state=not-ours");
            var client = NewClient(token, BinaOAuthEndpoints.BinaBe(), browser.Open);

            var ex = await Assert.ThrowsAsync<BinaSignInException>(() => client.InteractiveLoginAsync(TimeSpan.FromSeconds(20)));

            Assert.Equal(SignInFailure.StateMismatch, ex.Failure);
            Assert.Empty(token.Requests);
            string page = await browser.Page;
            Assert.Contains("Sign-in failed", page);
            Assert.DoesNotContain("signed in", page);
        }

        [Fact]
        public async Task Cancel_on_the_consent_card_is_a_browser_cancel()
        {
            var token = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
            var browser = new FakeBrowser(url => $"?error=access_denied&state={Query(url, "state")}");
            var client = NewClient(token, BinaOAuthEndpoints.BinaBe(), browser.Open);

            var ex = await Assert.ThrowsAsync<BinaSignInException>(() => client.InteractiveLoginAsync(TimeSpan.FromSeconds(20)));

            Assert.Equal(SignInFailure.BrowserCancelled, ex.Failure);
            Assert.Empty(token.Requests);
            Assert.Contains("Sign-in cancelled", await browser.Page);
        }

        [Fact]
        public async Task Rejected_exchange_never_shows_signed_in()
        {
            var token = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, "{\"message\":\"invalid_grant\"}"));
            var browser = new FakeBrowser(url => $"?code=stale&state={Query(url, "state")}");
            var client = NewClient(token, BinaOAuthEndpoints.BinaBe(), browser.Open);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.InteractiveLoginAsync(TimeSpan.FromSeconds(20)));

            Assert.Contains("HTTP 400", ex.Message);
            string page = await browser.Page;
            Assert.Contains("Sign-in failed", page);
            Assert.DoesNotContain("signed in", page);
        }

        [Fact]
        public async Task Cancel_ends_the_wait_quickly_and_frees_the_port()
        {
            var token = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
            var client = NewClient(token, BinaOAuthEndpoints.BinaBe(), _ => { /* the user never finishes */ });
            using var cts = new CancellationTokenSource();

            var login = client.InteractiveLoginAsync(TimeSpan.FromMinutes(6), cts.Token);
            await Task.Delay(200);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"cancel took {sw.Elapsed}");
            Assert.Empty(token.Requests);

            // The same prefix binds again: the listener really let go.
            string redirect = Query(client.LoginUrl, "redirect_uri");
            using var again = new HttpListener();
            again.Prefixes.Add(redirect);
            again.Start();
            again.Stop();
        }

        [Fact]
        public async Task A_page_that_never_redirects_times_out()
        {
            var client = NewClient(new FakeHandler(_ => Json(HttpStatusCode.OK, "{}")), BinaOAuthEndpoints.BinaBe(), _ => { });
            await Assert.ThrowsAsync<TimeoutException>(() => client.InteractiveLoginAsync(TimeSpan.FromMilliseconds(300)));
        }

        // ─── Helpers ───────────────────────────────────────────────────

        private static BinaOAuthClient NewClient(FakeHandler handler, BinaOAuthEndpoints endpoints, Action<string> openBrowser = null) =>
            new BinaOAuthClient(Web, Api, new HttpClient(handler), endpoints, openBrowser ?? (_ => { }));

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static string Query(string url, string key)
        {
            string query = new Uri(url).Query.TrimStart('?');
            foreach (var pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == key)
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        private sealed class RecordedRequest
        {
            public HttpMethod Method;
            public string Uri;
            public string Body;
        }

        /// <summary>Stands in for bina-be's token endpoint.</summary>
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
            public readonly List<RecordedRequest> Requests = new List<RecordedRequest>();

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                string body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                lock (Requests)
                    Requests.Add(new RecordedRequest { Method = request.Method, Uri = request.RequestUri.ToString(), Body = body });
                return _respond(request);
            }
        }

        /// <summary>
        /// Plays the browser: reads redirect_uri from the login URL, then GETs
        /// it with the query the login page would have appended.
        /// </summary>
        private sealed class FakeBrowser
        {
            private readonly Func<string, string> _callbackQuery;
            private readonly TaskCompletionSource<string> _page =
                new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            public FakeBrowser(Func<string, string> callbackQuery) => _callbackQuery = callbackQuery;

            public string RedirectUri { get; private set; }
            public Task<string> Page => _page.Task;

            public void Open(string loginUrl)
            {
                RedirectUri = Query(loginUrl, "redirect_uri");
                string target = RedirectUri + _callbackQuery(loginUrl);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
                        _page.TrySetResult(await http.GetStringAsync(target));
                    }
                    catch (Exception ex)
                    {
                        _page.TrySetException(ex);
                    }
                });
            }
        }
    }
}
