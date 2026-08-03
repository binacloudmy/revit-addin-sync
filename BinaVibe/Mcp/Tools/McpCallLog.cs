// McpCallLog — UAT trace of the MCP tool-call SEQUENCE, so we can prove the
// copilot's perceive->act->verify->correct roundtrip actually fired (query_geometry
// after a mutate, move_elements to correct, query_geometry again, …).
//
// Appends one line per call to %APPDATA%\RevitWebAppSync\mcp-calls.log. Fully
// best-effort: any failure is swallowed — logging must never break a tool call.
// (UAT-only aid; safe to strip before release.)

using System;
using System.IO;
using System.Text.Json;

namespace BinaVibe.Mcp.Tools
{
    internal static class McpCallLog
    {
        private static readonly object _gate = new object();
        private static string? _path;

        private static string Path()
        {
            if (_path != null) return _path;
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitWebAppSync");
            try { Directory.CreateDirectory(dir); } catch { }
            _path = System.IO.Path.Combine(dir, "mcp-calls.log");
            return _path;
        }

        public static void Write(string tool, JsonElement args)
        {
            try
            {
                string a = args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText();
                if (a.Length > 600) a = a.Substring(0, 600) + "…";
                string line = $"{DateTime.Now:HH:mm:ss.fff}  {tool}  {a}{Environment.NewLine}";
                lock (_gate) File.AppendAllText(Path(), line);
            }
            catch { /* never break a tool call over logging */ }
        }

        /// <summary>
        /// Free-text trace line. Used by the HTTP tool loop to record what the
        /// backend actually sent and what we did with it — a tool call that is
        /// PARKED (awaiting confirmation) or auto-rejected never reaches Invoke, so
        /// it leaves no trace at all and looks identical to "Revit never responded".
        /// </summary>
        public static void Note(string message)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff}  [loop] {message}{Environment.NewLine}";
                lock (_gate) File.AppendAllText(Path(), line);
            }
            catch { /* never break a tool call over logging */ }
        }

        /// <summary>
        /// Log what a tool RETURNED, not just that it was called.
        ///
        /// Added because a mutator reported "40 conceptual masses placed" while the
        /// model looked empty, and there was no way to see the category / LOD /
        /// visibility fields it had reported — the call log recorded only the
        /// arguments, and the pane's Count card silently dropped the detail text. With
        /// the result logged, one Build answers what previously took a Revit journal
        /// plus several by-category queries to guess at.
        /// </summary>
        public static void WriteResult(string tool, object? result)
        {
            try
            {
                string r;
                try
                {
                    r = result == null
                        ? "null"
                        : JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
                }
                catch (Exception ex) { r = $"(unserialisable: {ex.GetType().Name})"; }

                // Results are small summaries, but never let a rogue payload bloat the
                // log — the cap is generous enough to keep every field we report.
                if (r.Length > 4000) r = r.Substring(0, 4000) + "…";
                string line = $"{DateTime.Now:HH:mm:ss.fff}  {tool}  -> {r}{Environment.NewLine}";
                lock (_gate) File.AppendAllText(Path(), line);
            }
            catch { /* never break a tool call over logging */ }
        }
    }
}
