using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UiHarness
{
    /// <summary>
    /// Canned bina-be answers for "Login to CDE" (BinaPasswordSignIn), so the
    /// sign-in window can be walked through every step without a backend or a
    /// real account:
    ///   * password "wrong"            -> 400 Email or Password is incorrect
    ///   * an email starting "2fa"     -> an OTP challenge instead of tokens
    ///   * code 123456 on verifyOtp    -> tokens; any other code -> Invalid OTP
    ///   * generateOtp                 -> OTP sent successfully
    ///   * everything else             -> tokens
    /// </summary>
    internal sealed class CloudSignInStubHandler : HttpMessageHandler
    {
        public const string GoodCode = "123456";

        private const string Tokens =
            "{\"accessToken\":\"harness-access\",\"refreshToken\":\"harness-refresh\"," +
            "\"accessTokenExpiry\":1999999999,\"userId\":7}";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            // Long enough to see the busy bar, short enough not to drag.
            await Task.Delay(350, ct).ConfigureAwait(false);

            var body = JObject.Parse(await request.Content.ReadAsStringAsync().ConfigureAwait(false));
            string path = request.RequestUri.AbsolutePath;

            if (path == "/api/auth/user/sign-in")
            {
                if ((string)body["password"] == "wrong")
                    return Json(HttpStatusCode.BadRequest,
                        "{\"statusCode\":400,\"message\":\"Email or Password is incorrect\",\"error\":\"Bad Request\"}");
                if (((string)body["email"] ?? "").StartsWith("2fa"))
                    return Json(HttpStatusCode.Created, "{\"otpRequired\":true,\"otpToken\":\"harness-challenge\"}");
                return Json(HttpStatusCode.Created, Tokens);
            }

            if (path == "/graphql")
            {
                string query = (string)body["query"] ?? "";
                if (query.Contains("generateOtp"))
                    return Json(HttpStatusCode.OK, "{\"data\":{\"generateOtp\":\"OTP sent successfully\"}}");
                if ((string)body["variables"]?["input"]?["otp"] == GoodCode)
                    return Json(HttpStatusCode.OK, "{\"data\":{\"verifyOtp\":" + Tokens + "}}");
                return Json(HttpStatusCode.OK,
                    "{\"errors\":[{\"message\":\"Invalid OTP\",\"extensions\":{\"code\":\"BAD_USER_INPUT\"}}],\"data\":null}");
            }

            return Json(HttpStatusCode.NotFound, "{\"statusCode\":404,\"message\":\"Cannot POST " + path + "\"}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
