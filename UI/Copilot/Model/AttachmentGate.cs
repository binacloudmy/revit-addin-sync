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
                [".dwg"] = AttachmentKind.Dwg,
                [".dxf"] = AttachmentKind.Dwg,
                [".pdf"] = AttachmentKind.Pdf,
            };

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

            return "Supported files|" + Group(AttachmentKind.Text, AttachmentKind.Dwg, AttachmentKind.Pdf)
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
