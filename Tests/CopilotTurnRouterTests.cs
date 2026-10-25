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
            /// <summary>"in &lt;path&gt;" when a request arrives, "out &lt;path&gt;" once it is answered.</summary>
            public readonly List<string> Log = new List<string>();
            private readonly List<(Func<string, bool> Match, Func<HttpResponseMessage> Reply)> _routes = new List<(Func<string, bool>, Func<HttpResponseMessage>)>();

            public Server On(Func<string, bool> match, Func<HttpResponseMessage> reply) { _routes.Add((match, reply)); return this; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.PathAndQuery;
                var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
                lock (Requests) { Requests.Add((request.Method, path, body)); Log.Add("in " + path); }
                Func<HttpResponseMessage>? reply = null;
                lock (Requests)
                    for (var i = 0; i < _routes.Count; i++)
                        if (_routes[i].Match(path)) { reply = _routes[i].Reply; _routes.RemoveAt(i); break; }
                var resp = reply != null ? reply() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
                lock (Requests) Log.Add("out " + path);
                return resp;
            }

            public int Count(Func<(HttpMethod Method, string Path, string Body), bool> p) { lock (Requests) return Requests.Count(p); }
        }

        private sealed class Tools : ITurnTools
        {
            public TaskCompletionSource<bool>? Gate;
            public int Reads;
            public ReceiptLanguage Language { get; set; }
            /// <summary>What Revit's live snapshot says right now (tests switch it).</summary>
            public SnapshotModel Live = new SnapshotModel();
            public readonly List<SnapshotModel> ReadSnapshots = new List<SnapshotModel>();
            public readonly List<SnapshotModel> WriteSnapshots = new List<SnapshotModel>();
            public Action? AfterRead;
            public SnapshotModel Snapshot() => Live;

            public async Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel turnSnapshot, CancellationToken ct)
            {
                Interlocked.Increment(ref Reads);
                lock (ReadSnapshots) ReadSnapshots.Add(turnSnapshot);
                if (Gate != null)
                {
                    using (ct.Register(() => Gate.TrySetCanceled()))
                        await Gate.Task.ConfigureAwait(false);
                }
                AfterRead?.Invoke();
                return calls.Select(_ => new Envelope { Ok = true, Status = "done", Phase = "read", Summary = "12 doors", Count = 12,
                    Searched = new SearchedDto { Sources = new List<string> { "host" } } }).ToArray();
            }

            /// <summary>The confirm card: open until the turn's token cancels it (the pane's
            /// card declines on cancellation, ConfirmCardModelTests.CancellationDeclines).</summary>
            public TaskCompletionSource<bool>? CardOpen;
            public volatile bool CardDeclined;
            /// <summary>What the batch returns once the token cancels (default: declined).
            /// A commit already under way in Revit when Stop comes still lands (R74).</summary>
            public Func<Envelope>? AfterStop;

            public async Task<Envelope[]> RunWritesAsync(string turnId, string requestId, IReadOnlyList<ToolCallDto> calls, string? description,
                SnapshotModel turnSnapshot, CancellationToken ct)
            {
                lock (WriteSnapshots) WriteSnapshots.Add(turnSnapshot);
                if (CardOpen != null)
                {
                    var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => decision.TrySetResult(false)))
                    {
                        CardOpen.TrySetResult(true);
                        CardDeclined = !await decision.Task.ConfigureAwait(false);
                    }
                    if (AfterStop != null) return calls.Select(_ => AfterStop()).ToArray();
                    return calls.Select(_ => EnvelopeFactory.Failed(ErrorCodes.NotImplemented, "declined")).ToArray();
                }
                return calls.Select(_ => EnvelopeFactory.Failed(ErrorCodes.NotImplemented, "n/a")).ToArray();
            }
        }

        private static string Frame(string type, object data) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(data) + "\n\n";

        private static Func<HttpResponseMessage> Sse(string wire) => () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(wire))) };

        private static object ReadRequest(long seq, string requestId = "req_r") => new
        {
            seq, request_id = requestId, kind = "read",
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
        public async Task Router_CredentialAsk_TypedReplyIsRefused_DontAnswerStillWorks()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("ask", new { seq = 1, request_id = "req_q", question = "Paste your BINA password so I can sync.", multi = false, purpose = "clarify" })))
                .On(IsResults, Sse(Frame("reply_delta", new { seq = 2, text = "OK, skipping the sync." }) + Frame("turn_end", End(3))));
            var (router, ended) = RouterOn(server, new Tools());

            var asked = await router.RouteAsync("sync the model", "ai-generated");
            var card = asked.Ask!;
            Assert.True(card.AsksForCredential);

            var typed = await router.RouteAsync("hunter2", "ai-generated");

            Assert.Equal(RouteFailure.Refused, typed.FailureKind);
            Assert.Equal(card.TypedRefusalNote, typed.PaneNote);
            Assert.Same(card, router.PendingAsk);                                   // still parked
            Assert.Null(card.AnsweredSummary);                                      // not marked answered
            Assert.Equal(0, server.Count(r => IsResults(r.Path)));
            Assert.Equal(0, server.Count(r => r.Body.Contains("hunter2")));        // never left the pane
            Assert.False(router.IsBusy);

            var declined = await router.SubmitAskAsync(card, card.AnswerDeclined());

            Assert.Equal("OK, skipping the sync.", declined.Reply);
            var post = Assert.Single(server.Requests, r => IsResults(r.Path));
            using var body = JsonDocument.Parse(post.Body);
            Assert.Equal("", body.RootElement.GetProperty("answer").GetProperty("values").GetProperty("text").GetString());
            Assert.Single(ended);
        }

        [Fact]
        public async Task Router_ContinueFromHistoryDuringARunningTurn_StopsIt_DeclinesTheCard_KeepsTheAdoptedSession()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("reply_delta", new { seq = 1, text = "Chat A: " }) + Frame("tool_request", WriteRequest(2))))
                // Never reached: the old chat's turn must not post results or end with its session.
                .On(IsResults, Sse(Frame("reply_delta", new { seq = 3, text = "4 doors changed." }) + Frame("turn_end", End(4))))
                .On(IsCancel, () => new HttpResponseMessage(HttpStatusCode.OK));
            var tools = new Tools { CardOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var (router, ended) = RouterOn(server, tools);
            var drawn = 0;
            router.OnSteps = _ => Interlocked.Increment(ref drawn);
            router.OnCodeStream = _ => Interlocked.Increment(ref drawn);

            var running = router.RouteAsync("tukar mark pintu", "ai-generated");
            Assert.Same(tools.CardOpen.Task, await Task.WhenAny(tools.CardOpen.Task, Task.Delay(5000)));   // the confirm card is up
            var drawnBefore = Volatile.Read(ref drawn);

            router.AdoptSession("ses_B");                                    // continue chat B from History
            Assert.Same(running, await Task.WhenAny(running, Task.Delay(5000)));
            var old = await running;
            await Task.Delay(50);

            Assert.True(tools.CardDeclined);                                  // the open card declined
            Assert.True(old.Interrupted);
            Assert.True(old.Superseded);                                      // the pane draws and records none of it
            Assert.Equal(drawnBefore, Volatile.Read(ref drawn));              // nothing streamed into chat B
            Assert.Equal("ses_B", router.SessionId);                          // not overwritten by chat A's turn
            Assert.Equal(1, server.Count(x => IsCancel(x.Path)));
            Assert.Equal(0, server.Count(x => IsResults(x.Path)));
            Assert.Single(ended);
            Assert.False(router.IsBusy);

            // Chat B's next message goes to chat B's session and reaches the pane.
            server.On(IsStart, Sse(Frame("reply_delta", new { seq = 1, text = "Chat B." })
                                   + Frame("turn_end", new { seq = 2, status = "done", session_id = "ses_B", receipt_ids = new string[0] })));
            var next = await router.RouteAsync("and the windows?", "ai-generated");

            Assert.False(next.Superseded);
            Assert.Equal("Chat B.", next.Reply);
            var start = server.Requests.Last(x => IsStart(x.Path));
            using var body = JsonDocument.Parse(start.Body);
            Assert.Equal("ses_B", body.RootElement.GetProperty("session_id").GetString());
        }

        /// <summary>Start a turn whose write batch is under way, press Stop, and return
        /// the server, the result and every reply text streamed to the pane after Stop.</summary>
        private static async Task<(Server Server, RouteResult Result, CopilotTurnRouter Router, List<string> StreamedAfterStop)> StopDuringABatch(
            Func<Envelope> afterStop, int cancelReplyMs = 0)
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("tool_request", WriteRequest(1))))
                // The late post's reply: never read, so none of it reaches the pane.
                .On(IsResults, Sse(Frame("reply_delta", new { seq = 2, text = "Not for the pane." })
                                   + Frame("turn_end", new { seq = 3, status = "done", session_id = "ses_late", receipt_ids = new string[0] })))
                // A slow cloud answer to the cancel: the late results must still come after it (M1).
                .On(IsCancel, () => { if (cancelReplyMs > 0) Thread.Sleep(cancelReplyMs); return new HttpResponseMessage(HttpStatusCode.OK); });
            var tools = new Tools
            {
                CardOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                AfterStop = afterStop,
            };
            var (router, _) = RouterOn(server, tools);
            var streamed = new List<string>();
            var running = router.RouteAsync("tukar mark pintu", "ai-generated");
            Assert.Same(tools.CardOpen.Task, await Task.WhenAny(tools.CardOpen.Task, Task.Delay(5000)));
            router.OnCodeStream = t => { lock (streamed) streamed.Add(t); };

            router.CancelStream();
            Assert.Same(running, await Task.WhenAny(running, Task.Delay(5000)));
            var r = await running;
            for (var i = 0; i < 300 && server.Count(x => IsResults(x.Path)) == 0; i++) await Task.Delay(10);
            await Task.Delay(100);                                            // room for a second (wrong) post
            lock (streamed) return (server, r, router, streamed.ToList());
        }

        [Fact]
        public async Task Router_CommitThenStop_PostsThatBatchOnce_Late_AndDrawsNothing()
        {
            var (server, r, router, streamed) = await StopDuringABatch(() => new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Mark set on 4 doors", Count = 4,
            }, cancelReplyMs: 300);

            Assert.True(r.Interrupted);
            var post = Assert.Single(server.Requests, x => IsResults(x.Path));      // once
            using var body = JsonDocument.Parse(post.Body);
            Assert.Equal("req_w", body.RootElement.GetProperty("request_id").GetString());
            var result = Assert.Single(body.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("w1", result.GetProperty("call_id").GetString());
            Assert.Equal("commit", result.GetProperty("envelope").GetProperty("phase").GetString());
            Assert.False(body.RootElement.TryGetProperty("after_cancel", out _));  // results_request is closed in the schema
            Assert.Equal(1, server.Count(x => IsCancel(x.Path)));
            List<string> log; lock (server.Requests) log = server.Log.ToList();
            var cancelAnswered = log.FindIndex(e => e.StartsWith("out ") && IsCancel(e.Substring(4)));
            var lateArrived = log.FindIndex(e => e.StartsWith("in ") && IsResults(e.Substring(3)));
            Assert.True(cancelAnswered >= 0 && lateArrived > cancelAnswered, string.Join(" | ", log));   // M1: cancel first
            Assert.Null(router.SessionId);                                      // the late reply is never read
            Assert.DoesNotContain("Not for the pane", r.Reply ?? "");
            Assert.Empty(streamed);
        }

        [Fact]
        public async Task Router_ChatSwitchAsTheTurnEndsOnAQuestion_NeverParksIt()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("ask", new { seq = 1, request_id = "req_q", question = "Aras mana?", multi = false, purpose = "clarify" })))
                .On(IsCancel, () => new HttpResponseMessage(HttpStatusCode.OK))
                .On(IsStart, Sse(Frame("reply_delta", new { seq = 1, text = "Chat B." })
                                 + Frame("turn_end", new { seq = 2, status = "done", session_id = "ses_B", receipt_ids = new string[0] })));
            var (router, ended) = RouterOn(server, new Tools());
            var switched = 0;
            // The window M2 is about: the drive has ended, its question is not applied yet.
            router.AfterDriveForTests = () => { if (Interlocked.Exchange(ref switched, 1) == 0) router.AdoptSession("ses_B"); };

            var old = await router.RouteAsync("naikkan siling", "ai-generated");
            await Task.Delay(50);

            Assert.Equal(1, switched);
            Assert.True(old.Superseded);
            Assert.Null(router.PendingAsk);                                   // never parked
            Assert.Equal(1, server.Count(x => IsCancel(x.Path)));             // the old chat's turn is ended
            Assert.Single(ended);
            Assert.False(router.IsBusy);

            var next = await router.RouteAsync("Aras 2", "ai-generated");     // typed in chat B

            Assert.Equal("Chat B.", next.Reply);
            Assert.Equal(0, server.Count(x => IsResults(x.Path)));            // not the old question's answer
            var start = server.Requests.Last(x => IsStart(x.Path));
            using var body = JsonDocument.Parse(start.Body);
            Assert.Equal("ses_B", body.RootElement.GetProperty("session_id").GetString());
        }

        [Fact]
        public async Task Router_PreviewOnlyThenStop_PostsNothing()
        {
            var (server, r, _, streamed) = await StopDuringABatch(() => new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview, Summary = "Would set Mark on 4 doors", Count = 4,
            });

            Assert.True(r.Interrupted);
            Assert.Equal(0, server.Count(x => IsResults(x.Path)));
            Assert.Equal(1, server.Count(x => IsCancel(x.Path)));
            Assert.Empty(streamed);
        }

        [Fact]
        public void Landed_IsAnOkOrPartialCommit_Only()
        {
            Envelope E(string phase, bool ok, string status) => new Envelope { Ok = ok, Status = status, Phase = phase };
            Assert.True(CopilotTurnClient.Landed(new[] { E("preview", true, "done"), E("commit", true, "done") }));
            Assert.True(CopilotTurnClient.Landed(new[] { E("commit", false, "partial") }));
            Assert.False(CopilotTurnClient.Landed(new[] { E("commit", false, "failed") }));       // rolled back
            Assert.False(CopilotTurnClient.Landed(new[] { E("read", true, "done"), E("preview", true, "done") }));
            Assert.False(CopilotTurnClient.Landed(new Envelope?[] { null }));
        }

        [Fact]
        public void Router_NewOrContinuedChat_StartsAFreshHandleStore_SharedByReadsAndWrites()
        {
            var handles = new SessionHandles(() => new BinaVibe.Copilot.Read.HandleStore());
            IHandleStore reads = handles, writes = handles;          // what ReadExecutor and WriteBatchRunner hold
            var client = new CopilotTurnClient(new HttpClient(new Server()), new CopilotClientOptions { BaseUrl = Base, Token = () => "t", ReleaseBuild = true });
            var router = new CopilotTurnRouter(new Tools(), client, new CopilotRouterHooks { OnSessionReset = handles.NewSession });

            var doors = reads.Put(new[] { new ElementRef(10, null), new ElementRef(20, 900) }, "doors");
            Assert.Equal(new[] { 10L, 20L }, writes.Get(doors)!.Select(r => r.Id));   // a read's handle resolves in a write

            router.ResetSession();                                   // "+" new chat
            Assert.Null(writes.Get(doors));

            var walls = reads.Put(new[] { new ElementRef(30, null) }, "walls");
            Assert.NotNull(writes.Get(walls));
            router.AdoptSession("ses_old");                          // continue another chat from History
            Assert.Null(reads.Get(walls));
        }

        [Fact]
        public void PaneOpenState_ForwardsEachChangeOnce_StartupHiddenIsForwarded()
        {
            var applied = new List<bool>();
            var state = new PaneOpenState(open => applied.Add(open));
            Assert.Null(state.Open);

            Assert.True(state.Report(false));        // first Idling: Revit started with the pane hidden
            Assert.False(state.Report(false));       // IsVisible / frame event repeat it
            Assert.True(state.Report(true));         // shown: SetPaneOpen(true) prewarms once
            Assert.False(state.Report(true));
            Assert.True(state.Report(false));

            Assert.Equal(new[] { false, true, false }, applied);

            var throwing = new PaneOpenState(_ => throw new InvalidOperationException("not attached"));
            Assert.True(throwing.Report(true));      // a failing apply never reaches the pane or Revit
            Assert.True(throwing.Open);
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

        private static SnapshotModel Doc(string title, string key) => new SnapshotModel
        {
            Doc = new SnapshotDoc { Kind = "project", Title = title, ProjectKey = key, VersionGuid = "v", SessionNonce = "n" },
        };

        private static object WriteRequest(long seq) => new
        {
            seq, request_id = "req_w", kind = "write",
            calls = new[] { new { call_id = "w1", idempotency_key = "kw1", tool = "set_parameters", args = new { param = "Mark", value = "A" } } },
        };

        [Fact]
        public async Task Router_DocSwitchMidTurn_ToolsStillGetTheTurnStartSnapshot()
        {
            // Re-review C1: the tools must compare the TURN's document with the
            // live one, so a switch mid-turn makes W2/W3 answer doc_changed. If
            // they got the live snapshot, both sides would be the new document.
            var server = new Server()
                .On(IsStart, Sse(Frame("tool_request", ReadRequest(1))))
                .On(IsResults, Sse(Frame("tool_request", ReadRequest(2, "req_r2"))))
                .On(IsResults, Sse(Frame("tool_request", WriteRequest(3))))
                .On(IsResults, Sse(Frame("turn_end", End(4))));
            var tools = new Tools { Live = Doc("Pejabat.rvt", "proj-a") };
            tools.AfterRead = () => tools.Live = Doc("Sekolah.rvt", "proj-b");   // the drafter switches documents
            var (router, _) = RouterOn(server, tools);

            await router.RouteAsync("find doors then set marks", "ai-generated");

            Assert.Equal(2, tools.ReadSnapshots.Count);
            Assert.All(tools.ReadSnapshots, s => Assert.Equal("proj-a", s.Doc!.ProjectKey));
            var write = Assert.Single(tools.WriteSnapshots);
            Assert.Equal("proj-a", write.Doc!.ProjectKey);
            // The results bodies carry the live snapshot, so the cloud sees the switch.
            var lastResults = server.Requests.Last(r => IsResults(r.Path));
            using var body = JsonDocument.Parse(lastResults.Body);
            Assert.Equal("proj-b", body.RootElement.GetProperty("snapshot").GetProperty("doc").GetProperty("project_key").GetString());
        }

        [Fact]
        public async Task Router_AnsweredAsk_KeepsTheTurnStartSnapshot()
        {
            var server = new Server()
                .On(IsStart, Sse(Frame("ask", new { seq = 1, request_id = "req_q", question = "Aras mana?", multi = false, purpose = "clarify" })))
                .On(IsResults, Sse(Frame("tool_request", ReadRequest(2))))
                .On(IsResults, Sse(Frame("turn_end", End(3))));
            var tools = new Tools { Live = Doc("Pejabat.rvt", "proj-a") };
            var (router, _) = RouterOn(server, tools);

            await router.RouteAsync("naikkan siling", "ai-generated");
            tools.Live = Doc("Sekolah.rvt", "proj-b");                               // switched while the question was open
            await router.RouteAsync("Aras 2", "ai-generated");

            Assert.Equal("proj-a", Assert.Single(tools.ReadSnapshots).Doc!.ProjectKey);
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
