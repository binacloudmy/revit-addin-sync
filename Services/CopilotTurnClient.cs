#nullable enable
// Protocol-3 turn client (lean-harness spec §7.1, §9, §13, §14; W5 Task 2).
// Revit-free: HTTP and SSE only. The pane's router supplies an
// ICopilotTurnHandler that runs tool requests on Revit's thread through
// CopilotToolHost; this file never touches Revit.
//
//   POST /copilot/turns                      StartTurnAsync  (binds the turn)
//   POST /copilot/turns/{id}/results         after tool_request (automatic) / AnswerAsync
//   GET  /copilot/turns/{id}/events?after=N  ReplayAsync, and automatically when a stream drops
//   POST /copilot/turns/{id}/cancel          CancelAsync     (unbinds the turn)
//
// What this client guarantees, whatever the cloud sends:
//   - Turn binding. A tool_request runs only while its turn is the one the pane
//     started and has not ended or been cancelled. Anything else is Refused and
//     nothing is posted for it.
//   - Never twice. Frames are de-duplicated by seq; a tool_request whose
//     request_id already ran re-posts the cached results instead of running.
//   - Delivery. A results POST is retried 3 times with backoff, byte-identical
//     (idempotent by request_id). A dropped stream resumes with events?after=seq.
//   - Budgets (TurnBudget): 60 calls, 2 MB returned, 8 images per turn, compiled
//     in. The cloud may narrow them (Narrow), never raise them.
//   - Transport. The token goes in the Authorization header, never a URL.
//     Release builds talk only to https hosts under binacloud.ai (CopilotHostPolicy).
//   - Version gate. 426 is UpdateRequired; the pane runs the update flow.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.Services
{
    /// <summary>What the pane does with a turn's events. Called on a thread-pool
    /// thread; implementations marshal to the UI themselves.</summary>
    public interface ICopilotTurnHandler
    {
        void OnProgress(ProgressDto progress);
        void OnReplyDelta(ReplyDeltaDto delta);
        void OnError(StreamErrorDto error);

        /// <summary>Runs one tool_request (reads, or a write batch with its confirm
        /// card). One envelope per call, in call order. Only called for the bound turn.</summary>
        Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct);

        /// <summary>The snapshot sent with a results request.</summary>
        SnapshotModel CurrentSnapshot();
    }

    public enum TurnOutcomeKind
    {
        /// <summary>turn_end arrived (its Status says how it went).</summary>
        Ended,
        /// <summary>The turn is parked on an ask; answer it with AnswerAsync.</summary>
        Ask,
        /// <summary>A frame for a turn the pane does not hold; nothing ran.</summary>
        Refused,
        /// <summary>426: this add-in speaks an older protocol.</summary>
        UpdateRequired,
        /// <summary>409: a turn is already active for this session.</summary>
        TurnActive,
        /// <summary>401/403: sign in again.</summary>
        Unauthorized,
        Failed,
    }

    public sealed class TurnOutcome
    {
        public TurnOutcomeKind Kind;
        public TurnEndDto? End;
        public AskDto? Ask;
        /// <summary>The last SSE error event of the turn, if any.</summary>
        public StreamErrorDto? Error;
        /// <summary>Drafter-safe reason for Failed/Refused (never a raw socket text).</summary>
        public string? Message;
        public int? HttpStatus;
        /// <summary>A tool request ran but its results never reached the cloud.</summary>
        public bool ResultsUndelivered;
    }

    public sealed class CopilotClientOptions
    {
        public string BaseUrl = "";

        /// <summary>Bearer token, read per request (it may be refreshed mid-turn).</summary>
        public Func<string?> Token = () => null;

        /// <summary>Release rules for the host allowlist. Debug builds may use any host.</summary>
        public bool ReleaseBuild =
#if DEBUG
            false;
#else
            true;
#endif

        /// <summary>Retries after the first results POST (spec: ×3).</summary>
        public int ResultRetries = 3;

        /// <summary>events?after=seq resumes per stream before giving up.</summary>
        public int MaxReplays = 3;

        public Func<int, TimeSpan> Backoff = attempt => TimeSpan.FromMilliseconds(500 * (1 << Math.Min(attempt, 5)));

        public Func<TimeSpan, CancellationToken, Task> Delay = (d, ct) => Task.Delay(d, ct);

        /// <summary>A stream with no frame for this long counts as dropped.</summary>
        public TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(120);

        /// <summary>A fresh budget per turn. Tests narrow it.</summary>
        public Func<TurnBudget> Budget = () => new TurnBudget();

        /// <summary>Extra headers (fleet identity); never the token.</summary>
        public Action<HttpRequestMessage>? Decorate;
    }

    /// <summary>Compiled-in host allowlist (spec §7.1, §13): release builds accept
    /// only https to binacloud.ai or one of its subdomains. config.json cannot
    /// point the copilot anywhere else in a release build.</summary>
    public static class CopilotHostPolicy
    {
        public const string AllowedDomain = "binacloud.ai";

        public static bool IsAllowed(string? baseUrl, bool releaseBuild)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)
                || !Uri.TryCreate(baseUrl!.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return false;
            if (!releaseBuild) return true;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            return host == AllowedDomain || host.EndsWith("." + AllowedDomain, StringComparison.Ordinal);
        }
    }

    /// <summary>Per-turn limits, compiled in and enforced here (spec §7.1). The
    /// cloud can only narrow them. A refused or over-budget READ is replaced by a
    /// not_allowed failure (it changed nothing). A WRITE is never rewritten as a
    /// failure: it may have committed, so only its bulky data and image are dropped.</summary>
    public sealed class TurnBudget
    {
        public const int MaxCalls = 60;
        public const long MaxBytes = 2L * 1024 * 1024;
        public const int MaxImages = 8;

        public int CallLimit { get; private set; } = MaxCalls;
        public long ByteLimit { get; private set; } = MaxBytes;
        public int ImageLimit { get; private set; } = MaxImages;

        public int CallsUsed { get; private set; }
        public long BytesUsed { get; private set; }
        public int ImagesUsed { get; private set; }

        /// <summary>Lower any limit; a value above the current one (or below zero) is ignored.</summary>
        public void Narrow(int? calls, long? bytes, int? images)
        {
            if (calls is int c && c >= 0 && c < CallLimit) CallLimit = c;
            if (bytes is long b && b >= 0 && b < ByteLimit) ByteLimit = b;
            if (images is int i && i >= 0 && i < ImageLimit) ImageLimit = i;
        }

        /// <summary>Which calls may run, in order. Admitted calls are counted now.
        /// Nothing more runs once the byte budget is spent.</summary>
        public bool[] Admit(IReadOnlyList<ToolCallDto> calls)
        {
            var admitted = new bool[calls.Count];
            for (var i = 0; i < calls.Count; i++)
            {
                if (CallsUsed >= CallLimit || BytesUsed >= ByteLimit) continue;
                admitted[i] = true;
                CallsUsed++;
            }
            return admitted;
        }

        /// <summary>The envelope to send for a call that ran, after the image and
        /// byte budgets, and counts what is returned. Images count against the
        /// image budget only; everything else against the byte budget.</summary>
        public Envelope Account(Envelope envelope, bool isWrite)
        {
            var env = envelope;
            if (env.Image != null && ImagesUsed >= ImageLimit)
                env = isWrite ? Strip(env, data: false) : Refusal("Image budget reached: " + ImageLimit + " images per turn.");

            var size = SizeOf(env);
            if (BytesUsed + size > ByteLimit)
            {
                env = isWrite
                    ? Strip(env, data: true)
                    : Refusal("Result over this turn's " + Mb(ByteLimit) + " budget. Narrow the query or use the handle.");
                size = SizeOf(env);
            }

            if (env.Image != null) ImagesUsed++;
            BytesUsed += size;
            return env;
        }

        /// <summary>The refusal for a call that did not run.</summary>
        public Envelope RefusedCall() =>
            Refusal(BytesUsed >= ByteLimit
                ? "Turn budget reached: " + Mb(ByteLimit) + " of results per turn. Nothing ran."
                : "Turn budget reached: " + CallLimit + " tool calls per turn. Nothing ran.");

        /// <summary>For the step list, e.g. "Tools 12/60 · 0.4/2.0 MB · images 1/8".</summary>
        public string Describe() =>
            "Tools " + CallsUsed + "/" + CallLimit + " · "
            + (BytesUsed / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/"
            + Mb(ByteLimit) + " · images " + ImagesUsed + "/" + ImageLimit;

        private static string Mb(long bytes) =>
            (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

        private static Envelope Refusal(string message) => EnvelopeFactory.Failed(ErrorCodes.NotAllowed, message);

        /// <summary>A copy without the image (and without data when asked); every
        /// truth field (status, phase, changes, verified, warnings) is kept.</summary>
        private static Envelope Strip(Envelope e, bool data) => new Envelope
        {
            Ok = e.Ok,
            Status = e.Status,
            Phase = e.Phase,
            Summary = e.Summary,
            Data = data ? null : e.Data,
            Count = e.Count,
            Handle = e.Handle,
            Searched = e.Searched,
            Changes = e.Changes,
            Verified = e.Verified,
            PlanHash = e.PlanHash,
            Highlight = e.Highlight,
            Warnings = e.Warnings,
            Dialogs = e.Dialogs,
            Error = e.Error,
            Next = e.Next,
            Image = null,
        };

        private static long SizeOf(Envelope e) =>
            Encoding.UTF8.GetByteCount(CopilotJson.Serialize(e.Image == null ? e : Strip(e, data: false)));
    }

    public sealed class CopilotTurnClient
    {
        private readonly HttpClient _http;
        private readonly CopilotClientOptions _opt;
        private readonly object _sync = new object();
        private TurnState? _active;

        public CopilotTurnClient(HttpClient http, CopilotClientOptions options)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _opt = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>The turn the pane started and that has not ended; null otherwise.</summary>
        public string? ActiveTurnId { get { lock (_sync) return _active?.TurnId; } }

        /// <summary>The running budget of the bound turn (for the step list).</summary>
        public TurnBudget? ActiveBudget { get { lock (_sync) return _active?.Budget; } }

        private sealed class TurnState
        {
            public TurnState(string turnId, TurnBudget budget) { TurnId = turnId; Budget = budget; }
            public readonly string TurnId;
            public readonly TurnBudget Budget;
            public long LastSeq;
            /// <summary>request_id → the exact results body already sent (or being sent).</summary>
            public readonly Dictionary<string, string> Sent = new Dictionary<string, string>();
            public StreamErrorDto? LastError;
        }

        private bool IsBound(TurnState st)
        {
            lock (_sync) return ReferenceEquals(_active, st);
        }

        private void Unbind(TurnState st)
        {
            lock (_sync) if (ReferenceEquals(_active, st)) _active = null;
        }

        private TurnState? Bound(string turnId)
        {
            lock (_sync) return _active != null && _active.TurnId == turnId ? _active : null;
        }

        // ------------------------------------------------------------------
        // Public operations
        // ------------------------------------------------------------------

        /// <summary>POST /copilot/turns and drive the turn until it ends or asks.
        /// Binds the turn first; any previously bound turn stops being executable.</summary>
        public Task<TurnOutcome> StartTurnAsync(TurnRequestDto request, ICopilotTurnHandler handler, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var st = new TurnState(request.TurnId, _opt.Budget());
            lock (_sync) _active = st;
            request.Protocol = Protocol.Version;
            var body = CopilotJson.Serialize(request);
            return DriveAsync(st, c => SendAsync(HttpMethod.Post, Protocol.TurnsPath, body, stream: true, c), handler, ct);
        }

        /// <summary>Answer the bound turn's ask (POST .../results with answer).</summary>
        public Task<TurnOutcome> AnswerAsync(string turnId, string requestId, AnswerDto answer, ICopilotTurnHandler handler, CancellationToken ct)
        {
            var st = Bound(turnId);
            if (st == null) return Task.FromResult(Refused("That question belongs to a turn that is no longer active."));
            var body = CopilotJson.Serialize(new ResultsRequestDto
            {
                RequestId = requestId,
                Answer = answer,
                Snapshot = SafeSnapshot(handler),
            });
            lock (st.Sent) st.Sent[requestId] = body;
            return DriveAsync(st, c => PostResultsAsync(st.TurnId, body, c), handler, ct);
        }

        /// <summary>Resume the bound turn's stream from a seq (GET .../events?after=).</summary>
        public Task<TurnOutcome> ReplayAsync(string turnId, long afterSeq, ICopilotTurnHandler handler, CancellationToken ct)
        {
            var st = Bound(turnId);
            if (st == null) return Task.FromResult(Refused("That turn is not the one this pane started."));
            if (afterSeq > st.LastSeq) st.LastSeq = afterSeq;
            return DriveAsync(st, c => SendAsync(HttpMethod.Get, Protocol.EventsPath(turnId, st.LastSeq), null, stream: true, c), handler, ct);
        }

        /// <summary>Unbind the turn at once (nothing more of it runs), then tell the
        /// cloud. Best-effort on the network side.</summary>
        public async Task CancelAsync(string turnId, CancellationToken ct)
        {
            lock (_sync) if (_active != null && _active.TurnId == turnId) _active = null;
            if (!CopilotHostPolicy.IsAllowed(_opt.BaseUrl, _opt.ReleaseBuild)) return;
            try
            {
                using var resp = await SendAsync(HttpMethod.Post, Protocol.CancelPath(turnId), "{}", stream: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !ct.IsCancellationRequested)
            {
                // The cloud also ends idle turns; the add-in has already stopped executing it.
            }
        }

        // ------------------------------------------------------------------
        // The loop
        // ------------------------------------------------------------------

        private async Task<TurnOutcome> DriveAsync(
            TurnState st, Func<CancellationToken, Task<HttpResponseMessage>> open, ICopilotTurnHandler handler, CancellationToken ct)
        {
            if (!CopilotHostPolicy.IsAllowed(_opt.BaseUrl, _opt.ReleaseBuild))
            {
                Unbind(st);
                return Failed("This add-in build only talks to BINA's own servers; the configured copilot address is not one of them.");
            }

            HttpResponseMessage? resp;
            try
            {
                resp = await open(ct).ConfigureAwait(false);
            }
            catch (ResultsUndeliveredException)
            {
                return new TurnOutcome { Kind = TurnOutcomeKind.Failed, ResultsUndelivered = true, Error = st.LastError,
                    Message = "BINA could not be reached to report what ran in Revit. Revit's own record (Undo list) shows what changed." };
            }
            catch (Exception ex) when (IsTransport(ex, ct))
            {
                if (st.LastSeq == 0)
                {
                    Unbind(st);
                    return Failed("BINA could not be reached. Check the connection and send again.");
                }
                resp = null;   // the turn exists: resume from what we saw
            }

            var replays = 0;
            while (true)
            {
                if (resp != null)
                {
                    var status = (int)resp.StatusCode;
                    if (status != 200)
                    {
                        var mapped = await MapStatusAsync(st, resp).ConfigureAwait(false);
                        resp.Dispose();
                        return mapped;
                    }

                    StreamResult result;
                    try
                    {
                        result = await ReadStreamAsync(st, resp, handler, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        resp.Dispose();
                    }

                    if (result.Outcome != null) return result.Outcome;
                    if (result.Next != null) { resp = result.Next; replays = 0; continue; }
                }

                // The stream ended without a terminal frame: resume after the last seq.
                if (++replays > _opt.MaxReplays)
                    return Failed("The connection to BINA dropped and could not be resumed. Send the request again.");
                if (!IsBound(st)) return Refused("The turn was cancelled.");
                await _opt.Delay(_opt.Backoff(replays - 1), ct).ConfigureAwait(false);
                try
                {
                    resp = await SendAsync(HttpMethod.Get, Protocol.EventsPath(st.TurnId, st.LastSeq), null, stream: true, ct).ConfigureAwait(false);
                    if (IsRetryableStatus((int)resp.StatusCode)) { resp.Dispose(); resp = null; }
                }
                catch (Exception ex) when (IsTransport(ex, ct))
                {
                    resp = null;
                }
            }
        }

        private sealed class StreamResult
        {
            public TurnOutcome? Outcome;
            public HttpResponseMessage? Next;
        }

        private async Task<StreamResult> ReadStreamAsync(TurnState st, HttpResponseMessage resp, ICopilotTurnHandler handler, CancellationToken ct)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(_opt.StreamIdleTimeout);
            Stream stream;
            try
            {
                stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransport(ex, ct))
            {
                return new StreamResult();
            }

            // A blocked read cannot observe a token on .NET Framework: closing the
            // stream is what unblocks it.
            using var unblock = idle.Token.Register(() => { try { stream.Dispose(); } catch { } });
            try
            {
                await foreach (var ev in CopilotSse.Parse(stream, idle.Token).ConfigureAwait(false))
                {
                    idle.CancelAfter(_opt.StreamIdleTimeout);
                    if (ev.Seq is long seq)
                    {
                        if (seq <= st.LastSeq) continue;   // replayed or duplicated frame
                        st.LastSeq = seq;
                    }

                    switch (ev.Type)
                    {
                        case SseEventTypes.Progress:
                            var p = Read<ProgressDto>(ev);
                            if (p != null) Safe(() => handler.OnProgress(p));
                            break;
                        case SseEventTypes.ReplyDelta:
                            var d = Read<ReplyDeltaDto>(ev);
                            if (d != null) Safe(() => handler.OnReplyDelta(d));
                            break;
                        case SseEventTypes.Error:
                            var e = Read<StreamErrorDto>(ev);
                            if (e != null) { st.LastError = e; Safe(() => handler.OnError(e)); }
                            break;
                        case SseEventTypes.Ask:
                            if (!IsBound(st)) return new StreamResult { Outcome = Refused("The turn was cancelled.") };
                            var ask = Read<AskDto>(ev);
                            if (ask == null) return new StreamResult { Outcome = Failed("BINA sent a question the add-in could not read.") };
                            return new StreamResult { Outcome = new TurnOutcome { Kind = TurnOutcomeKind.Ask, Ask = ask, Error = st.LastError } };
                        case SseEventTypes.TurnEnd:
                            Unbind(st);
                            return new StreamResult
                            {
                                Outcome = new TurnOutcome
                                {
                                    Kind = TurnOutcomeKind.Ended,
                                    End = Read<TurnEndDto>(ev) ?? new TurnEndDto { Status = TurnEndStatus.Failed },
                                    Error = st.LastError,
                                },
                            };
                        case SseEventTypes.ToolRequest:
                            if (!IsBound(st)) return new StreamResult { Outcome = Refused("A tool request arrived for a turn this pane is not running; it was not executed.") };
                            var req = Read<ToolRequestDto>(ev);
                            if (req == null || string.IsNullOrEmpty(req.RequestId))
                                return new StreamResult { Outcome = Failed("BINA sent a tool request the add-in could not read; nothing ran.") };
                            var next = await RunAndPostAsync(st, req, handler, ct).ConfigureAwait(false);
                            return next;
                        default:
                            break;   // unknown frame types are ignored
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // idle timeout: treated as a drop
            }
            catch (Exception ex) when (IsTransport(ex, ct) || ex is ObjectDisposedException)
            {
                // the connection dropped mid-stream
            }
            ct.ThrowIfCancellationRequested();
            return new StreamResult();
        }

        private async Task<StreamResult> RunAndPostAsync(TurnState st, ToolRequestDto req, ICopilotTurnHandler handler, CancellationToken ct)
        {
            string? body;
            lock (st.Sent) st.Sent.TryGetValue(req.RequestId, out body);

            if (body == null)
            {
                var calls = req.Calls ?? new List<ToolCallDto>();
                var isWrite = req.Kind == ToolRequestKinds.Write;
                var admitted = st.Budget.Admit(calls);
                var envelopes = new Envelope[calls.Count];
                var run = new List<ToolCallDto>();
                var slots = new List<int>();
                for (var i = 0; i < calls.Count; i++)
                {
                    if (admitted[i]) { run.Add(calls[i]); slots.Add(i); }
                    else envelopes[i] = st.Budget.RefusedCall();
                }

                if (run.Count > 0)
                {
                    var subset = run.Count == calls.Count ? req : new ToolRequestDto
                    {
                        Seq = req.Seq, RequestId = req.RequestId, Kind = req.Kind, Calls = run, Description = req.Description,
                    };
                    var ran = await handler.RunToolsAsync(st.TurnId, subset, ct).ConfigureAwait(false) ?? Array.Empty<Envelope>();
                    for (var j = 0; j < slots.Count; j++)
                    {
                        var env = j < ran.Length && ran[j] != null
                            ? ran[j]
                            : EnvelopeFactory.Failed(ErrorCodes.RuntimeError, "The add-in returned no result for this call.");
                        envelopes[slots[j]] = st.Budget.Account(env, isWrite);
                    }
                }

                body = CopilotJson.Serialize(new ResultsRequestDto
                {
                    RequestId = req.RequestId,
                    Results = calls.Select((c, i) => new BinaVibe.Copilot.Contracts.ToolResultDto { CallId = c.CallId, Envelope = envelopes[i] }).ToList(),
                    Snapshot = SafeSnapshot(handler),
                });
                lock (st.Sent) st.Sent[req.RequestId] = body;
            }

            try
            {
                return new StreamResult { Next = await PostResultsAsync(st.TurnId, body, ct).ConfigureAwait(false) };
            }
            catch (ResultsUndeliveredException)
            {
                return new StreamResult
                {
                    Outcome = new TurnOutcome
                    {
                        Kind = TurnOutcomeKind.Failed,
                        ResultsUndelivered = true,
                        Error = st.LastError,
                        Message = "BINA could not be reached to report what ran in Revit. Revit's own record (Undo list) shows what changed.",
                    },
                };
            }
        }

        private sealed class ResultsUndeliveredException : Exception { }

        /// <summary>POST the same body until it lands: 1 try + ResultRetries retries
        /// on transport failures, 429 and 5xx. Other statuses are returned as-is.</summary>
        private async Task<HttpResponseMessage> PostResultsAsync(string turnId, string body, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var resp = await SendAsync(HttpMethod.Post, Protocol.ResultsPath(turnId), body, stream: true, ct).ConfigureAwait(false);
                    if (!IsRetryableStatus((int)resp.StatusCode)) return resp;
                    resp.Dispose();
                }
                catch (Exception ex) when (IsTransport(ex, ct))
                {
                }
                if (attempt >= _opt.ResultRetries) throw new ResultsUndeliveredException();
                await _opt.Delay(_opt.Backoff(attempt), ct).ConfigureAwait(false);
            }
        }

        // ------------------------------------------------------------------
        // HTTP
        // ------------------------------------------------------------------

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, bool stream, CancellationToken ct)
        {
            var req = new HttpRequestMessage(method, _opt.BaseUrl.TrimEnd('/') + path);
            if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (stream) req.Headers.Accept.ParseAdd("text/event-stream");
            var token = _opt.Token();
            if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try { _opt.Decorate?.Invoke(req); } catch { /* identity headers are best-effort */ }
            try
            {
                return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            finally
            {
                req.Dispose();
            }
        }

        private async Task<TurnOutcome> MapStatusAsync(TurnState st, HttpResponseMessage resp)
        {
            var status = (int)resp.StatusCode;
            switch (status)
            {
                case 426:
                    Unbind(st);
                    return new TurnOutcome { Kind = TurnOutcomeKind.UpdateRequired, HttpStatus = status,
                        Message = "BINA's copilot needs a newer add-in. Update BINA Sync to keep using it." };
                case 409:
                    Unbind(st);
                    return new TurnOutcome { Kind = TurnOutcomeKind.TurnActive, HttpStatus = status,
                        Message = "Another request is still running in this chat. Wait for it or press Stop." };
                case 401:
                case 403:
                    Unbind(st);
                    return new TurnOutcome { Kind = TurnOutcomeKind.Unauthorized, HttpStatus = status,
                        Message = "Your BINA AI sign-in has expired. Sign in again to continue." };
                default:
                    string detail = "";
                    try { detail = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)) ?? ""; } catch { }
                    Unbind(st);
                    return new TurnOutcome { Kind = TurnOutcomeKind.Failed, HttpStatus = status, Error = st.LastError,
                        Message = "BINA answered HTTP " + status + "." + (detail.Length > 0 && detail.Length <= 200 ? " " + detail : "") };
            }
        }

        private static bool IsRetryableStatus(int status) => status == 429 || (status >= 500 && status <= 599);

        private static bool IsTransport(Exception ex, CancellationToken ct) =>
            ex is HttpRequestException || ex is IOException || ex is WebException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested);   // HttpClient timeout

        private static T? Read<T>(SseEvent ev) where T : class
        {
            try { return CopilotJson.Deserialize<T>(ev.Data); }
            catch (System.Text.Json.JsonException) { return null; }
        }

        private static SnapshotModel SafeSnapshot(ICopilotTurnHandler handler)
        {
            try { return handler.CurrentSnapshot() ?? new SnapshotModel { Stale = true }; }
            catch { return new SnapshotModel { Stale = true }; }
        }

        private static void Safe(Action a)
        {
            try { a(); } catch { /* a UI hiccup must never break the turn */ }
        }

        private static TurnOutcome Refused(string message) => new TurnOutcome { Kind = TurnOutcomeKind.Refused, Message = message };

        private static TurnOutcome Failed(string message) => new TurnOutcome { Kind = TurnOutcomeKind.Failed, Message = message };
    }
}
