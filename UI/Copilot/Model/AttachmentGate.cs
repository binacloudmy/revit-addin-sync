// AttachmentGate — decides whether an attached file can be used, and if not, WHY.
//
// Why this exists: AddFiles used to drop files on the floor with a bare
// `continue` in three places — unsupported extension, missing file, over the size
// cap. No chip, no message, nothing in the prompt. The drafter attached a file,
// sent their turn, and the model answered having never seen it. That is
// indistinguishable from "the AI ignored me", and it is the worst possible
// failure for any feature whose INPUT is the attachment (a rule table, a spec).
//
// Two entry points both land here, and neither filtered before:
//   * the Attach button's OpenFileDialog — its Filter is advisory; Windows lets
//     the user type *.* in the filename box and pick anything;
//   * drag-and-drop (MentionInput.FileDropped) — raw DataFormats.FileDrop with
//     NO extension check at all, so dropping an .xlsx today vanishes silently.
//
// The decision lives here rather than in PromptBar so it can be unit-tested
// without WPF — same reason UsageState was extracted. PromptBar keeps only the
// rendering.
//
// Reasons are drafter-facing and Malay-first, matching the rest of the pane, and
// they always NAME THE LIMIT so the message is actionable ("terlalu besar — 40 KB,
// had 32 KB", not "file too large").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Outcome of gating one attachment: a kind when usable, otherwise a
    /// drafter-readable reason. Never both.</summary>
    public readonly struct GateResult
    {
        public readonly AttachmentKind? Kind;
        public readonly string Reason;

        private GateResult(AttachmentKind? kind, string reason) { Kind = kind; Reason = reason; }

        public bool Accepted => Kind.HasValue;

        public static GateResult Accept(AttachmentKind kind) => new GateResult(kind, null);
        public static GateResult Reject(string reason) => new GateResult(null, reason);
    }

    public static class AttachmentGate
    {
        /// <summary>Cap for files read as TEXT. Binary kinds (DWG/PDF) are read by
        /// the addin from the path, so no cap applies to them.</summary>
        public const long MaxTextBytes = 32 * 1024;

        // ── Images (AI-339): .png / .jpg / .jpeg attach like a pasted screenshot ────────────────────────────────────
        // They go into the SAME images list as Ctrl+V (not into the files list), so the limits are the same: at most
        // MaxImages per message, the long edge cut to MaxImageEdge before encoding. Nothing here reads a file.

        /// <summary>Images per message (pasted and attached together).</summary>
        public const int MaxImages = 3;
        /// <summary>The long edge of an image, in pixels, after it is cut down.</summary>
        public const int MaxImageEdge = 1568;
        /// <summary>An image file bigger than this is refused before it is decoded (a 4K PNG screenshot is well under it).</summary>
        public const long MaxImageFileBytes = 25L * 1024 * 1024;

        public static readonly IReadOnlyCollection<string> ImageExtensions = new[] { ".png", ".jpg", ".jpeg" };

        /// <summary>True for a path with an image extension the composer takes.</summary>
        public static bool IsImage(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var ext = Path.GetExtension(path);
            foreach (var e in ImageExtensions)
                if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The size an image is encoded at: the long edge at most <see cref="MaxImageEdge"/>, the shape kept, never larger than the
        /// original. Zero or negative sizes come back as 1 x 1.</summary>
        public static (int Width, int Height) FitSize(int width, int height, int maxEdge = MaxImageEdge)
        {
            if (width <= 0 || height <= 0) return (1, 1);
            var longEdge = Math.Max(width, height);
            if (longEdge <= maxEdge) return (width, height);
            var scale = (double)maxEdge / longEdge;
            return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        /// <summary>Why an image file was not added, in the pane's voice (the chip shows it); null when it can be added.
        /// <paramref name="already"/> is how many images the message holds.</summary>
        public static string? ImageRefusal(long fileBytes, int already)
        {
            if (already >= MaxImages)
                return "maksimum " + MaxImages + " imej sebuah mesej (buang satu dahulu)";
            if (fileBytes > MaxImageFileBytes)
                return "imej terlalu besar (" + (fileBytes / (1024 * 1024)) + " MB; had " + (MaxImageFileBytes / (1024 * 1024)) + " MB)";
            return null;
        }

        /// <summary>The line that tells the drafter screenshots can be pasted (the composer's hint and the Attach button's tooltip).</summary>
        public const string PasteHint = "Ctrl+V pastes a screenshot";

        /// <summary>The ONE list of what can be attached. The dialog filter is
        /// derived from it below so the two can never drift — a mismatch is how an
        /// extension ends up selectable but silently unusable.</summary>
        public static readonly IReadOnlyDictionary<string, AttachmentKind> Supported =
            new Dictionary<string, AttachmentKind>(StringComparer.OrdinalIgnoreCase)
            {
                [".txt"] = AttachmentKind.Text,
                [".csv"] = AttachmentKind.Text,
                [".md"] = AttachmentKind.Text,
                [".log"] = AttachmentKind.Text,
                [".json"] = AttachmentKind.Text,
                [".xml"] = AttachmentKind.Text,
                [".xlsx"] = AttachmentKind.Text,   // converted to CSV at attach time — see ConvertedText
                [".dwg"] = AttachmentKind.Dwg,
                [".dxf"] = AttachmentKind.Dwg,
                [".pdf"] = AttachmentKind.Pdf,
            };

        /// <summary>Text kinds that are not readable as text on disk and must be
        /// CONVERTED first (XlsxText). The byte cap is deliberately NOT applied to
        /// these: an .xlsx is a compressed ZIP, so file size says nothing useful
        /// about how much text it becomes. The cap is enforced on the conversion
        /// output instead.</summary>
        private static readonly HashSet<string> _convertedText =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xlsx" };

        public static IReadOnlyCollection<string> ConvertedText => _convertedText;

        public static bool NeedsConversion(string path)
        {
            var ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && _convertedText.Contains(ext);
        }

        /// <summary>OpenFileDialog filter, GENERATED from <see cref="Supported"/> —
        /// not hand-written. Add an extension to the map above and it appears in
        /// the dialog automatically; that is the point, because a hand-kept second
        /// list is exactly how an extension ends up unusable in one place and
        /// offered in the other.</summary>
        public static readonly string DialogFilter = BuildDialogFilter();

        private static string BuildDialogFilter()
        {
            string Group(params AttachmentKind[] kinds)
            {
                var exts = new List<string>();
                foreach (var pair in Supported)
                    if (Array.IndexOf(kinds, pair.Value) >= 0)
                        exts.Add("*" + pair.Key.ToLowerInvariant());
                exts.Sort(StringComparer.Ordinal);
                return string.Join(";", exts);
            }

            var images = string.Join(";", ImageExtensions.Select(e => "*" + e).OrderBy(e => e, StringComparer.Ordinal));
            return "Supported files|" + Group(AttachmentKind.Text, AttachmentKind.Dwg, AttachmentKind.Pdf) + ";" + images
                 + "|Images|" + images
                 + "|Text files|" + Group(AttachmentKind.Text)
                 + "|Drawings|" + Group(AttachmentKind.Dwg)
                 + "|Documents|" + Group(AttachmentKind.Pdf);
        }

        /// <summary>Gate one path. <paramref name="maxTextBytes"/> is injectable so
        /// tests do not have to write 32 KB of bytes to disk.</summary>
        public static GateResult Check(string path, long maxTextBytes = MaxTextBytes)
        {
            if (string.IsNullOrWhiteSpace(path))
                return GateResult.Reject("laluan fail kosong");

            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
                return GateResult.Reject("jenis fail tidak disokong (tiada sambungan fail)");

            if (!Supported.TryGetValue(ext, out var kind))
                return GateResult.Reject($"jenis fail tidak disokong ({ext.ToLowerInvariant()})");

            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception ex) { return GateResult.Reject($"fail tidak boleh dibaca: {ex.Message}"); }

            if (!info.Exists)
                return GateResult.Reject("fail tidak dijumpai");

            // Binary kinds never get read here — only the path travels — so the
            // text cap is irrelevant to them.
            if (kind != AttachmentKind.Text)
                return GateResult.Accept(kind);

            // Converted kinds (.xlsx) are compressed: the file size does not
            // predict the text size, so the cap is applied after conversion.
            if (NeedsConversion(path))
                return GateResult.Accept(kind);

            if (info.Length > maxTextBytes)
                return GateResult.Reject(
                    $"terlalu besar — {Kb(info.Length)}, had {Kb(maxTextBytes)}");

            return GateResult.Accept(kind);
        }

        /// <summary>Sizes read to a drafter, not to a machine: whole KB, and never
        /// "0 KB" for a small-but-real file.</summary>
        public static string Kb(long bytes)
        {
            if (bytes <= 0) return "0 KB";
            var kb = (long)Math.Round(bytes / 1024.0);
            return (kb < 1 ? 1 : kb) + " KB";
        }
    }
}
