// McpTunnelClient — production-shape transport (PRD §6.5 / §10.11).
//
// Instead of the addin opening an inbound HTTP listener (firewall-hostile,
// requires per-customer ngrok), the addin dials OUT over WSS to bina-ai
// at wss://.../revit-copilot/mcp/tunnel?tenant_id=...&session_id=...&token=....
//
// bina-ai keeps the socket open per tenant+session and sends tool-call
// frames down. The client reads frames, queues each as an McpJob to the
// existing IExternalEventHandler, executes on Revit's main thread, and
// sends the result frame back up.
//
// Coexists with McpServer (HttpListener) — flip via
// `BINA_VIBE_MCP_TRANSPORT` env (`http`, `wss`, `both`, default `http`).
// Default stays `http` until WSS hardening is complete.
//
// Wire format mirrors `app/agents/revit/copilot/tunnel_manager.py` in bina-ai:
//   Server → Client:  {"id":"...","method":"tool_call","params":{"tool":"...","args":{...}}}
//   Client → Server:  {"id":"...","result":{...}} OR {"id":"...","error":{"message":"..."}}
//   Heartbeat:        {"method":"heartbeat","params":{"ts":...}}
//   Ready:            {"method":"ready","params":{"tools":[...]}}

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Mcp
{
    public sealed class McpTunnelClient : IDisposable
    {
        private readonly McpExternalEventHandler _handler;
        private readonly ExternalEvent _externalEvent;
        private readonly McpIdempotencyCache _idem = new();
        private readonly Uri _serverUri;
        private readonly string _token;
        private readonly string _tenantId;
        private readonly string _sessionId;
        private readonly string? _userId;
        private readonly CancellationTokenSource _cts = new();
        private ClientWebSocket? _ws;
        private Task? _readLoop;
        private int _reconnectAttempt;

        // Honest-progress ceiling. A cold large model can take ~150s for its
        // first build (Revit finishing the open + first-transaction regen),
        // far past the old 60s cap which reported "timed out" while Revit was
        // still drawing the element (false failure + duplicate risk). Wait
        // generously and report the TRUTH; the backend is already patient
        // (VIBE_WSS_MAX_WAIT, default 300s). Override via BINA_VIBE_JOB_MAX_WAIT.
        private static readonly TimeSpan JobMaxWait = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("BINA_VIBE_JOB_MAX_WAIT"),
                out var _jw) && _jw > 0 ? _jw : 600);

        public McpTunnelClient(
            string baseUrl,
            string tenantId,
            string sessionId,
            string token,
            string? userId = null)
        {
            // Translate https:// → wss:// and append the WS endpoint.
            var scheme = baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "ws://" : "wss://";
            var rest = baseUrl.TrimEnd('/');
            if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(7);
            else if (rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(8);

            var qs = $"?tenant_id={Uri.EscapeDataString(tenantId)}"
                   + $"&session_id={Uri.EscapeDataString(sessionId)}"
                   + (userId != null ? $"&user_id={Uri.EscapeDataString(userId)}" : "")
                   + $"&token={Uri.EscapeDataString(token)}";
            _serverUri = new Uri($"{scheme}{rest}/revit-copilot/mcp/tunnel{qs}");
            _tenantId = tenantId;
            _sessionId = sessionId;
            _token = token;
            _userId = userId;

            _handler = new McpExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            _handler.Event = _externalEvent;  // lets the handler re-raise for the next queued job (no head-of-line block)
        }

        public void Start()
        {
            _readLoop = Task.Run(() => RunWithReconnect(_cts.Token));
        }

        private async Task RunWithReconnect(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    _ws = ws;
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                    await ws.ConnectAsync(_serverUri, ct).ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine($"[BinaVibe] WSS connected: {_serverUri}");
                    _reconnectAttempt = 0;

                    await SendReadyAsync(ws, ct).ConfigureAwait(false);
                    var heartbeat = StartHeartbeat(ws, ct);
                    await ReadLoop(ws, ct).ConfigureAwait(false);
                    await heartbeat.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BinaVibe] WSS errored: {ex.Message}");
                }
                finally
                {
                    _ws = null;
                }

                if (ct.IsCancellationRequested) return;
                // Exponential backoff (1, 2, 4, 8, 16, capped 30s).
                _reconnectAttempt = Math.Min(_reconnectAttempt + 1, 5);
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _reconnectAttempt)));
                System.Diagnostics.Debug.WriteLine($"[BinaVibe] WSS reconnect in {delay.TotalSeconds}s");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch { return; }
            }
        }

        private static async Task SendReadyAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var ready = new
            {
                method = "ready",
                @params = new
                {
                    // GENERATED from ToolRegistry (scripts/gen-tool-manifest.py) —
                    // the hand-written list this replaced was dead data nothing
                    // on the backend read (spec §8.2).
                    tools = Tools.InstalledToolManifest.Names,
                    protocol_version = Tools.InstalledToolManifest.ProtocolVersion,
                    manifest_version = Tools.InstalledToolManifest.Version,
                    addin_version = typeof(McpTunnelClient).Assembly.GetName().Version?.ToString() ?? "0.0",
                },
            };
            await SendJson(ws, ready, ct).ConfigureAwait(false);
        }

        private static Task StartHeartbeat(ClientWebSocket ws, CancellationToken ct) =>
            Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                        var hb = new { method = "heartbeat", @params = new { ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() } };
                        await SendJson(ws, hb, ct).ConfigureAwait(false);
                    }
                }
                catch { /* loop closes on cancel / socket close */ }
            }, ct);

        private async Task ReadLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[16 * 1024];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", ct).ConfigureAwait(false);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                }
                while (!res.EndOfMessage);

                var raw = sb.ToString();
                JsonDocument doc;
                try { doc = JsonDocument.Parse(raw); }
                catch { System.Diagnostics.Debug.WriteLine($"[BinaVibe] dropped non-JSON frame: {raw}"); continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("method", out var method) || method.GetString() != "tool_call") continue;
                    if (!root.TryGetProperty("id", out var idEl)) continue;
                    if (!root.TryGetProperty("params", out var paramsEl)) continue;
                    var id = idEl.GetString() ?? "";
                    var tool = paramsEl.TryGetProperty("tool", out var tEl) ? tEl.GetString() ?? "" : "";
                    var args = paramsEl.TryGetProperty("args", out var aEl)
                        ? aEl.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    var idemKey = paramsEl.TryGetProperty("idempotency_key", out var kEl)
                        ? kEl.GetString() ?? "" : "";

                    var job = new McpJob { Tool = tool, Args = args, IdempotencyKey = idemKey };
                    job.TEnqueued = System.Diagnostics.Stopwatch.GetTimestamp();   // t0

                    // Dedup: if this key was already handled (a retry after a
                    // false-timeout), reuse the ORIGINAL job — await its result
                    // instead of executing the write a second time (which would
                    // duplicate the element). Only enqueue when this job is new.
                    var canonical = _idem.GetOrAdd(idemKey, job);
                    if (ReferenceEquals(canonical, job))
                    {
                        _handler.Pending.Enqueue(job);
                        _externalEvent.Raise();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[BinaVibe] idempotent hit key={idemKey} tool={tool} — awaiting original, not re-executing");
                    }

                    // Tell the backend/pane the job has started so it can show
                    // honest "applying…" progress instead of a frozen wait.
                    // Informational only — does not complete the call.
                    await SendJson(ws, new { id, status = "running" }, ct).ConfigureAwait(false);

                    // Drive the in-process Copilot pane directly (no round-trip):
                    // show "applying <tool>…" while Revit works. Null-safe — the
                    // pane may not be open. Cleared in RespondAsync on completion.
                    try { RevitWebAppSync.App.CopilotPaneHost?.Panel?.ViewModel?.SetToolActivity(tool); }
                    catch { /* pane indicator is best-effort */ }

                    // Wait off the WS-read thread so we can keep reading
                    // additional frames in parallel. Wait on `canonical` so a
                    // deduped retry resolves from the original job's result.
                    _ = Task.Run(async () => await RespondAsync(ws, id, canonical, ct).ConfigureAwait(false));
                }
            }
        }

        private static async Task RespondAsync(ClientWebSocket ws, string id, McpJob job, CancellationToken ct)
        {
            // Wait for the Revit thread to COMPLETE, however long it honestly
            // takes (JobMaxWait, default 600s). The first build on a cold large
            // model can run ~150s (Revit open + first regen); the old 60s cap
            // reported "timed out" while Revit was still drawing the element —
            // a false failure that the user saw as "failed but it's there".
            try
            {
                var completed = job.Completed.Wait(JobMaxWait);
                if (!completed)
                {
                    // Genuine ceiling hit — be honest, and do NOT imply a clean
                    // failure (Revit may still be applying; a retry could duplicate).
                    await SendJson(ws, new { id, error = new { message =
                        $"Revit did not finish within {JobMaxWait.TotalSeconds:F0}s — it may still be applying; not retried to avoid duplicates." } },
                        ct).ConfigureAwait(false);
                    return;
                }

                if (job.Error != null)
                {
                    await SendJson(ws, new { id, error = new { message = job.Error } }, ct).ConfigureAwait(false);
                }
                else
                {
                    await SendJson(ws, new { id, result = job.Result ?? new Dictionary<string, object?>() }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BinaVibe] failed to send tool result: {ex.Message}");
            }
            finally
            {
                // Always clear the pane indicator, whatever the outcome.
                try { RevitWebAppSync.App.CopilotPaneHost?.Panel?.ViewModel?.ClearToolActivity(); }
                catch { /* best-effort */ }
            }
        }

        private static async Task SendJson(ClientWebSocket ws, object payload, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                if (_ws?.State == WebSocketState.Open)
                {
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "addin shutting down", CancellationToken.None)
                        .Wait(TimeSpan.FromSeconds(2));
                }
                _ws?.Dispose();
            }
            catch { }
        }
    }
}
