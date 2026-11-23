#nullable enable
// r25 audit (SHOULD): the backend answers a start with HTTP 409 and detail exactly "another_session_write" while another chat's
// write is still running. That is not "another request in this chat", so it gets its own words, in the chat's language, and it is
// not "fixed" by cancelling this chat's last turn. Any other 409 keeps the old text. Revit-free.

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
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R25AnotherChatWriteTests
    {
        private const string TurnA = "0b6f2a52-6a43-4c8e-9a51-3f2f1f6f8c11";
        private const string TurnB = "6c1e0f7d-2b4a-4f0e-8d0a-5b7d2e9c3a22";
        private const string En = "A change is still being applied in another BINA chat. Try again in a moment.";
        private const string Ms = "Satu perubahan masih sedang dibuat dalam chat BINA yang lain. Cuba lagi sebentar lagi.";

        private sealed class Cloud : HttpMessageHandler
        {
            public readonly List<string> Seen = new List<string>();
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _replies = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
            public Cloud Then(Func<HttpRequestMessage, HttpResponseMessage> reply) { _replies.Enqueue(reply); return this; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (Seen) Seen.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
                return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue()(request) : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
            }
        }

        private static Func<HttpRequestMessage, HttpResponseMessage> Json(HttpStatusCode code, string body) => _ =>
            new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static Func<HttpRequestMessage, HttpResponseMessage> AskStream() => _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(
                    "event: ask\ndata: " + JsonSerializer.Serialize(new { seq = 1, request_id = "req_q", question = "Aras mana?", multi = false, purpose = "clarify" }) + "\n\n"))),
            };

        private sealed class Handler : ICopilotTurnHandler
        {
            public void OnProgress(ProgressDto p) { }
            public void OnReplyDelta(ReplyDeltaDto d) { }
            public void OnError(StreamErrorDto e) { }
            public SnapshotModel CurrentSnapshot() => new SnapshotModel();
            public Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct) => Task.FromResult(new Envelope[0]);
        }

        private static CopilotTurnClient ClientOn(Cloud cloud) => new CopilotTurnClient(new HttpClient(cloud), new CopilotClientOptions
        {
            BaseUrl = "https://bina-ai-stg-onebistro.binacloud.ai", Token = () => "tok", ReleaseBuild = true, Delay = (_, __) => Task.CompletedTask,
        });

        [Fact]
        public async Task AnotherChatsWrite_HasItsOwnWords_AndDoesNotCancelThisChatsTurn()
        {
            var cloud = new Cloud()
                .Then(AskStream())
                .Then(Json(HttpStatusCode.OK, "{}"))                                              // the drafter stops turn A
                .Then(Json(HttpStatusCode.Conflict, "{\"detail\":\"another_session_write\"}"));
            var client = ClientOn(cloud);
            await client.StartTurnAsync(new TurnRequestDto { TurnId = TurnA, Text = "x" }, new Handler(), CancellationToken.None);
            await client.CancelAsync(TurnA, CancellationToken.None);

            var outcome = await client.StartTurnAsync(new TurnRequestDto { TurnId = TurnB, Text = "y" }, new Handler(), CancellationToken.None);

            Assert.Equal(TurnOutcomeKind.TurnActive, outcome.Kind);
            Assert.Equal(409, outcome.HttpStatus);
            Assert.Equal(En, outcome.Message);
            Assert.Equal(CopilotTurnClient.AnotherChatWriteMessage, outcome.Message);
            Assert.Single(cloud.Seen, s => s.EndsWith("/cancel"));                                // not re-cancelled
            Assert.Equal(2, cloud.Seen.Count(s => s == "POST /copilot/turns"));                   // not retried
        }

        [Theory]
        [InlineData("{\"detail\":\"a turn is already active for this session\"}")]
        [InlineData("")]
        [InlineData("not json")]
        public async Task AnyOther409_KeepsTheBusyChatText(string body)
        {
            var cloud = new Cloud().Then(Json(HttpStatusCode.Conflict, body));
            var outcome = await ClientOn(cloud).StartTurnAsync(new TurnRequestDto { TurnId = TurnB, Text = "y" }, new Handler(), CancellationToken.None);
            Assert.Equal(TurnOutcomeKind.TurnActive, outcome.Kind);
            Assert.Equal("Another request is still running in this chat. Wait for it or press Stop.", outcome.Message);
        }

        [Fact]
        public void TheCardSaysItInTheChatsLanguage()
        {
            Assert.Equal(En, FailureCards.TurnActiveBody(CopilotTurnClient.AnotherChatWriteMessage, malay: false));
            Assert.Equal(Ms, FailureCards.TurnActiveBody(CopilotTurnClient.AnotherChatWriteMessage, malay: true));
            const string busy = "Wait for it to finish or press Stop, then send again. Nothing was changed in the model.";
            Assert.Equal(busy, FailureCards.TurnActiveBody("Another request is still running in this chat. Wait for it or press Stop.", malay: false));
            Assert.Equal(busy, FailureCards.TurnActiveBody(null, malay: true));
        }

        [Fact]
        public void ThePanesTurnActiveCardUsesThoseWords()
        {
            var vm = File.ReadAllText(RevitWebAppSync.Tests.CodeTestSupport.RepoFile("UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("FailureCards.TurnActiveBody(rr.PaneNote, rr.Language == ReceiptLanguage.Ms)", vm);
        }
    }
}
