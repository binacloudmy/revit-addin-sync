// AI-501: forced Ya/Tidak card for turns started from Fix via Copilot.
// Pure logic — the VM wiring itself (WPF) is not unit-testable on the Mac harness.

using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    [Collection("ForcedConfirmStatic")]
    public class ForcedConfirmTests
    {
        public ForcedConfirmTests() { ForcedConfirm.EndTurn(); }

        [Fact]
        public void AutoOn_forced_shows_card()
            => Assert.False(ForcedConfirm.ShouldAutoApprove(true, true, forced: true));

        [Fact]
        public void AutoOn_not_forced_still_auto_approves()
            => Assert.True(ForcedConfirm.ShouldAutoApprove(true, true, forced: false));

        [Fact]
        public void AutoOff_never_auto_approves()
        {
            Assert.False(ForcedConfirm.ShouldAutoApprove(false, true, false));
            Assert.False(ForcedConfirm.ShouldAutoApprove(false, true, true));
        }

        [Fact]
        public void Mixed_batch_not_auto_approvable_unchanged()
            => Assert.False(ForcedConfirm.ShouldAutoApprove(true, false, false));

        [Fact]
        public void Codegen_card_forced_even_when_auto_and_clean()
        {
            Assert.False(ForcedConfirm.ShouldShowCodeCard(true, false, forced: false));
            Assert.True(ForcedConfirm.ShouldShowCodeCard(true, false, forced: true));
        }

        [Fact]
        public void Armed_turn_is_forced_and_stays_until_ended()
        {
            ForcedConfirm.ArmForNextTurn();
            Assert.False(ForcedConfirm.IsActive);   // arming alone changes nothing
            ForcedConfirm.BeginTurn();
            Assert.True(ForcedConfirm.IsActive);
            ForcedConfirm.EndTurn();
            Assert.False(ForcedConfirm.IsActive);
        }

        [Fact]
        public void Flag_applies_to_one_turn_only()
        {
            ForcedConfirm.ArmForNextTurn();
            ForcedConfirm.BeginTurn();
            Assert.True(ForcedConfirm.IsActive);
            ForcedConfirm.BeginTurn();              // next normal chat message
            Assert.False(ForcedConfirm.IsActive);
        }

        [Fact]
        public void Cancel_clears_armed_and_active()
        {
            ForcedConfirm.ArmForNextTurn();
            ForcedConfirm.EndTurn();
            ForcedConfirm.BeginTurn();
            Assert.False(ForcedConfirm.IsActive);
        }

        [Fact]
        public void Decline_rejects_every_pending_call()
        {
            var batch = new List<PendingToolCall>
            {
                new PendingToolCall { ToolCallId = "a", Tool = "set_parameter", Mutate = true },
                new PendingToolCall { ToolCallId = "b", Tool = "list_views", Mutate = false },
            };
            var results = batch.Select(ConfirmGate.Rejected).ToList();
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Ok));
            Assert.Equal(new[] { "a", "b" }, results.Select(r => r.ToolCallId));
            Assert.All(results, r => Assert.Equal("rejected", ((Dictionary<string, object>)r.Result)["status"]));
        }
    }
}
