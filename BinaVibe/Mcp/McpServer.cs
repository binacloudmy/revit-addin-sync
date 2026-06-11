// McpServer — embedded HTTP listener that exposes Revit data + actions
// to the bina-ai backend at `POST /mcp/tools/{name}`.
//
// Matches the protocol the backend's `app.agents.vibe.mcp_client.call`
// speaks: plain HTTPS POST, JSON args in body, JSON result back.
//
// Threading:
//   - HttpListener runs an accept loop on a background thread.
//   - Each request hands off to a thread-pool task that queues an
//     McpJob and waits up to 30s for the Revit thread to fulfil it.
//   - McpExternalEventHandler drains the queue on Revit's main thread.

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp
{
    public sealed class McpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly McpExternalEventHandler _handler;
        private readonly ExternalEvent _externalEvent;
        private readonly CancellationTokenSource _cts = new();
        // A cold large model's first build (Revit open + first-transaction
        // regen) can run ~150s. A short cap reported "failed" while Revit was
        // still drawing the element (false failure + duplicate risk), so wait
        // generously and report the truth. Override via BINA_VIBE_JOB_MAX_WAIT.
        private readonly TimeSpan _jobTimeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("BINA_VIBE_JOB_MAX_WAIT"),
                out var _jw) && _jw > 0 ? _jw : 600);

        public int Port { get; }
        public string Prefix => $"http://localhost:{Port}/";

        public McpServer(int port = 8080)
        {
            Port = port;
            _listener.Prefixes.Add(Prefix);
            _handler = new McpExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            _handler.Event = _externalEvent;  // lets the handler re-raise for the next queued job
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
                        args = JsonDocument.Parse(body).RootElement.Clone();
                    }
                    catch (JsonException je)
                    {
                        await WriteJson(ctx, 400, new { error = $"invalid JSON: {je.Message}" });
                        return;
                    }
                }

                var job = new McpJob { Tool = toolName, Args = args };
                job.TEnqueued = System.Diagnostics.Stopwatch.GetTimestamp();   // t0
                _handler.Pending.Enqueue(job);
                _externalEvent.Raise();

                if (!job.Completed.Wait(_jobTimeout))
                {
                    await WriteJson(ctx, 504, new { error = $"tool {toolName} timed out after {_jobTimeout.TotalSeconds}s" });
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
