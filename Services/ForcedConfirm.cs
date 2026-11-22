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

        /// <summary>True while the current turn must show the card.</summary>
        public static bool IsActive => _active;

        /// <summary>Launcher call: the NEXT ChatSend is a forced-confirm turn.</summary>
        public static void ArmForNextTurn() { _armed = true; }

        /// <summary>Called at the start of every ChatSend. Consumes the arm:
        /// the turn is forced only if it was armed; a normal message resets.</summary>
        public static void BeginTurn()
        {
            _active = _armed;
            _armed = false;
        }

        /// <summary>Turn is over (final reply, cancel, clear, failure).</summary>
        public static void EndTurn()
        {
            _active = false;
            _armed = false;
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
