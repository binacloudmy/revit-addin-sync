// PdfAttachmentCache — the DwgScratchCache twin for attached PDFs.
//
// Holds one extracted PdfDoc per "pdf:<guid>" ref so the summary call and every
// later drill-down (get_pdf_page_text, search_pdf) read the same text without
// re-parsing the file. Deliberately simpler than the DWG cache: PdfReader.Extract
// closes the file before returning, so nothing here holds a file handle open —
// only strings. That also means a PDF the user edits after attaching keeps
// answering from the version they attached, which is the honest behaviour for a
// turn that already reported its summary.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools
{
    public static class PdfAttachmentCache
    {
        private sealed class Entry
        {
            public PdfDoc Doc = new();
            public DateTime LastUsed;
        }

        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

        /// <summary>Read an attached PDF and return its pdf_ref. Throws with a
        /// drafter-readable reason; the pane degrades to a one-line note.</summary>
        public static string OpenAttachment(string path)
        {
            Sweep();

            // Same file attached twice in a session: reuse the parse.
            foreach (var kv in _entries)
                if (string.Equals(kv.Value.Doc.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    kv.Value.LastUsed = DateTime.UtcNow;
                    return kv.Key;
                }

            var doc = PdfReader.Extract(path);   // throws on missing / oversized / corrupt
            var pdfRef = "pdf:" + Guid.NewGuid().ToString("N");
            _entries[pdfRef] = new Entry { Doc = doc, LastUsed = DateTime.UtcNow };
            return pdfRef;
        }

        /// <summary>Run <paramref name="body"/> against the document behind a
        /// "pdf:" ref.</summary>
        public static T Use<T>(string pdfRef, Func<PdfDoc, T> body)
        {
            if (!_entries.TryGetValue(pdfRef, out var entry))
                throw new InvalidOperationException(
                    "unknown pdf_ref " + pdfRef + " — the attachment is no longer open; ask the user to re-attach the PDF");
            entry.LastUsed = DateTime.UtcNow;
            return body(entry.Doc);
        }

        public static bool IsAttachmentRef(string pdfRef) =>
            !string.IsNullOrEmpty(pdfRef) && pdfRef.StartsWith("pdf:", StringComparison.Ordinal);

        /// <summary>Drop every cached document. Called when the pane's session
        /// ends and on shutdown — these are page-text strings for whole spec
        /// documents, not something to keep for the life of the process.</summary>
        public static void CloseAll() => _entries.Clear();

        private static void Sweep()
        {
            var stale = _entries.Where(kv => DateTime.UtcNow - kv.Value.LastUsed > Ttl)
                                .Select(kv => kv.Key).ToList();
            foreach (var key in stale) _entries.Remove(key);
        }
    }
}
