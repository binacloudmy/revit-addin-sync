#nullable enable
// The pane's own words in the drafter's language (Malay or English, never
// both). Revit-free so the router, the turn client and the tests share it.
//
//   - Turn failures: the client names WHY a turn failed (TurnFailureReasons);
//     the router turns the reason into a short line in the turn's language.
//     Raw HTTP bodies and exception names never reach the drafter: they are
//     logged instead.
//   - Progress rows: a local label by progress kind, unless the cloud's own
//     text is already in the turn's language.
//   - ReplySplit: the streamed reply cut where a confirm card froze the text
//     above it.

using System;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Why a turn failed or was refused (TurnOutcome.Reason).</summary>
    public static class TurnFailureReasons
    {
        public const string HostNotAllowed = "host_not_allowed";
        public const string Unreachable = "unreachable";
        public const string ResultsUndelivered = "results_undelivered";
        public const string StreamLost = "stream_lost";
        public const string Cancelled = "cancelled";
        public const string NotActive = "not_active";
        public const string ForeignToolRequest = "foreign_tool_request";
        public const string BadFrame = "bad_frame";
        public const string UpdateRequired = "update_required";
        public const string TurnActive = "turn_active";
        public const string SignedOut = "signed_out";
        /// <summary>A non-200 answer the client has no special meaning for (TurnOutcome.HttpStatus says which).</summary>
        public const string Http = "http_error";
    }

    public static class CopilotPaneText
    {
        private static string L(ReceiptLanguage lang, string ms, string en) => lang == ReceiptLanguage.Ms ? ms : en;

        /// <summary>The first step of every turn leg, and the placeholder bubble before it.</summary>
        public static string Thinking(ReceiptLanguage lang) => L(lang, "Berfikir…", "Thinking…");

        public static string CouldNotFinish(ReceiptLanguage lang) =>
            L(lang, "Copilot tidak dapat menyiapkan permintaan ini.", "The copilot could not finish this request.");

        public static string Unexpected(ReceiptLanguage lang) =>
            L(lang, "Copilot berhenti tanpa diduga. Hantar permintaan sekali lagi.", "The copilot stopped unexpectedly. Send the request again.");

        public static string AnswerNotSent(ReceiptLanguage lang) =>
            L(lang, "Jawapan tidak dapat dihantar. Cuba lagi.", "The answer could not be sent. Try again.");

        public static string NoAnswer(ReceiptLanguage lang) =>
            L(lang, "Copilot tidak menjawab. Tiada apa yang diubah dalam model.", "The copilot did not answer. Nothing was changed in the model.");

        /// <summary>A failed turn's line for the drafter, from the client's reason; null
        /// when the reason is unknown (the caller keeps the client's own message).</summary>
        public static string? Failure(string? reason, int? httpStatus, ReceiptLanguage lang)
        {
            switch (reason)
            {
                case TurnFailureReasons.HostNotAllowed:
                    return L(lang, "Binaan add-in ini hanya berhubung dengan pelayan BINA; alamat copilot yang ditetapkan bukan salah satunya.",
                        "This add-in build only talks to BINA's own servers; the configured copilot address is not one of them.");
                case TurnFailureReasons.Unreachable:
                    return L(lang, "BINA tidak dapat dihubungi. Semak sambungan dan hantar semula.",
                        "BINA could not be reached. Check the connection and send again.");
                case TurnFailureReasons.ResultsUndelivered:
                    return L(lang, "BINA tidak dapat dihubungi untuk melaporkan apa yang dijalankan dalam Revit. Senarai Undo Revit menunjukkan apa yang berubah.",
                        "BINA could not be reached to report what ran in Revit. Revit's own record (Undo list) shows what changed.");
                case TurnFailureReasons.StreamLost:
                    return L(lang, "Sambungan ke BINA terputus dan tidak dapat disambung semula. Hantar permintaan sekali lagi.",
                        "The connection to BINA dropped and could not be resumed. Send the request again.");
                case TurnFailureReasons.Cancelled:
                    return L(lang, "Giliran ini telah dibatalkan.", "The turn was cancelled.");
                case TurnFailureReasons.NotActive:
                    return L(lang, "Giliran itu tidak lagi aktif. Hantar permintaan sekali lagi.",
                        "That turn is no longer active. Send the request again.");
                case TurnFailureReasons.ForeignToolRequest:
                    return L(lang, "Permintaan alat tiba untuk giliran yang tidak dijalankan oleh panel ini; ia tidak dilaksanakan.",
                        "A tool request arrived for a turn this pane is not running; it was not executed.");
                case TurnFailureReasons.BadFrame:
                    return L(lang, "BINA menghantar sesuatu yang tidak dapat dibaca oleh add-in; tiada apa yang dijalankan.",
                        "BINA sent something the add-in could not read; nothing ran.");
                case TurnFailureReasons.UpdateRequired:
                    return L(lang, "Copilot BINA memerlukan add-in yang lebih baharu. Kemas kini BINA Sync untuk terus menggunakannya.",
                        "BINA's copilot needs a newer add-in. Update BINA Sync to keep using it.");
                case TurnFailureReasons.TurnActive:
                    return L(lang, "Permintaan lain masih berjalan dalam sembang ini. Tunggu atau tekan Stop.",
                        "Another request is still running in this chat. Wait for it or press Stop.");
                case TurnFailureReasons.SignedOut:
                    return L(lang, "Log masuk BINA AI anda telah tamat. Log masuk semula untuk meneruskan.",
                        "Your BINA AI sign-in has expired. Sign in again to continue.");
                case TurnFailureReasons.Http:
                    return HttpFailure(httpStatus, lang);
                default:
                    return null;
            }
        }

        /// <summary>A short line for a non-200 answer; never the response body.</summary>
        public static string HttpFailure(int? status, ReceiptLanguage lang)
        {
            var s = status ?? 0;
            if (s == 413)
                return L(lang, "Permintaan terlalu besar untuk BINA. Lampirkan fail yang lebih sedikit atau lebih kecil dan hantar semula.",
                    "The request is too large for BINA. Attach fewer or smaller files and send again.");
            if (s == 429)
                return L(lang, "BINA sedang sibuk. Tunggu sebentar dan hantar semula.", "BINA is busy right now. Wait a moment and send again.");
            if (s >= 500)
                return L(lang, "BINA menghadapi masalah di pihaknya. Tunggu sebentar dan hantar semula.",
                    "BINA had a problem on its side. Wait a moment and send again.");
            if (s == 404)
                return L(lang, "BINA tidak lagi menyimpan giliran ini. Hantar permintaan sekali lagi.",
                    "BINA no longer has this turn. Send the request again.");
            return L(lang, "BINA tidak dapat menerima permintaan ini. Hantar semula; jika masih gagal, kemas kini BINA Sync.",
                "BINA could not accept this request. Send it again; if it keeps failing, update BINA Sync.");
        }

        /// <summary>The add-in's own label for a progress kind; null for a kind it does not know.</summary>
        public static string? ProgressKindLabel(string? kind, ReceiptLanguage lang) => kind switch
        {
            ProgressKinds.Thinking => Thinking(lang),
            ProgressKinds.Running => L(lang, "Sedang berjalan…", "Working…"),
            ProgressKinds.AwaitingConfirm => L(lang, "Menunggu pengesahan anda", "Waiting for your confirmation"),
            ProgressKinds.RevitBusy => L(lang, "Revit sedang sibuk", "Revit is busy"),
            _ => null,
        };

        /// <summary>A progress row's label: the cloud's text when it is already in the
        /// turn's language, else the local label for its kind (the cloud's text, or
        /// the kind, when the kind is unknown).</summary>
        public static string ProgressLabel(string? kind, string? cloudText, ReceiptLanguage lang)
        {
            var text = (cloudText ?? "").Trim();
            var local = ProgressKindLabel(kind, lang);
            if (text.Length > 0 && (local == null || ReceiptTemplates.Detect(text) == lang)) return text;
            return local ?? kind ?? "";
        }
    }

    /// <summary>The streamed reply cut at what is already shown above a confirm card.</summary>
    public static class ReplySplit
    {
        /// <summary>The text after the first <paramref name="shown"/> characters,
        /// without the separator that starts it.</summary>
        public static string After(string? full, int shown)
        {
            var s = full ?? "";
            if (shown <= 0) return s;
            return s.Length <= shown ? "" : s.Substring(shown).TrimStart();
        }
    }
}
