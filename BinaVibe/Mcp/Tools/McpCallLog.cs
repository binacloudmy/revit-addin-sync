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
    }
}
