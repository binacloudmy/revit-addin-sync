// ForcedConfirm — one-turn flag that makes the Copilot pane ALWAYS show the
// Ya/Tidak card, even when CopilotPrefs.AutoApproveWrites is on. Set by the
// JKR "Fix via Copilot" launcher; normal chats never touch it.
//
// Where the gate lives: the backend tags each pending call with `mutate` and
// `requires_confirmation`; the pane decides whether to skip the card
// (CopilotViewModel.RenderRouteResult). Skipping is a client-only decision, so
// forcing the card is a client-only change — no bina-ai change needed.
//
// Lifecycle (single pane, UI thread; fields are volatile for safety only):
//   Arm()      launcher, right before it sends the prompt
//   BeginTurn  ChatSend start: armed -> active; any other send -> inactive
//   EndTurn    turn finished / cancelled / chat cleared / new message
// Active spans the card round-trip (Ya/Tidak resumes the SAME turn).

namespace RevitWebAppSync.Services
{
    public static class ForcedConfirm
    {
        private static volatile bool _armed;
        private static volatile bool _active;
        private static volatile bool _resume;

        /// <summary>Armed, waiting for the next ChatSend (tests/diagnostics).</summary>
        public static bool IsArmed => _armed;

        /// <summary>True while the current turn must show the card.</summary>
        public static bool IsActive => _active;

        /// <summary>Launcher call: the NEXT ChatSend is a forced-confirm turn.</summary>
        public static void ArmForNextTurn() { _armed = true; }

        /// <summary>Consume the arm without starting a turn (ChatSend takes it first, begins the turn
        /// only after its early returns).</summary>
        public static bool TakeArm() { var a = _armed; _armed = false; return a; }

        /// <summary>The run parked on a clarify question: keep the turn forced for the user's answer
        /// (it resumes the same run through ChatSend with no arm).</summary>
        public static void PauseForAnswer() { _resume = _active; }

        /// <summary>A turn starts. Forced if armed; an answer to a paused forced run keeps it; any other
        /// message resets it.</summary>
        public static void BeginTurn(bool armed)
        {
            if (armed) _active = true;
            else if (!_resume) _active = false;
            _resume = false;
            _armed = false;
        }

        /// <summary>Test/diagnostic shortcut: take the arm and begin.</summary>
        public static void BeginTurn() => BeginTurn(TakeArm());

        /// <summary>Turn is over (final reply, cancel, clear, failure).</summary>
        public static void EndTurn()
        {
            _active = false;
            _armed = false;
            _resume = false;
        }

        /// <summary>Auto mode may skip the card only when the batch opted out
        /// of confirmation AND this turn is not forced.</summary>
        public static bool ShouldAutoApprove(bool autoApproveWrites, bool batchAutoApprovable, bool forced)
            => !forced && autoApproveWrites && batchAutoApprovable;

        /// <summary>Codegen gate: card shown unless Auto + clean flag + not forced.</summary>
        public static bool ShouldShowCodeCard(bool autoApproveWrites, bool codeRequiresConfirmation, bool forced)
            => forced || !autoApproveWrites || codeRequiresConfirmation;
    }
}
