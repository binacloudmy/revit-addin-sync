#nullable enable
// The id inside a "model:<id>" dwg_ref (or a bare id). A bad ref is the caller's
// argument, so it throws ArgumentException: the MCP handler answers 400, not 500.

using System;
using System.IO;

namespace BinaVibe.Mcp.Tools
{
    public static class DwgRef
    {
        public static long ParseModelId(string? dwgRef)
        {
            if (string.IsNullOrWhiteSpace(dwgRef))
                throw new ArgumentException(
                    "dwg_ref is required — call list_cad_links, or use the ref from the [Attached DWG] block");
            var raw = dwgRef!.StartsWith("model:", StringComparison.OrdinalIgnoreCase)
                ? dwgRef.Substring("model:".Length) : dwgRef;
            if (long.TryParse(raw, out var id)) return id;
            if (raw.IndexOfAny(new[] { '\\', '/' }) >= 0 || Path.HasExtension(raw))
                throw new ArgumentException(
                    "dwg_ref '" + dwgRef + "' is a file path, not a ref — link it with link_file " +
                    "(then use \"model:<id>\") or attach it in the pane (\"att:<guid>\")");
            throw new ArgumentException(
                "bad dwg_ref '" + dwgRef + "' — expected \"model:<id>\" or \"att:<guid>\"");
        }
    }
}
