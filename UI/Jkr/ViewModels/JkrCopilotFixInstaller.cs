using System;
using System.Collections.Generic;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>
    /// AI-494: puts the launcher on the AI-492 seam (<see cref="JkrCopilotFix.Launcher"/>). The button shows only while a
    /// launcher is set (<see cref="JkrCopilotFix.LauncherReady"/>, which <c>IssueVm.CopilotFixable</c> requires), and a
    /// launcher is set only while there is a prompt builder AND a confirm, so neither means no button.
    /// </summary>
    public static class JkrCopilotFixInstaller
    {
        /// <summary>The prompt builder of the protocol-3 rework (set_parameters wording; see JkrCopilotPrompt).</summary>
        public static Func<IssueVm, bool, IReadOnlyList<string>> DefaultPromptBuilder { get; } = (issue, bm) => JkrCopilotPrompt.Build(issue, bm);

        /// <summary>The prompt parts for a row, in the pane language (<c>bm</c>), none over the element-id cap. Default: the
        /// protocol-3 builder. Null hides the button for every row.</summary>
        public static Func<IssueVm, bool, IReadOnlyList<string>> PromptBuilder { get; set; } = DefaultPromptBuilder;

        /// <summary>Sets the launcher, or clears it (button hidden) when there is no prompt builder, no session or no confirm.
        /// The confirm is required: on the merged engine nothing else asks the person before a write.</summary>
        /// <param name="confirm">Shows the confirm dialog for the content and returns true on Yes.</param>
        public static void Install(ICopilotSession session, Func<string, IssueVm> issueById, Action<string> notify,
            JkrCopilotRecheckHost<IssueVm> recheck = null, Func<CopilotConfirmContent, bool> confirm = null)
        {
            if (PromptBuilder == null || session == null || confirm == null) { JkrCopilotFix.Launcher = null; return; }
            JkrCopilotFix.Launcher = new JkrCopilotFixLauncher(
                session,
                req =>
                {
                    var build = PromptBuilder ?? throw new InvalidOperationException("the Copilot prompt builder is not set");
                    var issue = issueById(req.IssueId) ?? throw new InvalidOperationException("the row is no longer in the list");
                    return build(issue, !PaneLanguage.IsEn);
                },
                notify,
                () => !PaneLanguage.IsEn,
                afterWrite: recheck == null ? (Func<CopilotFixRequest, System.Threading.Tasks.Task>)null : new JkrCopilotRecheck<IssueVm>(recheck).RunAsync,
                confirm: req =>
                {
                    var issue = issueById(req.IssueId) ?? throw new InvalidOperationException("the row is no longer in the list");
                    var bm = !PaneLanguage.IsEn;
                    return confirm(JkrCopilotConfirm.Build(JkrCopilotPrompt.FactsOf(issue, bm), bm));
                });
        }
    }
}
