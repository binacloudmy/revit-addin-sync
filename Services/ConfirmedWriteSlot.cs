#nullable enable
// AI-490: one-shot hand-off of the "confirmed_write" field from the JKR "Fix via Copilot" launcher to the turn it starts.
//
// The launcher arms the slot with the exact prompt text it is about to send and the scope the person said Ya to in the add-in's
// own dialog. The router takes it when it builds the turn for THAT text (TakeFor). Any other text (a normal chat message, a
// queued or retried one) gets null, so a normal turn never carries the field. The launcher clears the slot when its sequence
// ends, so a turn that never started (indexing, queued behind another turn, sign-in park) cannot leak it into a later chat.
// Compat switch: BINA_CONFIRMED_WRITE=0 (or Enabled=false) sends nothing; the server then asks as before.

using System;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.Services
{
    public static class ConfirmedWriteSlot
    {
        private static readonly object Gate = new object();
        private static string? _text;
        private static ConfirmedWriteDto? _dto;
        private static bool? _enabledOverride;

        /// <summary>Send the field at all. Default on; BINA_CONFIRMED_WRITE=0 turns it off when a server without the field is in play.
        /// (An unknown JSON field is ignored by the server, so on is also safe there; off just keeps the body byte-identical to before.)</summary>
        public static bool Enabled
        {
            get => _enabledOverride ?? Environment.GetEnvironmentVariable("BINA_CONFIRMED_WRITE") != "0";
            set => _enabledOverride = value;
        }

        public static void ResetEnabled() => _enabledOverride = null;

        /// <summary>Arm for the turn whose message is <paramref name="promptText"/>. A null dto, empty text or a disabled switch arms nothing.</summary>
        public static void Arm(string? promptText, ConfirmedWriteDto? dto)
        {
            var text = (promptText ?? "").Trim();
            lock (Gate)
            {
                if (dto == null || text.Length == 0 || !Enabled) { _text = null; _dto = null; return; }
                _text = text; _dto = dto;
            }
        }

        public static bool IsArmed { get { lock (Gate) return _dto != null; } }

        public static void Clear() { lock (Gate) { _text = null; _dto = null; } }

        /// <summary>The armed field when <paramref name="message"/> is the armed prompt; consumed (one turn only). Otherwise null, and
        /// the slot is left as it is.</summary>
        public static ConfirmedWriteDto? TakeFor(string? message)
        {
            var m = (message ?? "").Trim();
            lock (Gate)
            {
                if (_dto == null || _text == null || !m.StartsWith(_text, StringComparison.Ordinal)) return null;
                var d = _dto;
                _text = null; _dto = null;
                return d;
            }
        }
    }
}
