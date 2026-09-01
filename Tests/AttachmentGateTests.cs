// AttachmentGate — the rules that decide whether an attached file can be used.
//
// These exist because the old behaviour was a bare `continue` in three places:
// a rejected file left NO trace — no chip, no prompt block, nothing — so the
// drafter's turn was answered as though they had never attached anything. The
// gate's contract is "always return a reason", and that is what is pinned here.

using System;
using System.IO;
using System.Text;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class AttachmentGateTests : IDisposable
    {
        private readonly string _dir;

        public AttachmentGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "bina_gate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        /// <summary>Write a file of exactly <paramref name="bytes"/> bytes.</summary>
        private string Make(string name, int bytes = 16)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('a', bytes)));
            return path;
        }

        // ── accepted ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("rules.txt")]
        [InlineData("rules.csv")]
        [InlineData("notes.md")]
        [InlineData("run.log")]
        [InlineData("data.json")]
        [InlineData("data.xml")]
        public void TextExtensions_AreAccepted(string name)
        {
            var r = AttachmentGate.Check(Make(name));
            Assert.True(r.Accepted);
            Assert.Equal(AttachmentKind.Text, r.Kind);
            Assert.Null(r.Reason);
        }

        [Theory]
        [InlineData("plan.dwg", AttachmentKind.Dwg)]
        [InlineData("plan.dxf", AttachmentKind.Dwg)]
        [InlineData("spec.pdf", AttachmentKind.Pdf)]
        public void BinaryExtensions_MapToTheirKind(string name, AttachmentKind kind)
        {
            var r = AttachmentGate.Check(Make(name));
            Assert.True(r.Accepted);
            Assert.Equal(kind, r.Kind);
        }

        [Fact]
        public void ExtensionMatch_IsCaseInsensitive()
        {
            Assert.True(AttachmentGate.Check(Make("RULES.CSV")).Accepted);
        }

        // ── the size cap ──────────────────────────────────────────────────────

        [Fact]
        public void AtTheCap_IsAccepted()
        {
            // Boundary: the check is `>`, so exactly-at-cap must pass.
            var r = AttachmentGate.Check(Make("rules.csv", 100), maxTextBytes: 100);
            Assert.True(r.Accepted);
        }

        [Fact]
        public void OverTheCap_IsRejected_AndNamesBothSizes()
        {
            var r = AttachmentGate.Check(Make("rules.csv", 40 * 1024), maxTextBytes: 32 * 1024);
            Assert.False(r.Accepted);
            Assert.Null(r.Kind);
            // Actionable: the drafter must be able to see how far over they are.
            Assert.Contains("40 KB", r.Reason);
            Assert.Contains("32 KB", r.Reason);
        }

        [Fact]
        public void TheCapDoesNotApplyToBinaryKinds()
        {
            // A DWG or PDF is read by the addin from the path — nothing but the
            // path leaves the pane — so a 5 MB drawing is fine.
            var r = AttachmentGate.Check(Make("plan.dwg", 200), maxTextBytes: 100);
            Assert.True(r.Accepted);
            Assert.Equal(AttachmentKind.Dwg, r.Kind);
        }

        // ── rejected ──────────────────────────────────────────────────────────

        [Fact]
        public void UnsupportedExtension_IsRejected_AndNamesIt()
        {
            // The message has to say WHICH extension failed — "unsupported file"
            // leaves the drafter guessing which of their attachments was dropped.
            var r = AttachmentGate.Check(Make("report.docx"));
            Assert.False(r.Accepted);
            Assert.Contains(".docx", r.Reason);
        }

        [Fact]
        public void NoExtension_IsRejected()
        {
            var r = AttachmentGate.Check(Make("rules"));
            Assert.False(r.Accepted);
            Assert.NotNull(r.Reason);
        }

        [Fact]
        public void MissingFile_IsRejected()
        {
            var r = AttachmentGate.Check(Path.Combine(_dir, "nope.csv"));
            Assert.False(r.Accepted);
            Assert.Contains("tidak dijumpai", r.Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyPath_IsRejected(string path)
        {
            var r = AttachmentGate.Check(path);
            Assert.False(r.Accepted);
            Assert.NotNull(r.Reason);
        }

        [Fact]
        public void EveryRejection_CarriesAReason()
        {
            // The whole point of the gate: never a silent drop.
            foreach (var path in new[] { Make("report.docx"), Make("rules"), Path.Combine(_dir, "gone.csv") })
            {
                var r = AttachmentGate.Check(path);
                Assert.False(r.Accepted);
                Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            }
        }

        // ── the dialog filter is generated, not hand-kept ─────────────────────

        [Fact]
        public void DialogFilter_OffersEverySupportedExtension()
        {
            // If these ever diverge, an extension becomes selectable but unusable
            // (or usable but unofferable). Generating the filter is what prevents
            // it; this pins that it stays generated.
            foreach (var ext in AttachmentGate.Supported.Keys)
                Assert.Contains("*" + ext.ToLowerInvariant(), AttachmentGate.DialogFilter);
        }

        [Fact]
        public void Xlsx_IsSupported_AndOffered()
        {
            // Excel is what drafters actually have. Before this it was not in the
            // map at all, so dragging one onto the composer dropped it in silence.
            Assert.True(AttachmentGate.Supported.ContainsKey(".xlsx"));
            Assert.Contains("*.xlsx", AttachmentGate.DialogFilter);
        }

        [Fact]
        public void Xlsx_SkipsTheByteCap_BecauseItIsCompressed()
        {
            // An .xlsx is a ZIP: 32 KB on disk can be hundreds of KB of text, and
            // a 40 KB workbook can be a handful of rows. Gating on file size would
            // be wrong in both directions — XlsxText caps the OUTPUT instead.
            var r = AttachmentGate.Check(Make("rules.xlsx", 40 * 1024), maxTextBytes: 32 * 1024);
            Assert.True(r.Accepted);
            Assert.Equal(AttachmentKind.Text, r.Kind);
            Assert.True(AttachmentGate.NeedsConversion("book.xlsx"));
            Assert.False(AttachmentGate.NeedsConversion("rules.csv"));
        }

        // ── size formatting ───────────────────────────────────────────────────

        [Theory]
        [InlineData(0, "0 KB")]
        [InlineData(1, "1 KB")]          // never "0 KB" for a real file
        [InlineData(1024, "1 KB")]
        [InlineData(32 * 1024, "32 KB")]
        [InlineData(40 * 1024, "40 KB")]
        public void Kb_ReadsLikeAHuman(long bytes, string expected) =>
            Assert.Equal(expected, AttachmentGate.Kb(bytes));
    }
}
