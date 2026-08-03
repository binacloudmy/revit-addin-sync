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
        /// Binary kinds (DWG, PDF) get their own block instead: the ref — the
        /// agent's handle for that kind's detail tools — plus the compact summary
        /// the addin produced, never file bytes. A file that could NOT be read
        /// still gets a block saying so, so the agent reports the failure instead
        /// of silently ignoring the attachment.</summary>
        public static string Build(string text, List<FileAttachment> files)
        {
            if (files == null || files.Count == 0) return text;
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                // ReadError is checked FIRST, before the kind. A rejected text file
                // (over the cap, unsupported, locked) would otherwise take the
                // branch below and emit "[Attached: rules.csv]" followed by nothing
                // — the agent would believe it received an empty file rather than
                // no file, and answer without mentioning it.
                if (f.ReadError != null)
                {
                    sb.Append("[").Append(f.BlockLabel).Append(": ").Append(f.Name)
                      .Append(" — could not be read: ").Append(f.ReadError)
                      .AppendLine("]");
                }
                else if (f.Kind == AttachmentKind.Text)
                {
                    sb.Append("[Attached: ").Append(f.Name).AppendLine("]");
                    sb.AppendLine(f.Content);
                }
                else if (f.SummaryJson != null)
                {
                    sb.Append("[").Append(f.BlockLabel).Append(": ").Append(f.Name)
                      .Append(" ref=").Append(f.Ref ?? "").AppendLine("]");
                    sb.AppendLine(f.SummaryJson);
                }
                else
                {
                    sb.Append("[").Append(f.BlockLabel).Append(": ").Append(f.Name)
                      .Append(" — could not be read: ").Append(f.ReadError ?? "unknown error")
                      .AppendLine("]");
                }
                sb.AppendLine("---");
            }
            sb.Append(text);
            return sb.ToString();
        }
    }
}
