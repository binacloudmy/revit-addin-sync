#nullable enable
// W5 fix round 1: the pane's router (Revit-free; fake tools and a fake
// HttpMessageHandler). Review I3: one turn at a time, and each drive's events
// stay in its own turn. Also M1 (repeated progress frames share a row), M5
// (a cut table is said in the receipt), M7 (Stop posts cancel once) and the
// per-leg reply after an ask.

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
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotTurnRouterTests
    {
        private const string Base = "https://bina-ai-stg-onebistro.binacloud.ai";

        private sealed class Server : HttpMessageHandler
        {
            public readonly List<(HttpMethod Method, string Path, string Body)> Requests = new List<(HttpMethod, string, string)>();
            private readonly List<(Func<string, bool> Match, Func<HttpResponseMessage> Reply)> _routes = new List<(Func<string, bool>, Func<HttpResponseMessage>)>();

            public Server On(Func<string, bool> match, Func<HttpResponseMessage> reply) { _routes.Add((match, reply)); return this; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.PathAndQuery;
                var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
                lock (Requests) Requests.Add((request.Method, path, body));
                for (var i = 0; i < _routes.Count; i++)
                    if (_routes[i].Match(path)) { var r = _routes[i]; _routes.RemoveAt(i); return r.Reply(); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            }

            public int Count(Func<(HttpMethod Method, string Path, string Body), bool> p) { lock (Requests) return Requests.Count(p); }
        }

        private sealed class Tools : ITurnTools
        {
            public TaskCompletionSource<bool>? Gate;
            public int Reads;
            public ReceiptLanguage Language { get; set; }
            public SnapshotModel Snapshot() => new SnapshotModel();

            public async Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, CancellationToken ct)
            {
                Interlocked.Increment(ref Reads);
                if (Gate != null)
                {
                    using (ct.Register(() => Gate.TrySetCanceled()))
                        await Gate.Task.ConfigureAwait(false);
                }
                return calls.Select(_ => new Envelope { Ok = true, Status = "done", Phase = "read", Summary = "12 doors", Count = 12,
                    Searched = new SearchedDto { Sources = new List<string> { "host" } } }).ToArray();
            }

            public Task<Envelope[]> RunWritesAsync(string turnId, string requestId, IReadOnlyList<ToolCallDto> calls, string? description, CancellationToken ct) =>
                Task.FromResult(calls.Select(_ => EnvelopeFactory.Failed(ErrorCodes.NotImplemented, "n/a")).ToArray());
        }

        private static string Frame(string type, object data) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(data) + "\n\n";

        private static Func<HttpResponseMessage> Sse(string wire) => () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(wire))) };

        private static object ReadRequest(long seq) => new
        {
            seq, request_id = "req_r", kind = "read",
            calls = new[] { new { call_id = "c1", idempotency_key = "k1", tool = "find_elements", args = new { queries = new[] { new { category = "Doors" } } } } },
        };

        private static object End(long seq) => new { seq, status = "done", session_id = "ses_1", receipt_ids = new string[0] };

        private static bool IsStart(string p) => p == "/copilot/turns";
        private static bool IsResults(string p) => p.EndsWith("/results", StringComparison.Ordinal);
        private static bool IsCancel(string p) => p.EndsWith("/cancel", StringComparison.Ordinal);

        private static (CopilotTurnRouter Router, List<string> Ended) RouterOn(Server server, Tools tools)
        {
            var ended = new List<string>();
            var client = new CopilotTurnClient(new HttpClient(server), new CopilotClientOptions
            {
                BaseUrl = Base,
                Token = () => "t",
                ReleaseBuild = true,
                Delay = (_, __) => Task.CompletedTask,
            });
            var router = new CopilotTurnRouter(tools, client, new CopilotRouterHooks { OnTurnEnded = id => { lock (ended) ended.Add(id); } });
            return (router, ended);
        }

        [Fact]
        public async Task Router_RefusesASecondTurnWhileOneIsDriving()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("progress", new { seq = 1, kind = "thinking", text = "Reading" }) + Frame("tool_request", ReadRequest(2))))
                .On(IsResults, Sse(Frame("reply_delta", new { seq = 3, text = "12 pintu." }) + Frame("turn_end", End(4))));
            var tools = new Tools { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var (router, ended) = RouterOn(server, tools);

            var first = router.RouteAsync("berapa pintu di aras 2", "ai-generated");
            for (var i = 0; i < 200 && Volatile.Read(ref tools.Reads) == 0; i++) await Task.Delay(10);
            Assert.Equal(1, tools.Reads);
            Assert.True(router.IsBusy);

            var second = await router.RouteAsync("and the windows?", "ai-generated");

            Assert.Equal(RouteFailure.Busy, second.FailureKind);
            Assert.Equal("Finish or stop the current turn first.", second.PaneNote);
            Assert.Equal(1, server.Count(r => IsStart(r.Path)));          // nothing posted for the refused send

            tools.Gate.SetResult(true);
            var done = await first;

            Assert.Equal("12 pintu.", done.Reply);
            Assert.Equal(new[] { "find_elements" }, done.ToolsUsed);
            Assert.Single(done.Evidence!.Evidence);                       // the first turn's evidence, in the first turn
            Assert.False(router.IsBusy);
            Assert.Single(ended);
            Assert.Equal("Selesaikan atau hentikan giliran semasa dahulu.", CopilotTurnRouter.BusyNote(ReceiptLanguage.Ms));
        }

        [Fact]
        public async Task Router_ParkedAskIsNotBusy_TypedTextAnswersIt_ReplyIsPerLeg()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("reply_delta", new { seq = 1, text = "Saya perlu tahu satu perkara." })
                                 + Frame("ask", new { seq = 2, request_id = "req_q", question = "Aras mana?", multi = false, purpose = "clarify" })))
                .On(IsResults, Sse(Frame("reply_delta", new { seq = 3, text = "Baik, Aras 2." }) + Frame("turn_end", End(4))));
            var (router, ended) = RouterOn(server, new Tools());

            var asked = await router.RouteAsync("naikkan siling", "ai-generated");
            Assert.NotNull(asked.Ask);
            Assert.Equal("Saya perlu tahu satu perkara.", asked.Reply);
            Assert.False(router.IsBusy);                                  // parked on a question
            Assert.Empty(ended);

            var answered = await router.RouteAsync("Aras 2", "ai-generated");

            Assert.Equal("Baik, Aras 2.", answered.Reply);                 // only this leg's text
            var post = Assert.Single(server.Requests, r => IsResults(r.Path));
            using var body = JsonDocument.Parse(post.Body);
            Assert.Equal("Aras 2", body.RootElement.GetProperty("answer").GetProperty("values").GetProperty("text").GetString());
            Assert.Single(ended);
        }

        [Fact]
        public async Task Router_RepeatedProgressFramesShareARow()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("progress", new { seq = 1, kind = "thinking", text = "Reading the request" })
                                 + Frame("progress", new { seq = 3, kind = "thinking", text = "Reading the request" })
                                 + Frame("progress", new { seq = 5, kind = "thinking", text = "Reading the request" })
                                 + Frame("progress", new { seq = 6, kind = "running", text = "Preparing" })
                                 + Frame("turn_end", End(7))));
            var (router, _) = RouterOn(server, new Tools());

            var r = await router.RouteAsync("hello", "ai-generated");

            Assert.Equal(1, r.Steps!.Count(s => s.Label == "Reading the request"));
            Assert.Equal(1, r.Steps!.Count(s => s.Label == "Preparing"));
        }

        [Fact]
        public async Task Router_CutTable_IsSaidInTheReceipt_AndSentAsATable()
        {
            var server = new Server().On(IsStart, Sse(Frame("turn_end", End(1))));
            var (router, _) = RouterOn(server, new Tools());
            var csv = "Mark,Value\n" + string.Join("\n", Enumerable.Range(1, TableAttachment.MaxRows + 3).Select(i => "D-" + i + "," + i));
            router.PendingTables = new List<TableData> { TableAttachment.ParseCsv("doors.csv", csv) };

            var r = await router.RouteAsync("set marks from the table", "ai-generated");

            var note = Assert.Single(r.Evidence!.Notes);
            Assert.Contains("first 2000 of 2003 rows of doors.csv", note);
            var start = Assert.Single(server.Requests, x => IsStart(x.Path));
            using var body = JsonDocument.Parse(start.Body);
            var a = body.RootElement.GetProperty("attachments")[0];
            Assert.Equal("table", a.GetProperty("kind").GetString());
            Assert.Equal(TableAttachment.MaxRows, a.GetProperty("rows").GetArrayLength());
        }

        [Fact]
        public async Task Router_StopDuringATool_PostsCancelOnce_AndEndsTheTurn()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("tool_request", ReadRequest(1))))
                .On(IsCancel, () => new HttpResponseMessage(HttpStatusCode.OK))
                .On(IsCancel, () => new HttpResponseMessage(HttpStatusCode.OK));
            var tools = new Tools { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var (router, ended) = RouterOn(server, tools);

            var running = router.RouteAsync("find doors", "ai-generated");
            for (var i = 0; i < 200 && Volatile.Read(ref tools.Reads) == 0; i++) await Task.Delay(10);
            router.CancelStream();
            var r = await running;
            await Task.Delay(50);

            Assert.True(r.Interrupted);
            Assert.Equal("Cancelled.", r.PaneNote);
            Assert.Equal(1, server.Count(x => IsCancel(x.Path)));
            Assert.Equal(0, server.Count(x => IsResults(x.Path)));
            Assert.Single(ended);
            Assert.False(router.IsBusy);
        }

        [Fact]
        public async Task Router_RefusesWhileAForcedUpdateIsPending()
        {
            var server = new Server();
            var client = new CopilotTurnClient(new HttpClient(server), new CopilotClientOptions { BaseUrl = Base, Token = () => "t", ReleaseBuild = true });
            var router = new CopilotTurnRouter(new Tools(), client, new CopilotRouterHooks
            {
                UpdateBlocked = () => true,
                UpdateBlockedMessage = () => "Update first.",
            });

            var r = await router.RouteAsync("hello", "ai-generated");

            Assert.Equal(RouteFailure.UpdateRequired, r.FailureKind);
            Assert.Equal("Update first.", r.PaneNote);
            Assert.Empty(server.Requests);
        }
    }
}
