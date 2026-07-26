using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The PDF attachment extractor. Fixtures are generated with PdfPig's
    /// own writer, so there are no checked-in binaries and no native dependency.</summary>
    public class PdfReaderTests : IDisposable
    {
        private readonly List<string> _temp = new();

        public void Dispose()
        {
            foreach (var p in _temp) { try { File.Delete(p); } catch { } }
        }

        /// <summary>Write a PDF whose pages carry the given lines. A null entry
        /// makes a page with no text at all — the stand-in for a scanned page.</summary>
        private string MakePdf(params string[][] pages)
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            foreach (var lines in pages)
            {
                var page = builder.AddPage(PageSize.A4);
                if (lines == null) continue;
                int y = 700;
                foreach (var line in lines)
                {
                    page.AddText(line, 12, new PdfPoint(25, y), font);
                    y -= 20;
                }
            }
            var path = Path.Combine(Path.GetTempPath(), "bina-pdftest-" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(path, builder.Build());
            _temp.Add(path);
            return path;
        }

        private static T Get<T>(Dictionary<string, object?> d, string key) => (T)d[key]!;

        // ─── summary ────────────────────────────────────────────────────

        [Fact]
        public void Summary_ReportsPagesTextAndPreview()
        {
            var doc = PdfReader.Extract(MakePdf(
                new[] { "Parameter naming convention" },
                new[] { "Every wall carries a fire rating" }));

            var s = PdfReader.Summarize(doc, "pdf:abc");

            Assert.Equal("pdf.summary/1", Get<string>(s, "schema"));
            Assert.Equal("pdf:abc", Get<string>(s, "pdf_ref"));
            Assert.Equal(2, Get<int>(s, "pages"));
            Assert.True(Get<bool>(s, "has_text_layer"));
            Assert.Contains("Parameter naming", Get<string>(s, "preview"));
            Assert.Equal(0, Get<int>(s, "pages_without_text_count"));
        }

        [Fact]
        public void Summary_AlwaysDeclaresWhatCannotBeRead()
        {
            var doc = PdfReader.Extract(MakePdf(new[] { "anything" }));
            var unavailable = Get<List<object>>(PdfReader.Summarize(doc, "pdf:abc"), "unavailable");

            // Figures and drawing geometry are unreadable from ANY PDF here. If
            // this list ever empties, the agent starts describing linework it
            // never saw.
            Assert.Contains("figures", unavailable);
            Assert.Contains("drawing_geometry", unavailable);
            Assert.DoesNotContain("text", unavailable);
        }

        [Fact]
        public void ScannedPdf_ReportsNoTextLayerInsteadOfPretending()
        {
            // A page with no text at all is what a scan looks like to PdfPig.
            var doc = PdfReader.Extract(MakePdf(null, null));
            var s = PdfReader.Summarize(doc, "pdf:abc");

            Assert.False(Get<bool>(s, "has_text_layer"));
            Assert.Contains("text", Get<List<object>>(s, "unavailable"));
            Assert.Equal(2, Get<int>(s, "pages_without_text_count"));
            Assert.Equal("", Get<string>(s, "preview"));
        }

        // ─── page text ──────────────────────────────────────────────────

        [Fact]
        public void PageContent_TruncatesAndSaysSo()
        {
            var doc = PdfReader.Extract(MakePdf(new[] { "abcdefghijklmnopqrstuvwxyz" }));
            var page = PdfReader.PageContent(doc, "pdf:abc", 1, 200);   // 200 = floor

            Assert.True(Get<bool>(page, "page_has_text"));
            Assert.Equal(Get<string>(page, "text").Length, Get<int>(page, "chars"));
            Assert.False(Get<bool>(page, "truncated"));
        }

        [Fact]
        public void PageContent_RejectsAPageThatDoesNotExist()
        {
            var doc = PdfReader.Extract(MakePdf(new[] { "one page only" }));

            // Silently clamping to the last page would have the agent quote page 9
            // of a 1-page document.
            var ex = Assert.Throws<InvalidOperationException>(
                () => PdfReader.PageContent(doc, "pdf:abc", 9, 4000));
            Assert.Contains("out of range", ex.Message);
        }

        // ─── search ─────────────────────────────────────────────────────

        [Fact]
        public void Search_FindsHitsWithPageNumbers_CaseInsensitive()
        {
            var doc = PdfReader.Extract(MakePdf(
                new[] { "The fire rating is stated here" },
                new[] { "Nothing relevant" },
                new[] { "Fire rating appears again" }));

            var r = PdfReader.Search(doc, "pdf:abc", "fire rating", 10);
            var hits = Get<List<object>>(r, "hits").Cast<Dictionary<string, object?>>().ToList();

            Assert.Equal(2, Get<int>(r, "total_found"));
            Assert.Equal(new[] { 1, 3 }, hits.Select(h => (int)h["page"]!).ToArray());
            Assert.Contains("fire rating", ((string)hits[0]["snippet"]!).ToLowerInvariant());
            Assert.False(Get<bool>(r, "truncated"));
        }

        [Fact]
        public void Search_CountsEveryHitEvenWhenTheListIsCapped()
        {
            var doc = PdfReader.Extract(MakePdf(
                new[] { "wall", "wall", "wall", "wall" }));

            var r = PdfReader.Search(doc, "pdf:abc", "wall", 2);

            // Reporting count as the total would let the agent say "2 mentions"
            // about a document with 4.
            Assert.Equal(2, Get<int>(r, "count"));
            Assert.Equal(4, Get<int>(r, "total_found"));
            Assert.True(Get<bool>(r, "truncated"));
        }

        [Fact]
        public void Search_RequiresAQuery()
        {
            var doc = PdfReader.Extract(MakePdf(new[] { "text" }));
            Assert.Throws<InvalidOperationException>(() => PdfReader.Search(doc, "pdf:abc", "  ", 10));
        }

        // ─── extraction guards + cache ──────────────────────────────────

        [Fact]
        public void Extract_RejectsNonPdfAndMissingFiles()
        {
            Assert.Throws<InvalidOperationException>(() => PdfReader.Extract(null));
            Assert.Throws<InvalidOperationException>(
                () => PdfReader.Extract(Path.Combine(Path.GetTempPath(), "definitely-missing.pdf")));

            var notPdf = Path.Combine(Path.GetTempPath(), "bina-pdftest-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(notPdf, "hello");
            _temp.Add(notPdf);
            Assert.Throws<InvalidOperationException>(() => PdfReader.Extract(notPdf));
        }

        [Fact]
        public void Extract_RejectsACorruptPdfWithAReadableReason()
        {
            var fake = Path.Combine(Path.GetTempPath(), "bina-pdftest-" + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllText(fake, "this is not a PDF at all");
            _temp.Add(fake);

            var ex = Assert.Throws<InvalidOperationException>(() => PdfReader.Extract(fake));
            Assert.Contains("could not read this PDF", ex.Message);
        }

        [Fact]
        public void Cache_ReusesTheSameFileAndRejectsUnknownRefs()
        {
            var path = MakePdf(new[] { "cached" });

            var first = PdfAttachmentCache.OpenAttachment(path);
            var second = PdfAttachmentCache.OpenAttachment(path);
            Assert.Equal(first, second);                       // parsed once
            Assert.True(PdfAttachmentCache.IsAttachmentRef(first));

            Assert.Equal(1, PdfAttachmentCache.Use(first, d => d.PageCount));

            var ex = Assert.Throws<InvalidOperationException>(
                () => PdfAttachmentCache.Use("pdf:nope", d => d.PageCount));
            Assert.Contains("re-attach", ex.Message);

            PdfAttachmentCache.CloseAll();
            Assert.Throws<InvalidOperationException>(() => PdfAttachmentCache.Use(first, d => d.PageCount));
        }
    }
}
