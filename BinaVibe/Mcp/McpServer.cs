// McpServer — embedded HTTP listener that exposes Revit data + actions to the
// LOCAL BINA Copilot Engine at `POST /mcp/tools/{name}`.
//
// Engine architecture (docs/superpowers/specs/2026-07-08-copilot-engine-colocate-design.md):
// the agent loop runs as a local process next to Revit and calls this server
// synchronously over 127.0.0.1 — one POST per tool, the run never pauses.
//
// Wire format:
//   POST /mcp/tools/{name}
//   headers: X-Bina-Secret (required, must match BinaConfig.EngineSecret),
//            Idempotency-Key (optional, dedupes retries),
//            X-Bina-Mutate: 1 (the call writes to the model),
//            X-Bina-Always-Confirm: 1 (ask even in Auto mode),
//            X-Bina-Confirm-Wait-S (card wait in seconds, default 300)
//   body:    {"tool_call_id": "...", "args": { ... }}   (legacy bare-args tolerated)
//   200 + tool JSON, 401 bad/missing secret, 423 while a forced update is
//   pending, 504 on the request-wait cap.
//
// Confirmation gate (engine mode): a write marked with X-Bina-Mutate waits
// for the drafter's Ya/Tidak on the pane's card (EngineWriteGate) BEFORE it
// is queued for Revit. The wait is an await on this request's pool task, so
// nothing blocks the Revit UI thread, and the 50 s execution cap starts only
// after Ya. Anything but Ya answers 200 with the gate's result and the tool
// never runs. No X-Bina-Mutate means a read or an engine older than the
// gate: no card, as before.
//
// Threading (the earlier orphaned-queue bug is fixed here):
//   - HttpListener runs an accept loop on a background thread.
//   - Each request queues its McpJob via McpJobPump.Enqueue — the SAME queue
//     the Idling drainer services (with SetRaiseWithoutDelay) and the same 6s
//     modal/busy watchdog fast-fails. (The previous private McpExternalEventHandler
//     this class constructed was never drained by the pump, so every request hung
//     to the timeout — removed.)

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;

namespace BinaVibe.Mcp
{
    public sealed class McpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly McpIdempotencyCache _idempotency = new();
        private readonly string _secret;

        // How long a single tool request may wait for the Revit UI thread to
        // fulfil it. The pump's 6s idle watchdog fast-fails busy/modal cases
        // well before this; this is the hard ceiling for a genuinely slow tool.
        // Override via BINA_VIBE_JOB_MAX_WAIT.
        private readonly TimeSpan _requestWait = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("BINA_VIBE_JOB_MAX_WAIT"),
                out var _jw) && _jw > 0 ? _jw : 50);

        public int Port { get; }
        // Prefix stays "localhost" (NOT 127.0.0.1): on Windows a non-admin
        // process may register a localhost HttpListener prefix without a URL
        // ACL, but an explicit 127.0.0.1 prefix needs `netsh http add urlacl`
        // (elevation). http.sys matches on the Host header, so the engine must
        // dial http://localhost:{port} to match.
        public string Prefix => $"http://localhost:{Port}/";

        // secret: the shared loopback secret every /mcp/tools request must
        // present in X-Bina-Secret. Captured once here (not re-read per request)
        // — an interim Phase-1 value; changing it requires a restart.
        public McpServer(int port = 48820, string secret = "")
        {
            Port = port;
            _secret = secret ?? "";
            _listener.Prefixes.Add(Prefix);
        }

        public void Start()
        {
            _listener.Start();
            Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => HandleRequest(ctx));
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                if (path == "/mcp/health")
                {
                    await WriteJson(ctx, 200, new { ok = true });
                    return;
                }

                if (ctx.Request.HttpMethod != "POST" || !path.StartsWith("/mcp/tools/"))
                {
                    await WriteJson(ctx, 404, new { error = "not found", path });
                    return;
                }

                // Secret gate — reject before any parsing or execution.
                var presented = ctx.Request.Headers["X-Bina-Secret"];
                if (string.IsNullOrEmpty(_secret) ||
                    !string.Equals(presented, _secret, StringComparison.Ordinal))
                {
                    await WriteJson(ctx, 401, new { error = "bad or missing X-Bina-Secret" });
                    return;
                }

                // Forced update pending: no tool may touch the model. 423 Locked
                // so the engine surfaces the reason instead of retrying.
                if (RevitWebAppSync.Services.UpdateGate.IsBlocked)
                {
                    await WriteJson(ctx, 423, new { error = RevitWebAppSync.Services.UpdateGate.RefusalMessage });
                    return;
                }

                var toolName = path.Substring("/mcp/tools/".Length);
                if (string.IsNullOrEmpty(toolName))
                {
                    await WriteJson(ctx, 400, new { error = "missing tool name" });
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();

                JsonElement args;
                if (string.IsNullOrWhiteSpace(body))
                {
                    args = JsonDocument.Parse("{}").RootElement.Clone();
                }
                else
                {
                    try
                    {
                        var root = JsonDocument.Parse(body).RootElement;
                        // Engine wire: {"tool_call_id": "...", "args": {...}}.
                        // Legacy bare-args body (no "args" property) tolerated.
                        args = root.ValueKind == JsonValueKind.Object &&
                               root.TryGetProperty("args", out var a)
                            ? a.Clone()
                            : root.Clone();
                    }
                    catch (JsonException je)
                    {
                        await WriteJson(ctx, 400, new { error = $"invalid JSON: {je.Message}" });
                        return;
                    }
                }

                var idemKey = ctx.Request.Headers["Idempotency-Key"];
                var mutateHeader = ctx.Request.Headers[EngineWriteGate.MutateHeader];
                bool mutate = EngineWriteGate.HeaderFlag(mutateHeader);
                bool alwaysConfirm = EngineWriteGate.HeaderFlag(ctx.Request.Headers[EngineWriteGate.AlwaysConfirmHeader]);
                var confirmWait = EngineWriteGate.ConfirmWait(ctx.Request.Headers[EngineWriteGate.ConfirmWaitHeader]);
                if (mutateHeader == null) NoteUngated(toolName);
                var job = new McpJob { Tool = toolName, Args = args, IdempotencyKey = idemKey ?? "", Mutate = mutate };

                // Retry-safe dedup: a repeated Idempotency-Key returns the job
                // already in flight/completed — we only await it, never enqueue
                // a second execution (so a client retry can't double-mutate).
                var wait = _requestWait;
                var canonical = _idempotency.GetOrAdd(idemKey, job);
                if (ReferenceEquals(canonical, job))
                {
                    // New key. A write that needs a card waits for the click
                    // here, before it is queued, so the pump's 6 s watchdog
                    // and the cap below both start after Ya.
                    var verdict = WriteVerdict.Run;
                    if (EngineWriteGate.NeedsCard(mutate, alwaysConfirm, EngineWriteGate.IsAutoMode()))
                        verdict = await EngineWriteGate.AskAsync(toolName, args, alwaysConfirm, confirmWait).ConfigureAwait(false);
                    if (verdict == WriteVerdict.Run)
                        McpJobPump.Enqueue(job);   // run it (shared pump + watchdog)
                    else
                        job.SetResult(EngineWriteGate.ResultFor(verdict, confirmWait.TotalSeconds));
                }
                else
                {
                    job = canonical;               // duplicate: await the original
                    // The original may still be waiting on the card.
                    if (mutate) wait = confirmWait + _requestWait;
                }

                if (!job.Completed.Wait(wait))
                {
                    job.Abandoned = true;
                    await WriteJson(ctx, 504, new { error = $"tool {toolName} timed out after {wait.TotalSeconds}s" });
                    return;
                }

                if (job.Error != null)
                {
                    await WriteJson(ctx, 500, new { error = job.Error });
                    return;
                }

                await WriteJson(ctx, 200, job.Result ?? new System.Collections.Generic.Dictionary<string, object?>());
            }
            catch (Exception ex)
            {
                try { await WriteJson(ctx, 500, new { error = ex.Message }); } catch { }
            }
        }

        // Tool names seen without X-Bina-Mutate, logged once each. That is a
        // read, or an engine older than the confirmation gate (its writes
        // run with no card); the missing header alone cannot tell them apart.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ungated = new();

        private static void NoteUngated(string toolName)
        {
            if (_ungated.TryAdd(toolName, 0))
                System.Diagnostics.Debug.WriteLine(
                    $"[BINA] engine call without {EngineWriteGate.MutateHeader}: {toolName} (a read, or an engine older than the confirmation gate)");
        }

        private static async Task WriteJson(HttpListenerContext ctx, int status, object body)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = false });
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                if (_listener.IsListening) _listener.Stop();
                _listener.Close();
            }
            catch { }
        }
    }
}
