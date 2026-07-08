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
//            Idempotency-Key (optional, dedupes retries)
//   body:    {"tool_call_id": "...", "args": { ... }}   (legacy bare-args tolerated)
//   200 + tool JSON, 401 bad/missing secret, 504 on the request-wait cap.
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
                var job = new McpJob { Tool = toolName, Args = args, IdempotencyKey = idemKey ?? "" };

                // Retry-safe dedup: a repeated Idempotency-Key returns the job
                // already in flight/completed — we only await it, never enqueue
                // a second execution (so a client retry can't double-mutate).
                var canonical = _idempotency.GetOrAdd(idemKey, job);
                if (ReferenceEquals(canonical, job))
                    McpJobPump.Enqueue(job);       // new key: run it (shared pump + watchdog)
                else
                    job = canonical;               // duplicate: await the original

                if (!job.Completed.Wait(_requestWait))
                {
                    job.Abandoned = true;
                    await WriteJson(ctx, 504, new { error = $"tool {toolName} timed out after {_requestWait.TotalSeconds}s" });
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
