using System;
using System.Collections.Generic;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>
    /// AI-494: puts the launcher on the AI-492 seam (<see cref="JkrCopilotFix.Launcher"/>). The button shows only while a
    /// launcher is set (<see cref="JkrCopilotFix.LauncherReady"/>, which <c>IssueVm.CopilotFixable</c> requires), and a
    /// launcher is set only while there is a prompt builder, so no builder means no button.
    /// </summary>
    public static class JkrCopilotFixInstaller
    {
        /// <summary>AI-493's prompt builder. NOT the default (see <see cref="PromptBuilder"/>); the rework below sets it.</summary>
        public static Func<IssueVm, bool, IReadOnlyList<string>> DefaultPromptBuilder { get; } = (issue, bm) => JkrCopilotPrompt.Build(issue, bm);

        /// <summary>AI-493: the prompt parts for a row, in the pane language (<c>bm</c>), none over the element-id cap.
        /// DEFAULT NULL: Fix via Copilot is disabled (button hidden) until the protocol-3 rework (AI-490 follow-up). On the
        /// merged feat/bina-ai-copilot engine writes apply with no Ya/Tidak card, the prompts name tools that no longer
        /// exist (see JkrCopilotPrompt.CreateType.cs) and say "propose and wait", which his engine rule contradicts. The
        /// launcher, session, prompts and tests stay wired; the rework sets this to <see cref="DefaultPromptBuilder"/> (or its replacement).</summary>
        public static Func<IssueVm, bool, IReadOnlyList<string>> PromptBuilder { get; set; } = null;

        /// <summary>Sets the launcher, or clears it when there is no prompt builder.</summary>
        public static void Install(ICopilotSession session, Func<string, IssueVm> issueById, Action<string> notify, JkrCopilotRecheckHost<IssueVm> recheck = null)
        {
            if (PromptBuilder == null || session == null) { JkrCopilotFix.Launcher = null; return; }
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
                afterWrite: recheck == null ? (Func<CopilotFixRequest, System.Threading.Tasks.Task>)null : new JkrCopilotRecheck<IssueVm>(recheck).RunAsync);
        }
    }
}
