using System.Collections.Generic;
using System.Text;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Composes the prompt string actually sent to the backend from the
    /// user's text plus whatever they attached. Pure string logic (no WPF, no
    /// Revit) so the wire format is unit-testable — the backend parses these
    /// blocks, so a silent format change is a contract break.</summary>
    public static class RouteText
    {
        /// <summary>Each attached file's contents prepended as a labelled block,
        /// then the user's text. Returns <paramref name="text"/> unchanged when
        /// nothing is attached.
        ///
        /// The text-file block is byte-for-byte identical to the legacy PromptBar
        /// concatenation — the backend sees exactly the same input it always did.
        ///
        /// Drawings get their own block: the dwg_ref (the agent's handle for
        /// get_dwg_summary / get_dwg_layer_detail / get_dwg_blocks) plus the
        /// compact summary Revit produced — never file bytes. A drawing that
        /// could NOT be read still gets a block saying so, so the agent reports
        /// the failure instead of silently ignoring the attachment.</summary>
        public static string Build(string text, List<FileAttachment> files)
        {
            if (files == null || files.Count == 0) return text;
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                if (f.Kind == AttachmentKind.Dwg)
                {
                    if (f.DwgSummaryJson != null)
                    {
                        sb.Append("[Attached DWG: ").Append(f.Name)
                          .Append(" ref=").Append(f.DwgRef ?? "").AppendLine("]");
                        sb.AppendLine(f.DwgSummaryJson);
                    }
                    else
                    {
                        sb.Append("[Attached DWG: ").Append(f.Name)
                          .Append(" — could not be read: ").Append(f.DwgError ?? "unknown error")
                          .AppendLine("]");
                    }
                    sb.AppendLine("---");
                    continue;
                }
                sb.Append("[Attached: ").Append(f.Name).AppendLine("]");
                sb.AppendLine(f.Content);
                sb.AppendLine("---");
            }
            sb.Append(text);
            return sb.ToString();
        }
    }
}
