using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-494 (IssueVm / WPF half; not compiled on the Mac harness): the button shows only when a launcher AND a prompt builder are set.</summary>
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
        public void The_default_prompt_builder_is_the_AI_493_one()
        {
            JkrCopilotFixInstaller.PromptBuilder = _savedBuilder;
            Assert.NotNull(JkrCopilotFixInstaller.PromptBuilder);
            var parts = JkrCopilotFixInstaller.PromptBuilder(Row(), true);
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
