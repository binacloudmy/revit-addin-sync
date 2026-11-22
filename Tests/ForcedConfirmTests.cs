// AI-501: forced Ya/Tidak card for turns started from Fix via Copilot.
// Pure logic — the VM wiring itself (WPF) is not unit-testable on the Mac harness.

using System;
using System.Collections.Generic;
using System.IO;
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

        // Turn lifecycle: early-return, sign-in park and clarify resume (review fixes).
        [Fact]
        public void Take_arm_without_begin_does_not_start_a_turn()
        {
            ForcedConfirm.ArmForNextTurn();
            var armed = ForcedConfirm.TakeArm();   // ChatSend took it, then hit an early return
            Assert.True(armed);
            Assert.False(ForcedConfirm.IsArmed);
            Assert.False(ForcedConfirm.IsActive);
        }

        [Fact]
        public void Parked_prompt_rearms_and_the_resent_turn_is_forced()
        {
            ForcedConfirm.ArmForNextTurn();
            bool forcedPayload = ForcedConfirm.TakeArm();   // stored on the PromptPayload at the sign-in park
            ForcedConfirm.EndTurn();
            if (forcedPayload) ForcedConfirm.ArmForNextTurn();   // OnSessionChanged re-send
            ForcedConfirm.BeginTurn();
            Assert.True(ForcedConfirm.IsActive);
        }

        [Fact]
        public void Clarify_pause_keeps_the_turn_forced_for_the_answer()
        {
            ForcedConfirm.ArmForNextTurn();
            ForcedConfirm.BeginTurn();
            ForcedConfirm.PauseForAnswer();   // RenderRouteResult on a clarify result
            ForcedConfirm.BeginTurn();        // the user's answer through ChatSend, no arm
            Assert.True(ForcedConfirm.IsActive);
            ForcedConfirm.BeginTurn();        // the next, unrelated message
            Assert.False(ForcedConfirm.IsActive);
        }

        [Fact]
        public void Cancel_during_a_clarify_pause_ends_the_forced_turn()
        {
            ForcedConfirm.ArmForNextTurn();
            ForcedConfirm.BeginTurn();
            ForcedConfirm.PauseForAnswer();
            ForcedConfirm.EndTurn();
            ForcedConfirm.BeginTurn();
            Assert.False(ForcedConfirm.IsActive);
        }

        [Fact]
        public void Pause_without_a_forced_turn_changes_nothing()
        {
            ForcedConfirm.PauseForAnswer();
            ForcedConfirm.BeginTurn();
            Assert.False(ForcedConfirm.IsActive);
        }

        // Source guard: every code read of AutoApproveWrites outside the settings UI must sit next to a
        // ForcedConfirm call, so a new auto-approve path (e.g. PR #307's engine-mode gate) cannot skip the flag.
        [Fact]
        public void Every_AutoApproveWrites_read_goes_through_ForcedConfirm()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var allowed = new[] { "CopilotPrefs.cs", "SettingsView.xaml.cs", "PromptBar.xaml.cs", "ForcedConfirm.cs" };
            var offenders = new List<string>();
            foreach (var top in new[] { "UI", "Services", "BinaVibe" })
            {
                var root = Path.Combine(dir!.FullName, top);
                if (!Directory.Exists(root)) continue;
                foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                    if (Array.IndexOf(allowed, Path.GetFileName(f)) >= 0) continue;
                    var lines = File.ReadAllLines(f);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].Contains("AutoApproveWrites") || lines[i].TrimStart().StartsWith("//")) continue;
                        bool near = false;
                        for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 3); j++)
                            if (lines[j].Contains("ForcedConfirm")) near = true;
                        if (!near) offenders.Add(Path.GetFileName(f) + ":" + (i + 1));
                    }
                }
            }
            Assert.True(offenders.Count == 0, "AutoApproveWrites read without ForcedConfirm nearby: " + string.Join(", ", offenders));
        }
    }
}
