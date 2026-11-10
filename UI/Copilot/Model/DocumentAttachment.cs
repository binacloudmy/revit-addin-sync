#nullable enable
// A PDF as a document attachment (AI-297, protocol 3). Revit-free.
//
// The pages travel as attachments[kind=document] instead of being pasted in front
// of the prompt: nothing is cut at 20,000 characters, the cloud knows each page,
// and the text stays out of every later turn's history. The prompt only carries a
// one-line pointer (RouteText).

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class DocumentData
    {
        public string Name = "";
        /// <summary>Page texts in order, index 0 = page 1; "" for a page without a text layer.</summary>
        public List<string> Pages = new List<string>();
        /// <summary>Pages in the file; more than Pages.Count when the document was cut.</summary>
        public int TotalPages;
        public bool Truncated => TotalPages > Pages.Count;
        public bool HasText => Pages.Any(p => p.Length > 0);
    }

    public static class DocumentAttachment
    {
        /// <summary>Characters of page text sent in all; pages past it are left out and counted in page_count.</summary>
        public const int MaxChars = 400_000;

        /// <summary>The pages of a PDF read by PdfReader (index 0 = page 1). At least one page is always kept.</summary>
        public static DocumentData FromPages(string name, IReadOnlyList<string> pageText)
        {
            var d = new DocumentData { Name = name ?? "", TotalPages = pageText?.Count ?? 0 };
            var used = 0;
            foreach (var raw in pageText ?? Array.Empty<string>())
            {
                var text = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
                if (d.Pages.Count >= AttachmentLimits.MaxPages || (d.Pages.Count > 0 && used + text.Length > MaxChars)) break;
                d.Pages.Add(text);
                used += text.Length;
            }
            return d;
        }

        public static AttachmentDto ToAttachment(DocumentData d) => new AttachmentDto
        {
            Name = d.Name,
            Kind = AttachmentKinds.Document,
            Pages = d.Pages.Select((t, i) => new AttachmentPageDto { Page = i + 1, Text = t }).ToList(),
            PageCount = d.Truncated ? d.TotalPages : (int?)null,
        };
    }
}
