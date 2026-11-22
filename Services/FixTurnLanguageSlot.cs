#nullable enable
// AI-511: one-shot hand-off of the PANE language from the JKR "Fix via Copilot" launcher to the turn it starts.
//
// A normal chat turn sends lang = the language detected from the typed words. A Fix prompt is mostly English tool words
// (set_parameters, Type Name, ...), so the detected language is unreliable, and the cloud wrote ask_user in the wrong one.
// The launcher arms the slot with the prompt text and the pane language; the router takes it for THAT text only (TakeFor),
// so any other message keeps its detected language. The launcher clears the slot when its sequence ends.

using System;

namespace RevitWebAppSync.Services
{
    public static class FixTurnLanguageSlot
    {
        private static readonly object Gate = new object();
        private static string? _text;
        private static bool _ms;

        public static void Arm(string? promptText, bool ms)
        {
            var text = (promptText ?? "").Trim();
            lock (Gate)
            {
                if (text.Length == 0) { _text = null; return; }
                _text = text; _ms = ms;
            }
        }

        public static bool IsArmed { get { lock (Gate) return _text != null; } }

        public static void Clear() { lock (Gate) _text = null; }

        /// <summary>True (Malay) or false (English) when <paramref name="message"/> is the armed prompt; consumed. Otherwise null.</summary>
        public static bool? TakeFor(string? message)
        {
            var m = (message ?? "").Trim();
            lock (Gate)
            {
                if (_text == null || !m.StartsWith(_text, StringComparison.Ordinal)) return null;
                _text = null;
                return _ms;
            }
        }
    }
}
