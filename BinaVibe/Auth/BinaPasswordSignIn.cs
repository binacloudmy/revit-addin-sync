// "Login to CDE" with an email and password typed into the plugin.
//
// The browser round trip (BinaOAuthClient.InteractiveLoginAsync) depends on
// bina-web's desktop bridge on /login. Production bina-web does not serve it:
// the page signed the user in and opened the CDE instead of handing a code back
// to the loopback listener. bina-be's own sign-in route has no such dependency.
//
//   POST {api}/api/auth/user/sign-in { email, password, rememberMe }
//     -> { accessToken, refreshToken, accessTokenExpiry, userId }
//     -> { otpRequired: true, otpToken }     2FA account, ENFORCE_2FA_TOKEN_GATING on
//   POST {api}/graphql   verifyOtp(input: { email, otp, otpToken })
//     -> the same token fields, once the emailed code checks out
//   POST {api}/graphql   generateOtp(input: { email })     "Resend code"
//
// The refresh token is the one /api/auth/user/oauth/refresh already rotates, so
// BinaCloudSession works unchanged. The password is sent once and never stored.

using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BinaVibe.Auth
{
    public enum PasswordSignInStatus { SignedIn, OtpRequired, CodeSent, Rejected, Unreachable }

    public sealed class PasswordSignInResult
    {
        public PasswordSignInStatus Status { get; private set; }

        /// <summary>The session; set only when <see cref="Status"/> is SignedIn.</summary>
        public BinaTokenSet Tokens { get; private set; }

        /// <summary>The challenge to send back with the emailed code; OtpRequired only.</summary>
        public string OtpToken { get; private set; }

        /// <summary>User-facing reason; set for Rejected and Unreachable.</summary>
        public string Message { get; private set; }

        public int? StatusCode { get; private set; }

        internal static PasswordSignInResult SignedIn(BinaTokenSet tokens) =>
            new PasswordSignInResult { Status = PasswordSignInStatus.SignedIn, Tokens = tokens };

        internal static PasswordSignInResult OtpRequired(string otpToken) =>
            new PasswordSignInResult { Status = PasswordSignInStatus.OtpRequired, OtpToken = otpToken };

        internal static PasswordSignInResult CodeSent() =>
            new PasswordSignInResult { Status = PasswordSignInStatus.CodeSent };

        internal static PasswordSignInResult Rejected(int? statusCode, string message) =>
            new PasswordSignInResult { Status = PasswordSignInStatus.Rejected, StatusCode = statusCode, Message = message };

        internal static PasswordSignInResult Unreachable(string message) =>
            new PasswordSignInResult { Status = PasswordSignInStatus.Unreachable, Message = message };
    }

    public sealed class BinaPasswordSignIn
    {
        private const string SignInPath = "/api/auth/user/sign-in";
        private const string GraphQlPath = "/graphql";

        private const string VerifyOtpMutation =
            "mutation verifyOtp($input: VerifyOtpInput!) { verifyOtp(input: $input) " +
            "{ accessToken refreshToken accessTokenExpiry userId } }";

        private const string GenerateOtpMutation =
            "mutation generateOtp($input: GenerateOtpInput!) { generateOtp(input: $input) }";

        private readonly string _apiBaseUrl;
        private readonly HttpClient _http;

        public BinaPasswordSignIn(string apiBaseUrl, HttpClient http = null)
        {
            _apiBaseUrl = (apiBaseUrl ?? "").TrimEnd('/');
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>host[:port] being signed in to, for the window and messages.</summary>
        public string Host => SignInPreflight.HostOf(_apiBaseUrl);

        // None of these throw for a refusal or a network failure — those are
        // results. Only cancellation of ct propagates.

        public Task<PasswordSignInResult> SignInAsync(string email, string password, CancellationToken ct = default)
        {
            var body = new { email = (email ?? "").Trim(), password = password ?? "", rememberMe = true };
            return PostAsync(SignInPath, body, (json, status) =>
            {
                string otpToken = (string)json?["otpToken"];
                if ((bool?)json?["otpRequired"] == true && !string.IsNullOrEmpty(otpToken))
                    return PasswordSignInResult.OtpRequired(otpToken);
                return TokensOrRefusal(json, status, "The server did not start a session. Try again.");
            }, ct);
        }

        /// <summary>Finish a 2FA sign-in with the emailed code and its challenge.</summary>
        public Task<PasswordSignInResult> VerifyOtpAsync(string email, string otp, string otpToken, CancellationToken ct = default)
        {
            var input = new { email = (email ?? "").Trim(), otp = (otp ?? "").Trim(), otpToken };
            return GraphQlAsync(VerifyOtpMutation, input, (data, status) =>
                // With token gating off verifyOtp only answers { verified: true }.
                // That cannot follow an otpRequired sign-in, and it is not a session.
                TokensOrRefusal(data?["verifyOtp"] as JObject, status,
                    "The code was accepted but the server did not start a session. Sign in again."), ct);
        }

        /// <summary>Email a fresh code for the challenge in progress.</summary>
        public Task<PasswordSignInResult> ResendOtpAsync(string email, CancellationToken ct = default)
        {
            var input = new { email = (email ?? "").Trim() };
            return GraphQlAsync(GenerateOtpMutation, input, (data, status) => PasswordSignInResult.CodeSent(), ct);
        }

        private Task<PasswordSignInResult> GraphQlAsync(
            string query, object input, Func<JObject, int, PasswordSignInResult> read, CancellationToken ct)
        {
            return PostAsync(GraphQlPath, new { query, variables = new { input } }, (json, status) =>
            {
                // GraphQL reports a refusal as HTTP 200 with an errors array.
                var error = (json?["errors"] as JArray)?.FirstOrDefault();
                if (error != null)
                    return PasswordSignInResult.Rejected(status, OtpMessage((string)error["message"]));
                return read(json?["data"] as JObject, status);
            }, ct);
        }

        private async Task<PasswordSignInResult> PostAsync(
            string path, object body, Func<JObject, int, PasswordSignInResult> read, CancellationToken ct)
        {
            int status;
            string text;
            try
            {
                using (var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"))
                using (var resp = await _http.PostAsync(_apiBaseUrl + path, content, ct).ConfigureAwait(false))
                {
                    status = (int)resp.StatusCode;
                    text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // DNS, refused connection, TLS, proxy, or the client's own timeout.
                return PasswordSignInResult.Unreachable(
                    $"Can't reach {Host}. Check your internet connection or proxy.");
            }

            JObject json = TryParse(text);
            if (status < 200 || status > 299)
                return PasswordSignInResult.Rejected(status, RefusalMessage(status, json));
            return read(json, status);
        }

        private static PasswordSignInResult TokensOrRefusal(JObject json, int status, string refusal)
        {
            string accessToken = (string)json?["accessToken"];
            if (string.IsNullOrEmpty(accessToken))
                return PasswordSignInResult.Rejected(status, refusal);

            return PasswordSignInResult.SignedIn(new BinaTokenSet
            {
                AccessToken = accessToken,
                RefreshToken = (string)json["refreshToken"] ?? "",
                AccessTokenExpiry = (long?)json["accessTokenExpiry"] ?? 0,   // JWT exp: unix seconds
                UserId = (int?)json["userId"] ?? 0
            });
        }

        private string RefusalMessage(int status, JObject json)
        {
            if (status == 429)
                return "Too many attempts. Wait a few minutes, then try again.";
            if (status >= 500)
                return $"{Host} returned a server error (HTTP {status}). " +
                       "Try again in a few minutes; if it keeps happening, contact BINA support.";

            // bina-be: { statusCode, message, error } — message is a string, or a
            // list when request validation failed.
            JToken message = json?["message"];
            string said = message is JArray list
                ? string.Join("; ", list.Select(m => (string)m))
                : (string)message;
            return string.IsNullOrWhiteSpace(said) ? $"Sign-in failed (HTTP {status})." : said;
        }

        // bina-be's OTP refusals are terse; say what to do next.
        private static string OtpMessage(string said)
        {
            switch (said)
            {
                case "Invalid OTP":
                    return "That code is not correct. Check the email and try again.";
                case "OTP expired":
                    return "That code has expired. Click Resend code to get a new one.";
                case "OTP session expired":
                case "Invalid OTP session":
                    return "This sign-in took too long. Go back and sign in again.";
                default:
                    return string.IsNullOrWhiteSpace(said) ? "The server refused the request." : said;
            }
        }

        private static JObject TryParse(string text)
        {
            try { return string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text); }
            catch (JsonException) { return null; }
        }
    }
}
