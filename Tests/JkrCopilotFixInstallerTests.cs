using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-494 (IssueVm / WPF half; not compiled on the Mac harness): the button shows only when a launcher AND a prompt builder are set; the app default is the protocol-3 builder and a confirm dialog; with no builder or no confirm the button is hidden.</summary>
    [Collection("CopilotLauncher")]
    public class JkrCopilotFixInstallerTests : IDisposable
    {
        private readonly Func<IssueVm, IReadOnlyList<string>> _savedBuilder = JkrCopilotFixInstaller.PromptBuilder;
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
            JkrCopilotFixInstaller.Install(new NoSession(), id => Row(), _ => { }, confirm: _ => true);
            Assert.Null(JkrCopilotFix.Launcher);
            Assert.False(JkrCopilotFix.LauncherReady);
            Assert.False(Row().CopilotFixable);
        }

        [Fact]
        public void A_prompt_builder_sets_the_launcher_and_shows_the_button()
        {
            JkrCopilotFixInstaller.PromptBuilder = issue => new[] { "p" };
            JkrCopilotFixInstaller.Install(new NoSession(), id => Row(), _ => { }, confirm: _ => true);
            Assert.NotNull(JkrCopilotFix.Launcher);
            Assert.True(JkrCopilotFix.LauncherReady);
            Assert.True(Row().CopilotFixable);
        }

        [Fact]
        public void The_default_install_with_a_confirm_shows_the_button_for_the_enabled_families_only()
        {
            JkrCopilotFixInstaller.PromptBuilder = _savedBuilder;   // what the app starts with
            Assert.NotNull(JkrCopilotFixInstaller.PromptBuilder);
            JkrCopilotFixInstaller.Install(new NoSession(), id => Row(), _ => { }, confirm: _ => true);
            Assert.True(JkrCopilotFix.LauncherReady);
            Assert.True(Row().CopilotFixable);                 // Da.1
            var dd = Row(); dd.SpecCheckId = "Dd.1";
            Assert.True(dd.CopilotFixable);                    // Dd.1 (WP-6): enabled, the category has one code prefix
            dd.RevitCategory = "Floors";
            Assert.False(dd.CopilotFixable);                   // LB and LA: the new name is not derivable
            var c = Row(); c.SpecCheckId = "C1.3.1";
            Assert.False(c.CopilotFixable);                    // create-type: hidden
        }

        [Fact]
        public void No_confirm_means_no_launcher_and_a_hidden_button()
        {
            JkrCopilotFix.Launcher = new JkrCopilotFixLauncher(new NoSession(), _ => new[] { "x" }, _ => { });   // a stale one
            JkrCopilotFixInstaller.Install(new NoSession(), id => Row(), _ => { }, confirm: null);
            Assert.Null(JkrCopilotFix.Launcher);
            Assert.False(Row().CopilotFixable);
        }

        [Fact]
        public void The_installer_is_the_only_production_code_that_builds_a_launcher_and_it_always_passes_a_confirm()
        {
            var root = System.IO.Directory.GetCurrentDirectory();
            while (root != null && !System.IO.File.Exists(System.IO.Path.Combine(root, "RevitWebAppSync.csproj"))) root = System.IO.Path.GetDirectoryName(root);
            Assert.NotNull(root);
            var offenders = new List<string>();
            foreach (var f in System.IO.Directory.EnumerateFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                if (f.Contains(System.IO.Path.DirectorySeparatorChar + "Tests" + System.IO.Path.DirectorySeparatorChar)
                    || f.Contains(System.IO.Path.DirectorySeparatorChar + "obj" + System.IO.Path.DirectorySeparatorChar)
                    || f.Contains(System.IO.Path.DirectorySeparatorChar + "bin" + System.IO.Path.DirectorySeparatorChar)) continue;
                var text = System.IO.File.ReadAllText(f);
                if (!text.Contains("new JkrCopilotFixLauncher(")) continue;
                if (!f.EndsWith("JkrCopilotFixInstaller.cs") || !text.Contains("confirm: req =>")) offenders.Add(f);
            }
            Assert.Empty(offenders);
        }

        [Fact]
        public void The_confirm_dialog_gets_the_ids_and_a_No_sends_nothing()
        {
            var session = new CountingSession();
            CopilotConfirmContent seen = null;
            JkrCopilotFixInstaller.Install(session, id => Row(), _ => { }, confirm: c => { seen = c; return false; });
            var launcher = (JkrCopilotFixLauncher)JkrCopilotFix.Launcher;
            var outcome = launcher.LaunchAsync(Row().ToCopilotFixRequest()).GetAwaiter().GetResult();
            Assert.Equal(CopilotLaunchOutcome.NotConfirmed, outcome);
            Assert.Equal(0, session.Opened + session.Cleared + session.Sent);
            Assert.NotNull(seen);
            Assert.Contains("100", seen.Details);
            Assert.DoesNotContain("101", seen.Details);   // Da.1 is type-level: both ids share a label, one id listed
        }

        private sealed class CountingSession : ICopilotSession
        {
            public int Opened, Cleared, Sent;
            public Task<string> OpenPaneAsync() { Opened++; return Task.FromResult<string>(null); }
            public void NewChat() { Cleared++; }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsSending => false;
            public CopilotCardState Cards => CopilotCardState.None;
            public bool IsPaneVisible => true;
            public void Send(string text) { Sent++; }
        }

        [Fact]
        public void The_default_prompt_builder_is_the_protocol_3_one()
        {
            Assert.NotNull(JkrCopilotFixInstaller.DefaultPromptBuilder);
            var parts = JkrCopilotFixInstaller.DefaultPromptBuilder(Row());
            Assert.NotEmpty(parts);
            Assert.Contains("100", parts[0]);
            Assert.Contains("set_parameters", parts[0]);
        }

        [Fact]
        public void No_session_means_no_launcher()
        {
            JkrCopilotFixInstaller.PromptBuilder = issue => new[] { "p" };
            JkrCopilotFixInstaller.Install(null, id => Row(), _ => { });
            Assert.Null(JkrCopilotFix.Launcher);
        }
    }
}
