#nullable enable
// Drafter-facing receipt, evidence and caution text in English and Malay
// (lean-harness spec §7.11; W5 Task 3). Revit-free. Every sentence is a
// template over structured fields from TurnEvidence; nothing here reads or
// rewrites the model's reply.
//
//   Headline  "30/34 done · 4 failed (not owned)"   (always as a fraction)
//             "30/34 siap · 4 gagal (milik pengguna lain)"
//   Evidence  "34 found · searched host · Ceilings · Level = 'Level 2' · 34 selected, 30 visible in view"
//   Not searched (shown on its own, in the warning colour)
//   Caution   shown whenever any write in the turn is not done

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ReceiptTemplates
    {
        private const string Sep = " · ";

        public static string Headline(ReceiptFacts f, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var parts = new List<string> { f.Done + "/" + f.Total + (ms ? " siap" : " done") };
            if (f.Unchanged > 0) parts.Add(f.Unchanged + (ms ? " tiada perubahan" : " unchanged"));
            foreach (var g in f.Failed.Where(g => g.Count > 0))
                parts.Add(g.Count + (ms ? " gagal (" : " failed (") + Reason(g.Reason, lang) + ")");
            if (f.Declined > 0) parts.Add(f.Declined + (ms ? " tidak dilaksanakan (ditolak)" : " not applied (declined)"));
            return string.Join(Sep, parts);
        }

        /// <summary>An attached table that was cut to its first rows (review M5).</summary>
        public static string TableCut(string name, int sent, int total, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms
                ? "Hanya " + sent + " baris pertama daripada " + total + " dalam " + name + " dihantar; baki " + (total - sent) + " baris tidak digunakan."
                : "Only the first " + sent + " of " + total + " rows of " + name + " were sent; the other " + (total - sent) + " rows were not used.";

        public static string Caution(ReceiptFacts f, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms
                ? "Bukan semua perubahan dalam giliran ini dilaksanakan. Semak resit sebelum bergantung pada jawapan."
                : "Not every change in this turn was applied. Check the receipt before relying on the reply.";

        public static string EvidenceLine(EvidenceItem e, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var parts = new List<string>();
            if (e.Count is int n) parts.Add(n + (ms ? " dijumpai" : " found"));
            if (e.Sources.Count > 0) parts.Add((ms ? "dicari " : "searched ") + string.Join(", ", e.Sources));
            if (e.Categories.Count > 0) parts.Add(string.Join(", ", e.Categories));
            if (!string.IsNullOrWhiteSpace(e.Filter)) parts.Add(e.Filter!.Trim());
            if (e.Selected is int sel)
                parts.Add(e.VisibleInView is int vis
                    ? (ms ? sel + " dipilih, " + vis + " kelihatan dalam pandangan" : sel + " selected, " + vis + " visible in view")
                    : (ms ? sel + " dipilih" : sel + " selected"));
            return string.Join(Sep, parts);
        }

        /// <summary>One rule of a check table: "L3-12 · JKR 4.2 — 40 fail · 120 pass ·
        /// 3 not verified (link unloaded)". Not verified is never a pass.</summary>
        public static string CheckRuleLine(ResultTable t, int row, ReceiptLanguage lang)
        {
            if (t == null || row < 0 || row >= t.Rows.Count) return "";
            var ms = lang == ReceiptLanguage.Ms;
            string Cell(int i) => i < t.Rows[row].Count ? t.Rows[row][i] ?? "" : "";
            var head = Cell(0) + (Cell(1).Length > 0 ? Sep + Cell(1) : "");
            var counts = new List<string>
            {
                Cell(4) + (ms ? " gagal" : " fail"),
                Cell(3) + (ms ? " lulus" : " pass"),
            };
            if (Cell(5) != "0" && Cell(5).Length > 0)
                counts.Add(Cell(5) + (ms ? " tidak disahkan" : " not verified") + (Cell(6).Length > 0 ? " (" + Cell(6) + ")" : ""));
            return head + " — " + string.Join(Sep, counts);
        }

        /// <summary>M-12: what a turn leaves in local history. Reply is exactly the
        /// model's text, empty when it wrote none (never the add-in's "Done"). Note is
        /// the add-in's own line, kept apart and labelled: the pane note, then the
        /// receipt ("Receipt: 34/34 done") when the turn wrote; null when neither.</summary>
        public static (string Reply, string? Note) HistoryOf(string? reply, string? paneNote, TurnEvidence? evidence, ReceiptLanguage lang)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(paneNote)) parts.Add(paneNote!.Trim());
            if (evidence != null && evidence.HasWrites)
                parts.Add((lang == ReceiptLanguage.Ms ? "Resit: " : "Receipt: ") + Headline(evidence.Receipt, lang));
            return (reply ?? "", parts.Count > 0 ? string.Join(Sep, parts) : null);
        }

        /// <summary>History written before M-12 stored the add-in's "Done" as the bot
        /// reply: read back, it is the add-in's note, not the model's words.</summary>
        public static bool IsLegacyAddinDone(string? botText) => string.Equals(botText?.Trim(), "Done", StringComparison.Ordinal);

        /// <summary>The label of the add-in's own line in History and exports (N-4).</summary>
        public const string AddinLabel = "BINA add-in";

        /// <summary>N-4: a bot history row as the History tab and the session export show
        /// it. Reply is the model's text, null when it wrote none (no empty bubble).
        /// AddinLine is the add-in's own note or receipt, labelled, or null.</summary>
        public static (string? Reply, string? AddinLine) BotLines(string? text, string? paneNote)
        {
            var legacy = paneNote == null && IsLegacyAddinDone(text);
            var reply = legacy || string.IsNullOrWhiteSpace(text) ? null : text;
            var note = legacy ? text!.Trim() : string.IsNullOrWhiteSpace(paneNote) ? null : paneNote!.Trim();
            return (reply, note == null ? null : AddinLabel + ": " + note);
        }

        /// <summary>The bot row as one block of export text: the reply, then the add-in's line.</summary>
        public static string BotTranscript(string? text, string? paneNote)
        {
            var (reply, addin) = BotLines(text, paneNote);
            return string.Join("\n", new[] { reply, addin }.Where(x => !string.IsNullOrEmpty(x)));
        }

        public static string HighlightLabel(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Serlahkan" : "Highlight";

        /// <summary>Empty when everything was searched.</summary>
        public static string NotSearchedLine(EvidenceItem e, ReceiptLanguage lang) =>
            e.NotSearched.Count == 0
                ? ""
                : (lang == ReceiptLanguage.Ms ? "Tidak dicari: " : "Not searched: ") + string.Join("; ", e.NotSearched);

        public static string Reason(string code, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            switch (code)
            {
                case ErrorCodes.NotOwned: return ms ? "milik pengguna lain" : "not owned";
                case ErrorCodes.DocChanged: return ms ? "dokumen bertukar" : "document changed";
                case ErrorCodes.DiffDrift: return ms ? "berubah sejak pratonton" : "changed since preview";
                case ErrorCodes.RevitBusy: return ms ? "Revit sibuk" : "Revit busy";
                case ErrorCodes.RolledBack: return ms ? "dibatalkan oleh Revit" : "rolled back by Revit";
                case ErrorCodes.PolicyRejected: return ms ? "disekat oleh polisi" : "blocked by policy";
                case ErrorCodes.CompileError: return ms ? "ralat kod" : "code error";
                case ErrorCodes.RuntimeError: return ms ? "ralat dalam Revit" : "error in Revit";
                case ErrorCodes.Timeout: return ms ? "tamat masa" : "timed out";
                case ErrorCodes.NotFound: return ms ? "tidak dijumpai" : "not found";
                case ErrorCodes.InvalidArgs: return ms ? "input tidak sah" : "invalid input";
                case ErrorCodes.NotAllowed: return ms ? "tidak dibenarkan" : "not allowed";
                case ErrorCodes.NotImplemented: return ms ? "tiada dalam versi add-in ini" : "not in this add-in version";
                case ErrorCodes.NoGpu: return ms ? "eksport imej tidak tersedia" : "image export unavailable";
                case ReceiptFacts.Mismatch: return ms ? "bacaan semula tidak sepadan" : "read-back mismatch";
                case EnvelopeStatus.Partial: return ms ? "tidak lengkap" : "incomplete";
                case EnvelopeStatus.Failed: return ms ? "gagal" : "failed";
                default: return string.IsNullOrWhiteSpace(code) ? (ms ? "gagal" : "failed") : code.Replace('_', ' ');
            }
        }

        // ------------------------------------------------------------------
        // Language of the drafter (the prompt they typed)
        // ------------------------------------------------------------------

        // Words common in drafters' Malay requests and rare in English ones.
        private static readonly HashSet<string> MalayWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "yang", "dan", "di", "ke", "dari", "pada", "untuk", "dengan", "ini", "itu", "semua", "setiap",
            "tukar", "naikkan", "turunkan", "ubah", "cari", "tunjuk", "tunjukkan", "senaraikan", "kira", "semak",
            "berapa", "banyak", "mana", "apa", "ada", "tiada", "tak", "tidak", "bukan", "saya", "boleh", "nak",
            "tolong", "sila", "buat", "letak", "padam", "tambah", "kenapa", "bagaimana", "jika", "kalau",
            "pintu", "tingkap", "dinding", "siling", "lantai", "bumbung", "bilik", "aras", "tandas", "tangga",
            "lajur", "rasuk", "paip", "nombor", "nama", "jenis", "pandangan", "helaian", "jadual", "kalis",
        };

        private static readonly Regex Words = new Regex(@"[\p{L}]+", RegexOptions.Compiled);

        /// <summary>Malay when the prompt has at least two Malay words, or one in a
        /// prompt of at most four words; English otherwise.</summary>
        public static ReceiptLanguage Detect(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return ReceiptLanguage.En;
            var words = Words.Matches(text!).Cast<Match>().Select(m => m.Value).ToList();
            var hits = words.Count(w => MalayWords.Contains(w));
            return hits >= 2 || (hits == 1 && words.Count <= 4) ? ReceiptLanguage.Ms : ReceiptLanguage.En;
        }
    }
}
