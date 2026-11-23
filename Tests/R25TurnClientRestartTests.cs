#nullable enable
// r25 audit (MUST): a backend restart while a write's results were on their way. Every results POST and
// the cancel were refused, so the turn stayed open in the cloud and the next send met 409 for ever, and the
// committed batch was never reported. Now a cancel that never reached the cloud is sent again, a 409 from a
// start re-cancels the session's last given-up turn and retries the start once, and a committed batch whose
// results could not be delivered is posted late (R74). Revit-free; runs in the macOS scratch harness.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R25TurnClientRestartTests
    {
        private const string Base = "https://bina-ai-stg-onebistro.binacloud.ai";
        private const string TurnA = "0b6f2a52-6a43-4c8e-9a51-3f2f1f6f8c11";
        private const string TurnB = "6c1e0f7d-2b4a-4f0e-8d0a-5b7d2e9c3a22";

        private sealed class FakeServer : HttpMessageHandler
        {
            private readonly object _gate = new object();
            private readonly List<(string Path, string Body)> _seen = new List<(string, string)>();
            private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _script = new Dictionary<string, Queue<Func<HttpResponseMessage>>>();

            public FakeServer On(string pathPrefix, Func<HttpResponseMessage> reply)
            {
                lock (_gate)
                {
                    if (!_script.TryGetValue(pathPrefix, out var q)) _script[pathPrefix] = q = new Queue<Func<HttpResponseMessage>>();
                    q.Enqueue(reply);
                }
                return this;
            }

            public List<(string Path, string Body)> Seen { get { lock (_gate) return _seen.ToList(); } }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.Method + " " + request.RequestUri!.PathAndQuery;
                var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
                Func<HttpResponseMessage>? reply = null;
                lock (_gate)
                {
                    _seen.Add((path, body));
                    var key = _script.Keys.Where(k => path.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
                    if (key != null && _script[key].Count > 0) reply = _script[key].Dequeue();
                }
                return reply != null ? reply() : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no script for " + path) };
            }
        }

        private static string Frame(string type, object data) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(data) + "\n\n";

        private static Func<HttpResponseMessage> Sse(string wire) => () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(wire))) };

        private static Func<HttpResponseMessage> Status(HttpStatusCode code, string body = "") => () =>
            new HttpResponseMessage(code) { Content = new StringContent(body) };

        private static Func<HttpResponseMessage> Drop() => () => throw new HttpRequestException("connection refused");

        private static Func<HttpResponseMessage> Busy409() => Status(HttpStatusCode.Conflict, "{\"detail\":\"a turn is already active for this session\"}");

        private static object Ask(long seq) => new { seq, request_id = "req_ask", question = "Aras mana?", multi = false, purpose = "clarify" };

        private static object TurnEnd(long seq) => new { seq, status = "done", session_id = "ses_4c1d9e7a" };

        private static object WriteRequest(long seq) => new
        {
            seq,
            request_id = "req_02",
            kind = "write",
            calls = new[] { new { call_id = "call_02", idempotency_key = TurnA + ":call_02", tool = "set_parameters", args = new { } } },
        };

        private sealed class Handler : ICopilotTurnHandler
        {
            public int Executions;
            public void OnProgress(ProgressDto p) { }
            public void OnReplyDelta(ReplyDeltaDto d) { }
            public void OnError(StreamErrorDto e) { }
            public SnapshotModel CurrentSnapshot() => new SnapshotModel { ChangeSeq = 18 };
            public Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct)
            {
                Executions++;
                return Task.FromResult(request.Calls.Select(_ => new Envelope
                {
                    Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Set Comments on 1 wall.", Count = 1,
                    Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 1 },
                }).ToArray());
            }
        }

        private static CopilotTurnClient ClientOn(FakeServer server) =>
            new CopilotTurnClient(new HttpClient(server), new CopilotClientOptions
            {
                BaseUrl = Base,
                Token = () => "tok-123",
                ReleaseBuild = true,
                Delay = (_, __) => Task.CompletedTask,
            });

        private static TurnRequestDto Turn(string id, string? session = null) => new TurnRequestDto { TurnId = id, SessionId = session, Text = "Tukar Comments" };

        private static async Task<bool> Eventually(Func<bool> condition)
        {
            for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
            return condition();
        }

        [Fact]
        public async Task ACancelThatNeverReachedTheCloud_IsSentAgain()
        {
            var server = new FakeServer()
                .On("POST /copilot/turns/" + TurnA + "/cancel", Drop())
                .On("POST /copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK, "{}"))
                .On("POST /copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK, "{}"));
            var client = ClientOn(server);

            await client.CancelAsync(TurnA, CancellationToken.None);   // refused: the backend is restarting
            await client.CancelAsync(TurnA, CancellationToken.None);   // sent again, and it lands
            await client.CancelAsync(TurnA, CancellationToken.None);   // once it landed, never again

            Assert.Equal(2, server.Seen.Count(r => r.Path.EndsWith("/cancel")));
        }

        [Fact]
        public async Task A409AfterAGivenUpTurn_CancelsThatTurnAgainAndRetriesTheStartOnce()
        {
            var server = new FakeServer()
                .On("POST /copilot/turns", Sse(Frame("ask", Ask(1))))
                .On("POST /copilot/turns/" + TurnA + "/cancel", Drop())                 // Stop while the backend restarts
                .On("POST /copilot/turns", Busy409())                                 // it came back still holding turn A
                .On("POST /copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK, "{}"))
                .On("POST /copilot/turns", Sse(Frame("turn_end", TurnEnd(1))));
            var client = ClientOn(server);
            var handler = new Handler();

            Assert.Equal(TurnOutcomeKind.Ask, (await client.StartTurnAsync(Turn(TurnA), handler, CancellationToken.None)).Kind);
            await client.CancelAsync(TurnA, CancellationToken.None);
            var outcome = await client.StartTurnAsync(Turn(TurnB), handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Ended, outcome.Kind);
            Assert.Equal(new[]
            {
                "POST /copilot/turns", "POST /copilot/turns/" + TurnA + "/cancel",
                "POST /copilot/turns", "POST /copilot/turns/" + TurnA + "/cancel", "POST /copilot/turns",
            }, server.Seen.Select(r => r.Path).ToArray());
            var starts = server.Seen.Where(r => r.Path == "POST /copilot/turns").ToList();
            Assert.Equal(starts[1].Body, starts[2].Body);                              // the same request, once more
            Assert.Contains(TurnB, starts[2].Body);
        }

        [Fact]
        public async Task A409ThatStaysAfterTheRetry_IsTurnActive_AndIsRetriedOnlyOnce()
        {
            var server = new FakeServer()
                .On("POST /copilot/turns", Sse(Frame("ask", Ask(1))))
                .On("POST /copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK, "{}"))
                .On("POST /copilot/turns", Busy409())
                .On("POST /copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK, "{}"))
                .On("POST /copilot/turns", Busy409())
                .On("POST /copilot/turns", Busy409());
            var client = ClientOn(server);
            var handler = new Handler();

            await client.StartTurnAsync(Turn(TurnA), handler, CancellationToken.None);
            await client.CancelAsync(TurnA, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.TurnActive, (await client.StartTurnAsync(Turn(TurnB), handler, CancellationToken.None)).Kind);
            Assert.Equal(2, server.Seen.Count(r => r.Path == "POST /copilot/turns" && r.Body.Contains(TurnB)));
            // The given-up turn was re-cancelled once; a later 409 does not cancel it again.
            Assert.Equal(TurnOutcomeKind.TurnActive, (await client.StartTurnAsync(Turn(TurnB), handler, CancellationToken.None)).Kind);
            Assert.Equal(2, server.Seen.Count(r => r.Path.EndsWith("/cancel")));
            Assert.Equal(3, server.Seen.Count(r => r.Path == "POST /copilot/turns" && r.Body.Contains(TurnB)));
        }

        [Fact]
        public async Task A409InAnotherChat_DoesNotCancelThisChatsGivenUpTurn()
        {
            var server = new FakeServer()
                .On("POST /copilot/turns", Sse(Frame("ask", Ask(1))))
                .On("POST /copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK, "{}"))
                .On("POST /copilot/turns", Busy409());
            var client = ClientOn(server);
            var handler = new Handler();

            await client.StartTurnAsync(Turn(TurnA, "ses_A"), handler, CancellationToken.None);
            await client.CancelAsync(TurnA, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.TurnActive, (await client.StartTurnAsync(Turn(TurnB, "ses_B"), handler, CancellationToken.None)).Kind);
            Assert.Single(server.Seen, r => r.Path.EndsWith("/cancel"));
            Assert.Equal(2, server.Seen.Count(r => r.Path == "POST /copilot/turns"));
        }

        [Fact]
        public async Task ACommittedBatchWhoseResultsNeverArrived_IsPostedLateAfterTheCancel()
        {
            var server = new FakeServer()
                .On("POST /copilot/turns", Sse(Frame("tool_request", WriteRequest(1))))
                .On("POST /copilot/turns/" + TurnA + "/results", Drop())
                .On("POST /copilot/turns/" + TurnA + "/results", Drop())
                .On("POST /copilot/turns/" + TurnA + "/results", Drop())
                .On("POST /copilot/turns/" + TurnA + "/results", Drop())
                .On("POST /copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK, "{}"))
                .On("POST /copilot/turns/" + TurnA + "/results", Status(HttpStatusCode.OK, "{}"));
            var handler = new Handler();

            var outcome = await ClientOn(server).StartTurnAsync(Turn(TurnA), handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Failed, outcome.Kind);
            Assert.True(outcome.ResultsUndelivered);
            Assert.Equal(1, handler.Executions);
            Assert.True(await Eventually(() => server.Seen.Count(r => r.Path.EndsWith("/results")) == 5), string.Join(" | ", server.Seen.Select(r => r.Path)));
            var seen = server.Seen;
            var cancelAt = seen.FindIndex(r => r.Path.EndsWith("/cancel"));
            var late = seen.FindLastIndex(r => r.Path.EndsWith("/results"));
            Assert.True(cancelAt >= 0 && cancelAt < late, "the cancel goes before the late results (M1)");
            Assert.Equal(seen.First(r => r.Path.EndsWith("/results")).Body, seen[late].Body);   // the same request_id and envelopes
            await Task.Delay(50);
            Assert.Equal(5, server.Seen.Count(r => r.Path.EndsWith("/results")));             // once, never more
        }
    }
}
