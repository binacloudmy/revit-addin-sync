using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-494 (IssueVm / WPF half; not compiled on the Mac harness): the button shows only when a launcher AND a prompt builder are set; no builder is the app default (disabled until the protocol-3 rework).</summary>
    [Collection("CopilotLauncher")]
    public class JkrCopilotFixInstallerTests : IDisposable
    {
        private readonly Func<IssueVm, bool, IReadOnlyList<string>> _savedBuilder = JkrCopilotFixInstaller.PromptBuilder;
        public void Dispose() { JkrCopilotFixInstaller.PromptBuilder = _savedBuilder; JkrCopilotFix.Launcher = null; }

        private sealed class NoSession : ICopilotSession
        {
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsSending => false;
            public CopilotCardState Cards => CopilotCardState.None;
            public bool IsPaneVisible => true;
            public void Send(string text) { }
        }

        private static IssueVm Row()
        {
            var i = new IssueVm { Id = "r1", CheckId = "SPEC-Da.1", SpecCheckId = "Da.1", Title = "Da.1", RevitCategory = "Doors", RevitElementId = 100 };
            i.AddElements(new long[] { 100, 101 }, "Doors · d");
            return i;
        }

        [Fact]
        public void No_prompt_builder_means_no_launcher_and_a_hidden_button()
        {
            JkrCopilotFixInstaller.PromptBuilder = null;
            JkrCopilotFix.Launcher = new JkrCopilotFixLauncher(new NoSession(), _ => new[] { "x" }, _ => { });   // a stale one
            JkrCopilotFixInstaller.Install(new NoSession(), id => Row(), _ => { });
            Assert.Null(JkrCopilotFix.Launcher);
            Assert.False(JkrCopilotFix.LauncherReady);
            Assert.False(Row().CopilotFixable);
        }

        [Fact]
        public void A_prompt_builder_sets_the_launcher_and_shows_the_button()
        {
            JkrCopilotFixInstaller.PromptBuilder = (issue, bm) => new[] { "p" };
            JkrCopilotFixInstaller.Install(new NoSession(), id => Row(), _ => { });
            Assert.NotNull(JkrCopilotFix.Launcher);
            Assert.True(JkrCopilotFix.LauncherReady);
            Assert.True(Row().CopilotFixable);
        }

        [Fact]
        public void A_default_install_leaves_the_button_hidden_until_the_protocol_3_rework()
        {
            JkrCopilotFixInstaller.PromptBuilder = _savedBuilder;   // what the app starts with
            Assert.Null(JkrCopilotFixInstaller.PromptBuilder);
            JkrCopilotFix.Launcher = new JkrCopilotFixLauncher(new NoSession(), _ => new[] { "x" }, _ => { });   // a stale one
            JkrCopilotFixInstaller.Install(new NoSession(), id => Row(), _ => { });
            Assert.Null(JkrCopilotFix.Launcher);
            Assert.False(JkrCopilotFix.LauncherReady);
            Assert.False(Row().CopilotFixable);
        }

        [Fact]
        public void The_AI_493_prompt_builder_is_kept_for_the_rework()
        {
            Assert.NotNull(JkrCopilotFixInstaller.DefaultPromptBuilder);
            var parts = JkrCopilotFixInstaller.DefaultPromptBuilder(Row(), true);
            Assert.NotEmpty(parts);
            Assert.Contains("100", parts[0]);
        }

        [Fact]
        public void No_session_means_no_launcher()
        {
            JkrCopilotFixInstaller.PromptBuilder = (issue, bm) => new[] { "p" };
            JkrCopilotFixInstaller.Install(null, id => Row(), _ => { });
            Assert.Null(JkrCopilotFix.Launcher);
        }
    }
}
