// PdfReader — the DwgReader twin for PDF attachments.
//
// Same contract as the DWG path: the addin reads the file locally, the model
// gets a compact versioned summary plus tools to drill in, and the file's bytes
// never leave the drafter's machine. Extraction is PdfPig (Apache-2.0, pure
// managed) rather than a cloud parser, so an attachment costs nothing per page
// and works offline.
//
// FIDELITY CEILING — read this before "fixing" a missing field. PdfPig reads the
// TEXT LAYER. It does not read:
//   - text in scanned / image-only PDFs (there is no text layer to read)
//   - the contents of embedded figures, photos and diagrams
//   - the GEOMETRY of a drawing exported to PDF (the annotation text comes
//     through; the linework does not — that is what the DWG path is for)
// Every result ships an `unavailable` list saying so, and the tool guidance
// forbids quoting what was not returned. All PdfPig types stay inside this file,
// so an OCR or LlamaParse engine can later fill those gaps by producing the same
// PdfDoc, with no change to the tools, the prompt, or the pane.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>Everything read out of one PDF, held as plain strings. Extracted
    /// once at attach time so no file handle is kept open (a DWG has to keep its
    /// scratch document open because Revit geometry can't be snapshotted cheaply;
    /// page text can).</summary>
    public sealed class PdfDoc
    {
        public string Name = "";
        public string Path = "";
        public string Title = "";
        public string Author = "";
        public string Producer = "";
        public string Created = "";
        /// <summary>Page text, index 0 = page 1. Empty string = no text on that page.</summary>
        public string[] PageText = Array.Empty<string>();
        public List<(string Title, int Page, int Level)> Outline = new();

        public int PageCount => PageText.Length;
        public bool HasTextLayer => PageText.Any(t => t.Length > 0);
    }

    public static class PdfReader
    {
        public const string Schema = "pdf.summary/1";

        private const long MaxBytes = 64L * 1024 * 1024;
        private const int MaxPages = 2000;
        private const int PreviewChars = 800;
        private const int MaxOutline = 25;
        private const int MaxListedPages = 25;
        private const int DefaultPageChars = 4000;
        private const int MaxPageChars = 20000;
        private const int SnippetRadius = 120;
        private const int MaxSearchHits = 50;

        private static readonly HashSet<string> SupportedExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".pdf" };

        /// <summary>What can never be read out of a PDF this way. `text` joins the
        /// list when the file has no text layer at all (a scan).</summary>
        private static List<object> UnavailableFields(bool hasTextLayer)
        {
            var list = new List<object> { "figures", "drawing_geometry" };
            if (!hasTextLayer) list.Add("text");
            return list;
        }

        // ─── extraction (the only place PdfPig is touched) ──────────────

        /// <summary>Read a PDF into a PdfDoc and close the file. Throws with a
        /// drafter-readable reason (missing, wrong type, too big, corrupt,
        /// password-protected) — the pane turns that into a one-line note and
        /// still sends the turn.</summary>
        public static PdfDoc Extract(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("no file path given");
            if (!File.Exists(path))
                throw new InvalidOperationException("file not found: " + path);
            if (!SupportedExtensions.Contains(System.IO.Path.GetExtension(path)))
                throw new InvalidOperationException("not a PDF: " + System.IO.Path.GetFileName(path));

            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
                throw new InvalidOperationException(
                    $"PDF is {info.Length / (1024 * 1024)}MB — the limit is {MaxBytes / (1024 * 1024)}MB");

            var doc = new PdfDoc
            {
                Name = System.IO.Path.GetFileName(path),
                Path = path,
            };

            try
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
                if (pdf.NumberOfPages > MaxPages)
                    throw new InvalidOperationException(
                        $"PDF has {pdf.NumberOfPages} pages — the limit is {MaxPages}");

                var texts = new string[pdf.NumberOfPages];
                for (int i = 1; i <= pdf.NumberOfPages; i++)
                {
                    // One unreadable page must not lose the other 200.
                    try { texts[i - 1] = pdf.GetPage(i).Text ?? ""; }
                    catch { texts[i - 1] = ""; }
                }
                doc.PageText = texts;

                var info2 = pdf.Information;
                doc.Title = info2?.Title ?? "";
                doc.Author = info2?.Author ?? "";
                doc.Producer = info2?.Producer ?? "";
                doc.Created = info2?.CreationDate ?? "";

                doc.Outline = ReadOutline(pdf);
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "could not read this PDF (corrupt, password-protected, or an unsupported "
                    + "variant): " + ex.Message);
            }

            return doc;
        }

        private static List<(string Title, int Page, int Level)> ReadOutline(UglyToad.PdfPig.PdfDocument pdf)
        {
            var outline = new List<(string, int, int)>();
            try
            {
                if (!pdf.TryGetBookmarks(out var bookmarks) || bookmarks == null) return outline;
                foreach (var node in bookmarks.GetNodes())
                {
                    var title = (node.Title ?? "").Trim();
                    if (title.Length == 0) continue;
                    int page = node is UglyToad.PdfPig.Outline.DocumentBookmarkNode d ? d.PageNumber : 0;
                    outline.Add((title, page, node.Level));
                }
            }
            catch { /* malformed outline — the summary is still useful without it */ }
            return outline;
        }

        // ─── summary (pdf.summary/1) ────────────────────────────────────

        public static Dictionary<string, object?> Summarize(PdfDoc doc, string pdfRef)
        {
            var emptyPages = new List<object>();
            int emptyCount = 0;
            for (int i = 0; i < doc.PageText.Length; i++)
            {
                if (doc.PageText[i].Length > 0) continue;
                emptyCount++;
                if (emptyPages.Count < MaxListedPages) emptyPages.Add(i + 1);
            }

            var outline = doc.Outline.Take(MaxOutline).Select(o => (object)new Dictionary<string, object?>
            {
                ["title"] = o.Title,
                ["page"] = o.Page,
                ["level"] = o.Level,
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["schema"] = Schema,
                ["pdf_ref"] = pdfRef,
                ["name"] = doc.Name,
                ["pages"] = doc.PageCount,
                ["title"] = doc.Title,
                ["author"] = doc.Author,
                ["producer"] = doc.Producer,
                ["created"] = doc.Created,
                ["has_text_layer"] = doc.HasTextLayer,
                ["text_chars"] = doc.PageText.Sum(t => t.Length),
                ["pages_without_text"] = emptyPages,
                ["pages_without_text_count"] = emptyCount,
                ["outline"] = outline,
                ["outline_truncated"] = Math.Max(0, doc.Outline.Count - outline.Count),
                ["preview"] = Preview(doc),
                ["unavailable"] = UnavailableFields(doc.HasTextLayer),
            };
        }

        /// <summary>First PreviewChars of real text, so the agent can tell what
        /// the document IS without a round-trip. Deliberately small: the whole
        /// point of the summary is a bounded turn regardless of PDF size.</summary>
        private static string Preview(PdfDoc doc)
        {
            var sb = new StringBuilder();
            foreach (var page in doc.PageText)
            {
                if (page.Length == 0) continue;
                sb.Append(Collapse(page));
                if (sb.Length >= PreviewChars) break;
                sb.Append(' ');
            }
            return sb.Length <= PreviewChars ? sb.ToString() : sb.ToString(0, PreviewChars) + "…";
        }

        // ─── one page's text ────────────────────────────────────────────

        public static Dictionary<string, object?> PageContent(PdfDoc doc, string pdfRef, int page, int maxChars)
        {
            if (page < 1 || page > doc.PageCount)
                throw new InvalidOperationException(
                    $"page {page} is out of range — this PDF has {doc.PageCount} page(s)");
            maxChars = Math.Max(200, Math.Min(maxChars <= 0 ? DefaultPageChars : maxChars, MaxPageChars));

            var text = doc.PageText[page - 1] ?? "";
            var shown = text.Length <= maxChars ? text : text.Substring(0, maxChars);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["pdf_ref"] = pdfRef,
                ["page"] = page,
                ["pages"] = doc.PageCount,
                ["text"] = shown,
                ["chars"] = shown.Length,
                ["total_chars"] = text.Length,
                ["truncated"] = shown.Length < text.Length,
                // An empty page in a PDF that HAS text elsewhere is a real answer
                // ("nothing on page 7"); in a scan it means the whole file is
                // unreadable. The flag tells the two apart.
                ["page_has_text"] = text.Length > 0,
                ["unavailable"] = UnavailableFields(doc.HasTextLayer),
            };
        }

        // ─── search ─────────────────────────────────────────────────────

        /// <summary>Case-insensitive substring search across pages, returning a
        /// snippet per hit with its page number. The primary way into a long
        /// document — paging through get_pdf_page_text is the slow path.</summary>
        public static Dictionary<string, object?> Search(PdfDoc doc, string pdfRef, string query, int limit)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new InvalidOperationException("query is required");
            limit = Math.Max(1, Math.Min(limit <= 0 ? 10 : limit, MaxSearchHits));

            var hits = new List<object>();
            int found = 0;

            for (int p = 0; p < doc.PageText.Length; p++)
            {
                var text = doc.PageText[p];
                if (text.Length == 0) continue;
                int from = 0;
                while (true)
                {
                    int at = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    found++;
                    if (hits.Count < limit)
                    {
                        int start = Math.Max(0, at - SnippetRadius);
                        int end = Math.Min(text.Length, at + query.Length + SnippetRadius);
                        hits.Add(new Dictionary<string, object?>
                        {
                            ["page"] = p + 1,
                            ["snippet"] = (start > 0 ? "…" : "")
                                          + Collapse(text.Substring(start, end - start))
                                          + (end < text.Length ? "…" : ""),
                        });
                    }
                    from = at + query.Length;
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["pdf_ref"] = pdfRef,
                ["query"] = query,
                ["hits"] = hits,
                ["count"] = hits.Count,
                ["total_found"] = found,
                ["truncated"] = found > hits.Count,
                ["searched_pages"] = doc.PageCount,
                ["unavailable"] = UnavailableFields(doc.HasTextLayer),
            };
        }

        /// <summary>Squash the newlines and runs of spaces PDF text extraction
        /// produces, so snippets read as sentences and cost fewer tokens.</summary>
        private static string Collapse(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }
    }
}
