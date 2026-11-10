#nullable enable
// AI-297: a PDF is a document attachment with pages, not text pasted in front of the prompt.
// The shared fixtures in contracts/copilot/examples/attachments.json round-trip through the
// add-in's types, the rules name what the invalid cases break, a PDF becomes pages (nothing cut
// at 20,000 characters), and the routed prompt keeps only a one-line pointer.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotDocumentAttachmentTests
    {
        private static string ContractsDir([CallerFilePath] string thisFile = "")
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "contracts", "copilot");
            if (File.Exists(Path.Combine(copied, "tools.json"))) return copied;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "contracts", "copilot");
                if (File.Exists(Path.Combine(candidate, "tools.json"))) return candidate;
            }
            var fromSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "contracts", "copilot"));
            if (File.Exists(Path.Combine(fromSource, "tools.json"))) return fromSource;
            throw new DirectoryNotFoundException("contracts/copilot not found");
        }

        private static JsonElement Fixtures() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), "examples", "attachments.json"))).RootElement.Clone();

        public static IEnumerable<object[]> ValidNames() => Fixtures().GetProperty("valid").EnumerateObject().Select(p => new object[] { p.Name });
        public static IEnumerable<object[]> InvalidNames() => Fixtures().GetProperty("invalid").EnumerateObject().Select(p => new object[] { p.Name });

        [Theory]
        [MemberData(nameof(ValidNames))]
        public void Valid_AttachmentRoundTripsAndHasNoViolations(string name)
        {
            var raw = Fixtures().GetProperty("valid").GetProperty(name).GetRawText();
            var dto = CopilotJson.Deserialize<AttachmentDto>(raw)!;
            Assert.Empty(AttachmentRules.Violations(dto));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(raw), JsonNode.Parse(CopilotJson.Serialize(dto))), name + " changed on the round trip");
        }

        [Theory]
        [MemberData(nameof(InvalidNames))]
        public void Invalid_IsReportedAsTheFixtureSays(string name)
        {
            var c = Fixtures().GetProperty("invalid").GetProperty(name);
            var raw = c.GetProperty("attachment").GetRawText();
            switch (c.GetProperty("add_in").GetString())
            {
                case "unreadable":
                    Assert.ThrowsAny<JsonException>(() => CopilotJson.Deserialize<AttachmentDto>(raw));
                    break;
                case "violation":
                    Assert.NotEmpty(AttachmentRules.Violations(CopilotJson.Deserialize<AttachmentDto>(raw)!));
                    break;
                case "ignored":
                    Assert.Empty(AttachmentRules.Violations(CopilotJson.Deserialize<AttachmentDto>(raw)!));
                    break;
                default:
                    throw new InvalidDataException(name + ": add_in must be violation, unreadable or ignored");
            }
        }

        [Fact]
        public void Schema_DocumentKindAndKeysMatchTheTypes()
        {
            var defs = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir(), "protocol3.schema.json"))).RootElement.GetProperty("$defs");
            var kinds = defs.GetProperty("attachment").GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(k => k.GetString()!).ToArray();
            Assert.Equal(AttachmentKinds.All, kinds);
            Assert.Equal(AttachmentLimits.MaxPages, defs.GetProperty("attachment").GetProperty("properties").GetProperty("pages").GetProperty("maxItems").GetInt32());

            string[] Names(Type t) => t.GetProperties()
                .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name)
                .Where(n => n != null).Select(n => n!).OrderBy(x => x).ToArray();
            string[] Keys(string def) => defs.GetProperty(def).GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();
            Assert.Equal(Keys("attachment"), Names(typeof(AttachmentDto)));
            Assert.Equal(Keys("attachment_page"), Names(typeof(AttachmentPageDto)));
        }

        // ---- a PDF becomes pages ------------------------------------------------------------------

        [Fact]
        public void FromPages_KeepsEveryPageAndItsNumber_NothingCutAt20000Characters()
        {
            var pages = Enumerable.Range(1, 30).Select(i => "Page " + i + " " + new string('x', 5000)).ToList();   // 150,000 characters
            var d = DocumentAttachment.FromPages("brochure.pdf", pages);

            Assert.Equal(30, d.Pages.Count);
            Assert.False(d.Truncated);
            var a = DocumentAttachment.ToAttachment(d);
            Assert.Empty(AttachmentRules.Violations(a));
            Assert.Equal(AttachmentKinds.Document, a.Kind);
            Assert.Equal(30, a.Pages![29].Page);
            Assert.StartsWith("Page 30 ", a.Pages[29].Text);
            Assert.Null(a.PageCount);   // not cut: page_count is only for a cut document
        }

        [Fact]
        public void FromPages_PageEightHoldsTheUrl()
        {
            var pages = Enumerable.Range(1, 8).Select(i => i == 8 ? "Contact us at www.pcolite.com" : "Page " + i).ToList();
            var a = DocumentAttachment.ToAttachment(DocumentAttachment.FromPages("b.pdf", pages));
            Assert.Contains("www.pcolite.com", a.Pages!.Single(p => p.Page == 8).Text);
        }

        [Fact]
        public void FromPages_CutsAtTheCharacterBudgetAndSaysHowManyPagesTheFileHas()
        {
            var big = new string('y', 90_000);
            var d = DocumentAttachment.FromPages("long.pdf", new[] { big, big, big, big, big });   // 450,000 characters, 200,000 allowed
            Assert.Equal(2, d.Pages.Count);
            Assert.True(d.Truncated);
            var a = DocumentAttachment.ToAttachment(d);
            Assert.Equal(5, a.PageCount);
            Assert.Empty(AttachmentRules.Violations(a));
        }

        [Fact]
        public void FromPages_AlwaysKeepsOnePage_AndNormalisesLineEnds()
        {
            var d = DocumentAttachment.FromPages("one.pdf", new[] { "  line 1\r\nline 2\rline 3  " });
            Assert.Equal("line 1\nline 2\nline 3", Assert.Single(d.Pages));
            var huge = DocumentAttachment.FromPages("h.pdf", new[] { new string('z', DocumentAttachment.MaxChars + 10) });
            Assert.Equal(DocumentAttachment.MaxChars, Assert.Single(huge.Pages).Length);   // one page alone never passes the cap
            Assert.Equal(1000, Assert.Single(DocumentAttachment.FromPages("t.pdf", new[] { new string('z', 5000) }, 1000).Pages).Length);
        }

        [Fact]
        public void FromPages_AScanHasNoTextAndIsNotSent()
        {
            var d = DocumentAttachment.FromPages("scan.pdf", new[] { "", "", "" });
            Assert.Equal(3, d.TotalPages);
            Assert.False(d.HasText);
        }

        [Fact]
        public void FromPages_BlankPagesKeepTheirNumbers()
        {
            var a = DocumentAttachment.ToAttachment(DocumentAttachment.FromPages("b.pdf", new[] { "one", "", "three" }));
            Assert.Equal(new[] { 1, 2, 3 }, a.Pages!.Select(p => p.Page).ToArray());
            Assert.Equal("", a.Pages![1].Text);
            Assert.Empty(AttachmentRules.Violations(a));
        }

        // ---- the prompt keeps a pointer, not the pages --------------------------------------------

        [Fact]
        public void RouteText_ADocumentSentAsAnAttachmentIsOneLine_NotItsText()
        {
            var pdf = FileAttachment.ForDocument("PCO.pdf", @"C:\specs\PCO.pdf");
            pdf.Ref = "pdf:1";
            pdf.SummaryJson = "{\"schema\":\"pdf.summary/1\",\"page_text\":[{\"page\":8,\"text\":\"Contact us at www.pcolite.com\"}]}";
            pdf.DocumentName = "PCO.pdf";
            pdf.DocumentPages = 8;
            pdf.DocumentTotalPages = 8;

            var routed = RouteText.Build("fill the URL", new List<FileAttachment> { pdf });

            Assert.Contains("[Attached document: PCO.pdf — 8 pages, sent as a document attachment]", routed);
            Assert.DoesNotContain("www.pcolite.com", routed);
            Assert.DoesNotContain("pdf.summary", routed);
            Assert.EndsWith("fill the URL", routed);
        }

        [Fact]
        public void RouteText_ACutDocumentSaysHowManyPagesWentOut()
        {
            var pdf = FileAttachment.ForDocument("long.pdf", @"C:\x\long.pdf");
            pdf.DocumentName = "long.pdf";
            pdf.DocumentPages = 2;
            pdf.DocumentTotalPages = 5;
            Assert.Contains("5 pages, the first 2 sent, sent as a document attachment", RouteText.Build("x", new List<FileAttachment> { pdf }));
        }

        [Theory]
        [InlineData("[Attached document: PCO.pdf — 8 pages, sent as a document attachment]\n---\nfill", "PCO.pdf")]
        [InlineData("[Attached PDF: PIAWAIAN.pdf ref=pdf:9f2c]\n{}\n---\nx", "PIAWAIAN.pdf")]
        [InlineData("[Attached: notes.txt]\nhello\n---\nx", null)]
        [InlineData("no attachment", null)]
        [InlineData("", null)]
        public void RouteText_AttachmentName_FindsTheDocumentPointerToo(string routed, string? expected) =>
            Assert.Equal(expected, RouteText.AttachmentName(routed));

        [Fact]
        public void RouteText_APdfWithoutAnAttachmentKeepsItsSummaryBlock()
        {
            var pdf = FileAttachment.ForDocument("scan.pdf", @"C:\x\scan.pdf");
            pdf.Ref = "pdf:2";
            pdf.SummaryJson = "{\"schema\":\"pdf.summary/1\"}";
            Assert.Contains("pdf.summary/1", RouteText.Build("x", new List<FileAttachment> { pdf }));
        }
    }
}
