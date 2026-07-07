// SSE event → StreamChunk parser, split out from AIServiceStream.cs so it can
// be unit-tested without dragging in AIService (and its config/command/HTTP
// dependency closure). Pure, HTTP-free: only System.Text.Json + the AIResponse
// model. AIServiceStreamExtensions is a `partial` static class; the HTTP
// streaming method lives in AIServiceStream.cs.

using System;
using System.Text.Json;
using Newtonsoft.Json;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    public enum StreamChunkKind { Meta, Status, Tool, Reply, CodePartial, Done, Error, Unknown }

    public sealed class StreamChunk
    {
        public StreamChunkKind Kind { get; init; }
        public string RawData { get; init; } = "";
        public string Delta { get; init; } = "";      // for CodePartial
        public AIResponse Final { get; init; }         // for Done
        public string Error { get; init; }             // for Error

        // Status / Tool — a single human-readable progress line for the pane's
        // live progress card. For Status it's the backend's "label"; for Tool
        // it's the backend-authored "label" (e.g. "Creating wall on Level 1"),
        // falling back to "<tool>…". Empty for other kinds.
        public string StatusLabel { get; init; } = "";

        // Tool only — the raw tool name from the agent's tool-call trace
        // (e.g. "create_wall"). Empty for non-Tool chunks.
        public string ToolName { get; init; } = "";

        // Live step-trail fields (optional on the wire; default to a sane
        // running state so an un-upgraded backend still renders transiently).
        public string StepId { get; init; } = "";
        public string Phase { get; init; } = "";
        public string State { get; init; } = "running"; // running | done | error
        public string Detail { get; init; } = "";
    }

    public static partial class AIServiceStreamExtensions
    {
        /// <summary>
        /// Pure SSE-event → StreamChunk parser. No HTTP, no I/O — given an
        /// event name and its data payload it returns the typed chunk. Public
        /// so it can be unit-tested directly (the streaming loop is hard to
        /// exercise without a live server).
        /// </summary>
        public static StreamChunk ParseEvent(string eventName, string raw)
        {
            try
            {
                switch (eventName)
                {
                    case "meta":
                        return new StreamChunk { Kind = StreamChunkKind.Meta, RawData = raw };
                    case "status":
                        using (var doc = JsonDocument.Parse(raw))
                        {
                            // Backend sends { "label", "step_id"?, "phase"?,
                            // "state"?, "detail"? }. Tolerate "message"/"text"
                            // aliases and bare-label (old backend) payloads.
                            var root = doc.RootElement;
                            string label =
                                root.TryGetProperty("label", out var lbl) ? (lbl.GetString() ?? "")
                                : root.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "")
                                : root.TryGetProperty("text", out var txt) ? (txt.GetString() ?? "")
                                : "";
                            string sState = root.TryGetProperty("state", out var sst) ? (sst.GetString() ?? "running") : "running";
                            string sStepId = root.TryGetProperty("step_id", out var ssid) ? (ssid.GetString() ?? "") : "";
                            string sPhase = root.TryGetProperty("phase", out var sph) ? (sph.GetString() ?? "") : "";
                            string sDetail = root.TryGetProperty("detail", out var sde) ? (sde.GetString() ?? "") : "";
                            if (string.IsNullOrEmpty(sStepId))
                                sStepId = Guid.NewGuid().ToString("N");
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Status,
                                StatusLabel = label,
                                StepId = sStepId,
                                Phase = sPhase,
                                State = sState,
                                Detail = sDetail,
                                RawData = raw,
                            };
                        }
                    case "tool":
                        using (var doc = JsonDocument.Parse(raw))
                        {
                            // New backend sends { "tool", "step_id", "phase",
                            // "label", "detail", "state" }. Old backend sends
                            // just { "tool"/"name", "status"? } — tolerate both.
                            var root = doc.RootElement;
                            string tool =
                                root.TryGetProperty("tool", out var t) ? (t.GetString() ?? "")
                                : root.TryGetProperty("name", out var n) ? (n.GetString() ?? "")
                                : "";
                            string tstatus = root.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                            string state = root.TryGetProperty("state", out var st) ? (st.GetString() ?? "running") : "running";
                            string stepId = root.TryGetProperty("step_id", out var sid) ? (sid.GetString() ?? "") : "";
                            string phase = root.TryGetProperty("phase", out var ph) ? (ph.GetString() ?? "") : "";
                            string detail = root.TryGetProperty("detail", out var de) ? (de.GetString() ?? "") : "";
                            string richLabel = root.TryGetProperty("label", out var lb) ? (lb.GetString() ?? "") : "";
                            if (string.IsNullOrEmpty(stepId))
                                stepId = string.IsNullOrEmpty(tool) ? Guid.NewGuid().ToString("N") : tool;
                            // Prefer the backend's rich label. Otherwise map the raw
                            // tool name through the single ToolLabels table so even an
                            // un-upgraded backend reads "Reading grids…" not "list_grids…".
                            string label = !string.IsNullOrEmpty(richLabel)
                                ? richLabel
                                : (string.IsNullOrEmpty(tool)
                                    ? "Working…"
                                    : (string.IsNullOrEmpty(tstatus)
                                        ? ToolLabels.Label(tool) + "…"
                                        : ToolLabels.Label(tool) + " (" + tstatus + ")…"));
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Tool,
                                ToolName = tool,
                                StatusLabel = label,
                                StepId = stepId,
                                Phase = phase,
                                State = state,
                                Detail = detail,
                                RawData = raw,
                            };
                        }
                    case "reply_partial":
                        using (var rdoc = JsonDocument.Parse(raw))
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Reply,
                                Delta = rdoc.RootElement.TryGetProperty("delta", out var rd) ? (rd.GetString() ?? "") : "",
                                RawData = raw,
                            };
                    case "code_partial":
                        using (var doc = JsonDocument.Parse(raw))
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.CodePartial,
                                Delta = doc.RootElement.TryGetProperty("delta", out var d) ? (d.GetString() ?? "") : "",
                                RawData = raw,
                            };
                    case "done":
                        return new StreamChunk
                        {
                            Kind = StreamChunkKind.Done,
                            Final = JsonConvert.DeserializeObject<AIResponse>(raw),
                            RawData = raw,
                        };
                    case "error":
                        using (var doc = JsonDocument.Parse(raw))
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Error,
                                Error = doc.RootElement.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : raw,
                                RawData = raw,
                            };
                    default:
                        return new StreamChunk { Kind = StreamChunkKind.Unknown, RawData = raw };
                }
            }
            catch
            {
                return new StreamChunk { Kind = StreamChunkKind.Unknown, RawData = raw };
            }
        }
    }
}
