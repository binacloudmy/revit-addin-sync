using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Auth;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RevitWebAppSync.Tests
{
    // "Login to CDE" signs in with email + password typed into the plugin, against
    // bina-be's own sign-in route. These pin the three answers that route gives
    // (tokens, an OTP challenge for a 2FA account, a refusal) and the GraphQL
    // calls that finish or re-send the challenge.
    public class BinaPasswordSignInTests
    {
        private const string Api = "https://api.test";

        private const string TokenJson =
            "{\"accessToken\":\"at-1\",\"refreshToken\":\"rt-1\",\"accessTokenExpiry\":1999999999,\"userId\":7}";

        private static BinaPasswordSignIn NewClient(FakeHttpHandler handler) =>
            new BinaPasswordSignIn(Api + "/", new HttpClient(handler));

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new HttpResponseMessage(status) { Content = new StringContent(body) };

        // ─── Sign-in ───────────────────────────────────────────────────

        [Fact]
        public async Task SignIn_posts_the_credentials_and_reads_the_tokens()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(req =>
            {
                Assert.Equal(HttpMethod.Post, req.Method);
                Assert.Equal("https://api.test/api/auth/user/sign-in", req.RequestUri.ToString());
                var body = JObject.Parse(req.Content.ReadAsStringAsync().Result);
                Assert.Equal("jane@example.test", (string)body["email"]);
                Assert.Equal("s3cret pass", (string)body["password"]);
                Assert.True((bool)body["rememberMe"]);
                // bina-be answers a POST with 201, not 200.
                return Json(HttpStatusCode.Created, TokenJson);
            });

            var result = await NewClient(handler).SignInAsync("  jane@example.test ", "s3cret pass");

            Assert.Equal(PasswordSignInStatus.SignedIn, result.Status);
            Assert.Equal("at-1", result.Tokens.AccessToken);
            Assert.Equal("rt-1", result.Tokens.RefreshToken);
            Assert.Equal(1999999999, result.Tokens.AccessTokenExpiry);
            Assert.Equal(7, result.Tokens.UserId);
            Assert.Null(result.Message);
        }

        [Fact]
        public async Task SignIn_for_a_2fa_account_returns_the_challenge_and_no_tokens()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(_ => Json(HttpStatusCode.Created, "{\"otpRequired\":true,\"otpToken\":\"challenge-1\"}"));

            var result = await NewClient(handler).SignInAsync("jane@example.test", "pw");

            Assert.Equal(PasswordSignInStatus.OtpRequired, result.Status);
            Assert.Equal("challenge-1", result.OtpToken);
            Assert.Null(result.Tokens);
        }

        [Fact]
        public async Task SignIn_refusal_shows_what_the_server_said()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(_ => Json(HttpStatusCode.BadRequest,
                "{\"statusCode\":400,\"message\":\"Email or Password is incorrect\",\"error\":\"Bad Request\"}"));

            var result = await NewClient(handler).SignInAsync("jane@example.test", "wrong");

            Assert.Equal(PasswordSignInStatus.Rejected, result.Status);
            Assert.Equal(400, result.StatusCode);
            Assert.Equal("Email or Password is incorrect", result.Message);
            Assert.Null(result.Tokens);
        }

        [Fact]
        public async Task SignIn_refusal_joins_a_validation_list()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(_ => Json(HttpStatusCode.BadRequest,
                "{\"statusCode\":400,\"message\":[\"email should not be empty\",\"password should not be empty\"]}"));

            var result = await NewClient(handler).SignInAsync("", "");

            Assert.Equal("email should not be empty; password should not be empty", result.Message);
        }

        [Fact]
        public async Task SignIn_rate_limit_asks_the_user_to_wait()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(_ => Json((HttpStatusCode)429, "{\"statusCode\":429,\"message\":\"ThrottlerException\"}"));

            var result = await NewClient(handler).SignInAsync("jane@example.test", "pw");

            Assert.Equal(PasswordSignInStatus.Rejected, result.Status);
            Assert.Equal("Too many attempts. Wait a few minutes, then try again.", result.Message);
        }

        [Fact]
        public async Task SignIn_server_error_names_the_host()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(_ => Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>"));

            var result = await NewClient(handler).SignInAsync("jane@example.test", "pw");

            Assert.Equal(PasswordSignInStatus.Rejected, result.Status);
            Assert.Equal(502, result.StatusCode);
            Assert.Contains("api.test returned a server error (HTTP 502)", result.Message);
        }

        [Fact]
        public async Task SignIn_success_without_a_session_is_a_refusal()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(_ => Json(HttpStatusCode.Created, "{}"));

            var result = await NewClient(handler).SignInAsync("jane@example.test", "pw");

            Assert.Equal(PasswordSignInStatus.Rejected, result.Status);
            Assert.Null(result.Tokens);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        [Fact]
        public async Task SignIn_network_failure_is_unreachable_and_names_the_host()
        {
            var handler = new FakeHttpHandler();
            handler.EnqueueAsync((req, ct) => throw new HttpRequestException("no route to host"));

            var result = await NewClient(handler).SignInAsync("jane@example.test", "pw");

            Assert.Equal(PasswordSignInStatus.Unreachable, result.Status);
            Assert.Equal("Can't reach api.test. Check your internet connection or proxy.", result.Message);
        }

        [Fact]
        public async Task SignIn_cancel_propagates()
        {
            var handler = new FakeHttpHandler();
            handler.EnqueueAsync(async (req, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
            using (var cts = new CancellationTokenSource())
            {
                var pending = NewClient(handler).SignInAsync("jane@example.test", "pw", cts.Token);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
        }

        // ─── OTP challenge ─────────────────────────────────────────────

        [Fact]
        public async Task VerifyOtp_sends_the_code_with_its_challenge_and_reads_the_tokens()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(req =>
            {
                Assert.Equal("https://api.test/graphql", req.RequestUri.ToString());
                var body = JObject.Parse(req.Content.ReadAsStringAsync().Result);
                Assert.Contains("verifyOtp(input: $input)", (string)body["query"]);
                var input = body["variables"]["input"];
                Assert.Equal("jane@example.test", (string)input["email"]);
                Assert.Equal("123456", (string)input["otp"]);
                Assert.Equal("challenge-1", (string)input["otpToken"]);
                return Json(HttpStatusCode.OK, "{\"data\":{\"verifyOtp\":" + TokenJson + "}}");
            });

            var result = await NewClient(handler).VerifyOtpAsync("jane@example.test", " 123456 ", "challenge-1");

            Assert.Equal(PasswordSignInStatus.SignedIn, result.Status);
            Assert.Equal("at-1", result.Tokens.AccessToken);
            Assert.Equal("rt-1", result.Tokens.RefreshToken);
            Assert.Equal(7, result.Tokens.UserId);
        }

        [Theory]
        [InlineData("Invalid OTP", "That code is not correct. Check the email and try again.")]
        [InlineData("OTP expired", "That code has expired. Click Resend code to get a new one.")]
        [InlineData("OTP session expired", "This sign-in took too long. Go back and sign in again.")]
        [InlineData("Invalid OTP session", "This sign-in took too long. Go back and sign in again.")]
        [InlineData("Something new", "Something new")]
        public async Task VerifyOtp_graphql_error_is_a_refusal(string serverMessage, string shown)
        {
            var handler = new FakeHttpHandler();
            // GraphQL reports a refused code as HTTP 200 with an errors array.
            handler.Enqueue(_ => Json(HttpStatusCode.OK,
                "{\"errors\":[{\"message\":\"" + serverMessage + "\",\"extensions\":{\"code\":\"BAD_USER_INPUT\"}}],\"data\":null}"));

            var result = await NewClient(handler).VerifyOtpAsync("jane@example.test", "000000", "challenge-1");

            Assert.Equal(PasswordSignInStatus.Rejected, result.Status);
            Assert.Equal(shown, result.Message);
            Assert.Null(result.Tokens);
        }

        [Fact]
        public async Task VerifyOtp_that_only_validates_is_not_a_session()
        {
            var handler = new FakeHttpHandler();
            // Token gating off: verifyOtp checks the code and issues nothing.
            handler.Enqueue(_ => Json(HttpStatusCode.OK, "{\"data\":{\"verifyOtp\":{\"verified\":true}}}"));

            var result = await NewClient(handler).VerifyOtpAsync("jane@example.test", "123456", "challenge-1");

            Assert.Equal(PasswordSignInStatus.Rejected, result.Status);
            Assert.Null(result.Tokens);
        }

        [Fact]
        public async Task ResendOtp_asks_for_a_new_code()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(req =>
            {
                Assert.Equal("https://api.test/graphql", req.RequestUri.ToString());
                var body = JObject.Parse(req.Content.ReadAsStringAsync().Result);
                Assert.Contains("generateOtp(input: $input)", (string)body["query"]);
                Assert.Equal("jane@example.test", (string)body["variables"]["input"]["email"]);
                return Json(HttpStatusCode.OK, "{\"data\":{\"generateOtp\":\"OTP sent successfully\"}}");
            });

            var result = await NewClient(handler).ResendOtpAsync("jane@example.test");

            Assert.Equal(PasswordSignInStatus.CodeSent, result.Status);
        }

        [Fact]
        public async Task ResendOtp_refusal_is_reported()
        {
            var handler = new FakeHttpHandler();
            handler.Enqueue(_ => Json(HttpStatusCode.OK, "{\"errors\":[{\"message\":\"User not found\"}],\"data\":null}"));

            var result = await NewClient(handler).ResendOtpAsync("jane@example.test");

            Assert.Equal(PasswordSignInStatus.Rejected, result.Status);
            Assert.Equal("User not found", result.Message);
        }

        // ─── Helpers ───────────────────────────────────────────────────

        /// <summary>Stands in for bina-be: answers queued responses in order.</summary>
        private sealed class FakeHttpHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses =
                new Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>();

            public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
                _responses.Enqueue((req, ct) => Task.FromResult(respond(req)));

            public void EnqueueAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
                _responses.Enqueue(respond);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (_responses.Count == 0)
                    throw new InvalidOperationException("unexpected request: " + request.RequestUri);
                return _responses.Dequeue()(request, ct);
            }
        }
    }
}
