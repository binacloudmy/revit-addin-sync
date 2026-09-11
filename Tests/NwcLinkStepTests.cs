using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The NWC upload+link step (ClickUp 86d49v9ak).
    ///
    /// The guarantee under test is failure isolation: by the time this runs the
    /// rvt version is already committed, so nothing here may throw and nothing
    /// here may look like a failed sync. Every path has to come back as data.
    ///
    /// Driven through SyncApiClient's injected HttpClient, so the real serializer,
    /// the real routes and the real error handling are exercised — a hand-rolled
    /// fake of the client would prove only that the fake behaves.
    /// </summary>
    public class NwcLinkStepTests : IDisposable
    {
        private readonly string _nwcPath;

        public NwcLinkStepTests()
        {
            _nwcPath = Path.Combine(Path.GetTempPath(), $"bina_nwc_test_{Guid.NewGuid():N}.nwc");
            File.WriteAllText(_nwcPath, "not really a cache");
        }

        public void Dispose()
        {
            try { if (File.Exists(_nwcPath)) File.Delete(_nwcPath); } catch { }
        }

        /// <summary>Canned answers keyed by the path fragment of the request.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public List<string> Calls { get; } = new List<string>();
            public List<string> Bodies { get; } = new List<string>();

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls.Add(request.RequestUri.AbsoluteUri);
                Bodies.Add(request.Content == null
                    ? ""
                    : await request.Content.ReadAsStringAsync().ConfigureAwait(false));

                return _respond(request);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new HttpResponseMessage(code)
            {
                Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };

        private static bool Is(HttpRequestMessage r, string fragment) =>
            r.RequestUri.AbsoluteUri.Contains(fragment);

        private static async Task<NwcLinkStep.Outcome> Run(StubHandler handler, string nwcPath)
        {
            using (var api = new SyncApiClient("https://bina.test", "token", new HttpClient(handler)))
            {
                return await NwcLinkStep.RunAsync(api, projectId: 7, designId: 100,
                    disciplineType: "Architecture", nwcPath: nwcPath);
            }
        }

        private static StubHandler HappyPath() => new StubHandler(r =>
        {
            if (Is(r, "sync/init-link"))
                return Json(HttpStatusCode.OK,
                    "{\"uploadUrl\":\"https://obs.test/put\",\"fileKey\":\"bim-disciplines/7/Architecture/1.nwc\"}");
            if (Is(r, "obs.test/put"))
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (Is(r, "sync/link"))
                return Json(HttpStatusCode.OK, "{\"linkId\":555,\"designId\":100,\"fileName\":\"Block-A.nwc\"}");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        [Fact]
        public async Task Links_the_cache_and_reports_it()
        {
            var handler = HappyPath();

            var outcome = await Run(handler, _nwcPath);

            Assert.True(outcome.Linked);
            Assert.Equal(555, outcome.LinkId);
            Assert.Contains(Path.GetFileName(_nwcPath), outcome.Message);

            // presign, PUT, link — in that order, and the key the server issued is
            // the one handed back rather than one the client invented.
            Assert.Equal(3, handler.Calls.Count);
            Assert.Contains("sync/init-link", handler.Calls[0]);
            Assert.Contains("obs.test/put", handler.Calls[1]);
            Assert.Contains("sync/link", handler.Calls[2]);
            Assert.Contains("bim-disciplines/7/Architecture/1.nwc", handler.Bodies[2]);
        }

        [Fact]
        public async Task Sends_the_design_id_and_declares_the_type_as_nwc()
        {
            var handler = HappyPath();

            await Run(handler, _nwcPath);

            string link = handler.Bodies[2];
            Assert.Contains("\"designId\":100", link);
            Assert.Contains("\"projectId\":7", link);
            Assert.Contains("\"fileType\":\"nwc\"", link);
            // NullValueHandling.Ignore: an absent note must not reach the wire as
            // null, since the server takes the field as optional.
            Assert.DoesNotContain("notes", link);
        }

        [Fact]
        public async Task A_rejected_link_is_reported_not_thrown()
        {
            var handler = new StubHandler(r =>
            {
                if (Is(r, "sync/init-link"))
                    return Json(HttpStatusCode.OK,
                        "{\"uploadUrl\":\"https://obs.test/put\",\"fileKey\":\"bim-disciplines/7/Architecture/1.nwc\"}");
                if (Is(r, "obs.test/put")) return new HttpResponseMessage(HttpStatusCode.OK);

                // What the guards answer for a folder design id or a foreign key.
                return Json(HttpStatusCode.BadRequest, "{\"message\":\"fileKey must belong to this project\"}");
            });

            var outcome = await Run(handler, _nwcPath);

            Assert.False(outcome.Linked);
            Assert.Null(outcome.LinkId);
            Assert.Contains("not linked", outcome.Message);
            // The reason survives into the message — "it failed" alone gives the
            // drafter nothing to act on.
            Assert.Contains("fileKey must belong to this project", outcome.Message);
        }

        [Fact]
        public async Task A_server_without_the_route_is_reported_as_such()
        {
            var handler = new StubHandler(r => Json(HttpStatusCode.NotFound, "{\"message\":\"Cannot POST\"}"));

            var outcome = await Run(handler, _nwcPath);

            Assert.False(outcome.Linked);
            Assert.Contains("not linked", outcome.Message);
            // One call: nothing is uploaded when there is nowhere to put it.
            Assert.Single(handler.Calls);
        }

        [Fact]
        public async Task An_answer_without_an_upload_url_stops_before_uploading()
        {
            var handler = new StubHandler(r => Json(HttpStatusCode.OK, "{}"));

            var outcome = await Run(handler, _nwcPath);

            Assert.False(outcome.Linked);
            Assert.Contains("does not accept NWC links yet", outcome.Message);
            Assert.Single(handler.Calls);
        }

        [Fact]
        public async Task A_failed_upload_never_reaches_the_link_call()
        {
            var handler = new StubHandler(r =>
            {
                if (Is(r, "sync/init-link"))
                    return Json(HttpStatusCode.OK,
                        "{\"uploadUrl\":\"https://obs.test/put\",\"fileKey\":\"bim-disciplines/7/Architecture/1.nwc\"}");

                // 4xx from storage: an expired or malformed URL. UploadAsync does
                // not retry those, so this is one attempt and out.
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            });

            var outcome = await Run(handler, _nwcPath);

            Assert.False(outcome.Linked);
            Assert.Contains("could not be uploaded", outcome.Message);
            Assert.DoesNotContain(handler.Calls, c => c.Contains("sync/link"));
        }

        [Fact]
        public async Task A_missing_file_costs_no_request_at_all()
        {
            var handler = HappyPath();

            var outcome = await Run(handler, Path.Combine(Path.GetTempPath(), "bina_nwc_does_not_exist.nwc"));

            Assert.False(outcome.Linked);
            Assert.Contains("not found on disk", outcome.Message);
            Assert.Empty(handler.Calls);
        }

        [Fact]
        public async Task No_api_client_is_an_outcome_rather_than_a_crash()
        {
            var outcome = await NwcLinkStep.RunAsync(null, 7, 100, "Architecture", _nwcPath);

            Assert.False(outcome.Linked);
            Assert.False(string.IsNullOrEmpty(outcome.Message));
        }
    }
}
