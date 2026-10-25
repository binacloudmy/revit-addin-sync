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
