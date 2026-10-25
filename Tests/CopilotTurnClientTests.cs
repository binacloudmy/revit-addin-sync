#nullable enable
// W5 Task 2: the protocol-3 turn client against a fake HttpMessageHandler.
// Revit-free; runs in the macOS scratch harness.
//
// Review Focus 5 (plan): the network drops after a write committed. The results
// POST is retried with the same request_id, events replay after the last seq,
// and the write is never applied twice.

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
    public class CopilotTurnClientTests
    {
        private const string Base = "https://bina-ai-stg-onebistro.binacloud.ai";
        private const string TurnA = "0b6f2a52-6a43-4c8e-9a51-3f2f1f6f8c11";

        // ------------------------------------------------------------------
        // Fakes
        // ------------------------------------------------------------------

        private sealed class Recorded
        {
            public HttpMethod Method = HttpMethod.Get;
            public string PathAndQuery = "";
            public string Body = "";
            public string? Authorization;
            public string? Accept;
        }

        /// <summary>Answers each request with the next scripted reply for its path
        /// prefix. A reply is a function so it can throw (network drop).</summary>
        private sealed class FakeServer : HttpMessageHandler
        {
            public readonly List<Recorded> Requests = new List<Recorded>();
            private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _script =
                new Dictionary<string, Queue<Func<HttpResponseMessage>>>();

            public FakeServer On(string pathPrefix, Func<HttpResponseMessage> reply)
            {
                if (!_script.TryGetValue(pathPrefix, out var q)) _script[pathPrefix] = q = new Queue<Func<HttpResponseMessage>>();
                q.Enqueue(reply);
                return this;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var rec = new Recorded
                {
                    Method = request.Method,
                    PathAndQuery = request.RequestUri!.PathAndQuery,
                    Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(),
                    Authorization = request.Headers.Authorization?.ToString(),
                    Accept = request.Headers.Accept.ToString(),
                };
                Requests.Add(rec);
                var key = _script.Keys.Where(k => rec.PathAndQuery.StartsWith(k, StringComparison.Ordinal))
                    .OrderByDescending(k => k.Length).FirstOrDefault();
                if (key == null || _script[key].Count == 0)
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no script for " + rec.PathAndQuery) };
                return _script[key].Dequeue()();
            }
        }

        private static string Frame(string type, object data) =>
            "event: " + type + "\ndata: " + JsonSerializer.Serialize(data) + "\n\n";

        private static Func<HttpResponseMessage> Sse(string wire) => () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(wire))),
            };

        private static Func<HttpResponseMessage> Status(HttpStatusCode code, string body = "") => () =>
            new HttpResponseMessage(code) { Content = new StringContent(body) };

        private static Func<HttpResponseMessage> Drop() => () => throw new HttpRequestException("connection reset");

        private sealed class FakeHandler : ICopilotTurnHandler
        {
            public int Executions;
            public readonly List<string> Replies = new List<string>();
            public readonly List<string> Progress = new List<string>();
            public readonly List<StreamErrorDto> Errors = new List<StreamErrorDto>();
            public Func<ToolRequestDto, Envelope[]>? Run;

            public void OnProgress(ProgressDto p) => Progress.Add(p.Kind + ":" + p.Text);
            public void OnReplyDelta(ReplyDeltaDto d) => Replies.Add(d.Text);
            public void OnError(StreamErrorDto e) => Errors.Add(e);
            public SnapshotModel CurrentSnapshot() => new SnapshotModel { ChangeSeq = 18 };

            public Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct)
            {
                Executions++;
                var envs = Run != null
                    ? Run(request)
                    : request.Calls.Select(_ => Committed()).ToArray();
                return Task.FromResult(envs);
            }
        }

        private static Envelope Committed() => new Envelope
        {
            Ok = true,
            Status = EnvelopeStatus.Done,
            Phase = EnvelopePhase.Commit,
            Summary = "Set Height Offset From Level = 2700 mm on 34 ceilings.",
            Count = 34,
            Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 34 },
        };

        private static object WriteRequest(long seq, string requestId = "req_02") => new
        {
            seq,
            request_id = requestId,
            kind = "write",
            calls = new[]
            {
                new { call_id = "call_02", idempotency_key = TurnA + ":call_02", tool = "set_parameters",
                      args = new { target = new { handle = "h_7f3a91c2" }, param = "Height Offset From Level", value = "2700 mm" } },
            },
            description = "Naikkan 34 siling di Aras 2 ke 2700 mm.",
        };

        private static object TurnEnd(long seq) => new { seq, status = "done", session_id = "ses_4c1d9e7a", receipt_ids = new[] { "r1" } };

        private static CopilotTurnClient ClientOn(FakeServer server, string token = "tok-123") =>
            new CopilotTurnClient(new HttpClient(server), new CopilotClientOptions
            {
                BaseUrl = Base,
                Token = () => token,
                ReleaseBuild = true,
                Delay = (_, __) => Task.CompletedTask,   // no real backoff in tests
            });

        private static TurnRequestDto Turn(string id = TurnA) => new TurnRequestDto { TurnId = id, Text = "Naikkan siling Aras 2 ke 2700 mm" };

        // ------------------------------------------------------------------
        // Review Focus 5
        // ------------------------------------------------------------------

        [Fact]
        public async Task Client_ResumesEventsAfterSeqAndRepostsResultsIdempotently()
        {
            var server = new FakeServer()
                .On("/copilot/turns/" + TurnA + "/results", Drop())   // the write committed; the POST never arrived
                .On("/copilot/turns/" + TurnA + "/results",
                    // Retried POST answered, then the stream drops mid-frame.
                    Sse(Frame("reply_delta", new { seq = 3, text = "Siap: 34 siling " }) + "event: reply_delta\ndata: {\"seq\":4,\"te"))
                .On("/copilot/turns/" + TurnA + "/events",
                    // Replay after seq 3: a duplicate of seq 3, then the rest.
                    Sse(Frame("reply_delta", new { seq = 3, text = "Siap: 34 siling " })
                        + Frame("reply_delta", new { seq = 4, text = "kini pada 2700 mm." })
                        + Frame("turn_end", TurnEnd(5))))
                .On("/copilot/turns",
                    Sse(Frame("progress", new { seq = 1, kind = "awaiting_confirm", text = "Waiting for your approval in Revit" })
                        + Frame("tool_request", WriteRequest(2))));
            var handler = new FakeHandler();
            var client = ClientOn(server);

            var outcome = await client.StartTurnAsync(Turn(), handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Ended, outcome.Kind);
            Assert.Equal("ses_4c1d9e7a", outcome.End!.SessionId);
            Assert.Equal(1, handler.Executions);                      // never applied twice

            var posts = server.Requests.Where(r => r.PathAndQuery.EndsWith("/results")).ToList();
            Assert.Equal(2, posts.Count);                             // retried once
            Assert.Equal(posts[0].Body, posts[1].Body);               // same body, same request_id
            using (var body = JsonDocument.Parse(posts[0].Body))
            {
                Assert.Equal("req_02", body.RootElement.GetProperty("request_id").GetString());
                Assert.Equal(3, body.RootElement.GetProperty("protocol").GetInt32());
                Assert.Equal("call_02", body.RootElement.GetProperty("results")[0].GetProperty("call_id").GetString());
            }

            var replay = Assert.Single(server.Requests, r => r.PathAndQuery.Contains("/events"));
            Assert.Equal("/copilot/turns/" + TurnA + "/events?after=3", replay.PathAndQuery);
            Assert.Equal(new[] { "Siap: 34 siling ", "kini pada 2700 mm." }, handler.Replies);   // seq 3 once
            Assert.All(server.Requests, r => Assert.Equal("Bearer tok-123", r.Authorization));
            Assert.Contains(server.Requests, r => r.Method == HttpMethod.Post && r.Accept!.Contains("text/event-stream"));
            Assert.Null(client.ActiveTurnId);                         // turn_end unbinds
        }

        [Fact]
        public async Task Client_ReplayedToolRequestWithSameRequestId_RepostsCachedResultsWithoutRunning()
        {
            var server = new FakeServer()
                .On("/copilot/turns/" + TurnA + "/results",
                    // The cloud lost our results and asks again under a new seq.
                    Sse(Frame("tool_request", WriteRequest(3))))
                .On("/copilot/turns/" + TurnA + "/results", Sse(Frame("turn_end", TurnEnd(4))))
                .On("/copilot/turns", Sse(Frame("tool_request", WriteRequest(2))));
            var handler = new FakeHandler();

            var outcome = await ClientOn(server).StartTurnAsync(Turn(), handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Ended, outcome.Kind);
            Assert.Equal(1, handler.Executions);
            var posts = server.Requests.Where(r => r.PathAndQuery.EndsWith("/results")).ToList();
            Assert.Equal(2, posts.Count);
            Assert.Equal(posts[0].Body, posts[1].Body);
        }

        [Fact]
        public async Task Client_ResultsPostFailsFourTimes_FailsWithoutRunningAgain()
        {
            var server = new FakeServer()
                .On("/copilot/turns/" + TurnA + "/results", Drop())
                .On("/copilot/turns/" + TurnA + "/results", Status(HttpStatusCode.BadGateway))
                .On("/copilot/turns/" + TurnA + "/results", Drop())
                .On("/copilot/turns/" + TurnA + "/results", Status(HttpStatusCode.ServiceUnavailable))
                .On("/copilot/turns", Sse(Frame("tool_request", WriteRequest(2))));
            var handler = new FakeHandler();

            var outcome = await ClientOn(server).StartTurnAsync(Turn(), handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Failed, outcome.Kind);
            Assert.True(outcome.ResultsUndelivered);
            Assert.Equal(1, handler.Executions);
            Assert.Equal(4, server.Requests.Count(r => r.PathAndQuery.EndsWith("/results")));   // 1 + 3 retries
        }

        // ------------------------------------------------------------------
        // Turn binding
        // ------------------------------------------------------------------

        [Fact]
        public async Task RefusesToolRequestForOtherTurn()
        {
            const string TurnB = "5d0e7c3a-91b4-4f6e-8d27-0c4a6b1e9f30";
            var server = new FakeServer()
                .On("/copilot/turns/" + TurnA + "/events", Sse(Frame("tool_request", WriteRequest(2))))
                .On("/copilot/turns", Sse(Frame("progress", new { seq = 1, kind = "thinking", text = "x" })
                                          + Frame("ask", new { seq = 2, request_id = "req_q", question = "Aras mana?", multi = false, purpose = "clarify" })));
            var handler = new FakeHandler();
            var client = ClientOn(server);

            // The pane's own turn is B (parked on a question); A is not ours.
            var b = await client.StartTurnAsync(Turn(TurnB), handler, CancellationToken.None);
            Assert.Equal(TurnOutcomeKind.Ask, b.Kind);
            Assert.Equal(TurnB, client.ActiveTurnId);

            var a = await client.ReplayAsync(TurnA, 0, handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Refused, a.Kind);
            Assert.Equal(0, handler.Executions);
            Assert.DoesNotContain(server.Requests, r => r.PathAndQuery.EndsWith("/results"));
            Assert.Equal(TurnB, client.ActiveTurnId);                 // our turn stays bound
        }

        [Fact]
        public async Task RefusesToolRequestArrivingAfterCancel()
        {
            var server = new FakeServer()
                .On("/copilot/turns/" + TurnA + "/cancel", Status(HttpStatusCode.OK))
                .On("/copilot/turns", Sse(Frame("progress", new { seq = 1, kind = "thinking", text = "x" })
                                          + Frame("tool_request", WriteRequest(2))));
            var client = ClientOn(server);
            var handler = new CancellingHandler(client);

            // The drafter hits Stop while the stream is still open: the
            // tool_request that follows on the same stream must not run.
            var outcome = await client.StartTurnAsync(Turn(), handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Refused, outcome.Kind);
            Assert.Equal(0, handler.Executions);
            Assert.Null(client.ActiveTurnId);
            Assert.DoesNotContain(server.Requests, r => r.PathAndQuery.EndsWith("/results"));
            Assert.Contains(server.Requests, r => r.Method == HttpMethod.Post && r.PathAndQuery == "/copilot/turns/" + TurnA + "/cancel");

            // And a later replay of that turn is refused before anything is sent.
            var before = server.Requests.Count;
            var replay = await client.ReplayAsync(TurnA, 0, handler, CancellationToken.None);
            Assert.Equal(TurnOutcomeKind.Refused, replay.Kind);
            Assert.Equal(before, server.Requests.Count);
        }

        private sealed class CancellingHandler : ICopilotTurnHandler
        {
            private readonly CopilotTurnClient _client;
            public int Executions;
            public CancellingHandler(CopilotTurnClient client) { _client = client; }
            public void OnProgress(ProgressDto p) => _client.CancelAsync(TurnA, CancellationToken.None).GetAwaiter().GetResult();
            public void OnReplyDelta(ReplyDeltaDto d) { }
            public void OnError(StreamErrorDto e) { }
            public SnapshotModel CurrentSnapshot() => new SnapshotModel();
            public Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct)
            {
                Executions++;
                return Task.FromResult(request.Calls.Select(_ => Committed()).ToArray());
            }
        }

        [Fact]
        public async Task AnswerContinuesTheParkedTurn()
        {
            var server = new FakeServer()
                .On("/copilot/turns/" + TurnA + "/results", Sse(Frame("reply_delta", new { seq = 2, text = "Baik." }) + Frame("turn_end", TurnEnd(3))))
                .On("/copilot/turns", Sse(Frame("ask", new { seq = 1, request_id = "req_03", question = "Simpan nota?", multi = false,
                                                         purpose = "project_note", note_text = "Office ceilings at 2700 mm.",
                                                         options = new[] { new { id = "yes", label = "Ya, simpan" }, new { id = "no", label = "Tidak" } } })));
            var handler = new FakeHandler();
            var client = ClientOn(server);

            var first = await client.StartTurnAsync(Turn(), handler, CancellationToken.None);
            Assert.Equal(TurnOutcomeKind.Ask, first.Kind);
            Assert.Equal(AskPurposes.ProjectNote, first.Ask!.Purpose);

            var answer = new AnswerDto();
            answer.Values["yes"] = JsonSerializer.SerializeToElement(true);
            var done = await client.AnswerAsync(TurnA, "req_03", answer, handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Ended, done.Kind);
            var post = Assert.Single(server.Requests, r => r.PathAndQuery.EndsWith("/results"));
            using var body = JsonDocument.Parse(post.Body);
            Assert.Equal("req_03", body.RootElement.GetProperty("request_id").GetString());
            Assert.True(body.RootElement.GetProperty("answer").GetProperty("values").GetProperty("yes").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("results", out _));
            Assert.Equal(18, body.RootElement.GetProperty("snapshot").GetProperty("change_seq").GetInt64());
        }

        // ------------------------------------------------------------------
        // Status codes, host policy, budgets on the wire
        // ------------------------------------------------------------------

        [Fact]
        public async Task Status426_IsUpdateRequired_409_IsTurnActive_401_IsUnauthorized()
        {
            var server = new FakeServer()
                .On("/copilot/turns", Status((HttpStatusCode)426, "{\"update_required\":true}"))
                .On("/copilot/turns", Status(HttpStatusCode.Conflict))
                .On("/copilot/turns", Status(HttpStatusCode.Unauthorized));
            var handler = new FakeHandler();
            var client = ClientOn(server);

            Assert.Equal(TurnOutcomeKind.UpdateRequired, (await client.StartTurnAsync(Turn(), handler, CancellationToken.None)).Kind);
            Assert.Null(client.ActiveTurnId);
            Assert.Equal(TurnOutcomeKind.TurnActive, (await client.StartTurnAsync(Turn(), handler, CancellationToken.None)).Kind);
            Assert.Equal(TurnOutcomeKind.Unauthorized, (await client.StartTurnAsync(Turn(), handler, CancellationToken.None)).Kind);
            Assert.Equal(0, handler.Executions);
        }

        [Fact]
        public async Task ErrorEventThenTurnEnd_ReportsTheError()
        {
            var server = new FakeServer()
                .On("/copilot/turns", Sse(Frame("error", new { seq = 1, code = "plan_limit", message = "This month's copilot credits are used up." })
                                          + Frame("turn_end", new { seq = 2, status = "failed", session_id = "s", receipt_ids = new string[0] })));
            var handler = new FakeHandler();

            var outcome = await ClientOn(server).StartTurnAsync(Turn(), handler, CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Ended, outcome.Kind);
            Assert.Equal(TurnEndStatus.Failed, outcome.End!.Status);
            Assert.Equal("plan_limit", outcome.Error!.Code);
            Assert.Single(handler.Errors);
        }

        [Theory]
        [InlineData("https://bina-ai-prod-ready.binacloud.ai", true, true)]
        [InlineData("https://bina-ai-stg-onebistro.binacloud.ai", true, true)]
        [InlineData("https://binacloud.ai", true, true)]
        [InlineData("http://bina-ai-prod-ready.binacloud.ai", true, false)]          // https only
        [InlineData("https://binacloud.ai.evil.example", true, false)]
        [InlineData("https://evilbinacloud.ai", true, false)]
        [InlineData("https://michelina-extrajudicial-logily.ngrok-free.dev", true, false)]
        [InlineData("http://localhost:8000", true, false)]
        [InlineData("http://localhost:8000", false, true)]                         // debug builds: any host
        [InlineData("not a url", false, false)]
        public void HostPolicy_ReleaseBuildsAcceptOnlyHttpsBinacloud(string baseUrl, bool release, bool allowed)
        {
            Assert.Equal(allowed, CopilotHostPolicy.IsAllowed(baseUrl, release));
        }

        [Fact]
        public async Task ReleaseBuild_DisallowedHost_SendsNothing()
        {
            var server = new FakeServer();
            var client = new CopilotTurnClient(new HttpClient(server), new CopilotClientOptions
            {
                BaseUrl = "https://example.com",
                Token = () => "t",
                ReleaseBuild = true,
            });

            var outcome = await client.StartTurnAsync(Turn(), new FakeHandler(), CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.Failed, outcome.Kind);
            Assert.Empty(server.Requests);
        }

        [Fact]
        public async Task CallsBeyondTheTurnBudget_AreRefusedWithoutRunning()
        {
            var calls = Enumerable.Range(1, 3).Select(i => new
            {
                call_id = "c" + i, idempotency_key = "k" + i, tool = "inspect",
                args = new { target = new { selection = true } },
            }).ToArray();
            var server = new FakeServer()
                .On("/copilot/turns/" + TurnA + "/results", Sse(Frame("turn_end", TurnEnd(2))))
                .On("/copilot/turns", Sse(Frame("tool_request", new { seq = 1, request_id = "r1", kind = "read", calls })));
            var ran = new List<string>();
            var handler = new FakeHandler
            {
                Run = req =>
                {
                    ran.AddRange(req.Calls.Select(c => c.CallId));
                    return req.Calls.Select(_ => new Envelope { Ok = true, Status = "done", Phase = "read", Summary = "ok" }).ToArray();
                },
            };
            var client = new CopilotTurnClient(new HttpClient(server), new CopilotClientOptions
            {
                BaseUrl = Base,
                Token = () => "t",
                ReleaseBuild = true,
                Delay = (_, __) => Task.CompletedTask,
                Budget = () => { var b = new TurnBudget(); b.Narrow(calls: 2, bytes: null, images: null); return b; },
            });

            await client.StartTurnAsync(Turn(), handler, CancellationToken.None);

            Assert.Equal(new[] { "c1", "c2" }, ran);
            var post = Assert.Single(server.Requests, r => r.PathAndQuery.EndsWith("/results"));
            using var body = JsonDocument.Parse(post.Body);
            var results = body.RootElement.GetProperty("results");
            Assert.Equal(3, results.GetArrayLength());                         // one envelope per call, in order
            Assert.Equal("c3", results[2].GetProperty("call_id").GetString());
            Assert.Equal("not_allowed", results[2].GetProperty("envelope").GetProperty("error").GetProperty("code").GetString());
        }
    }
}
