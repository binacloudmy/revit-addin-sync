using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Model;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr
{
    /// <summary>
    /// AI-494: the real <see cref="ICopilotSession"/>. Opens the Copilot dockable pane the way
    /// <c>OpenCopilotCommand</c> does, and drives the pane's <see cref="CopilotViewModel"/> through its public members.
    /// Revit and WPF; not unit-tested (the sequence is, with a fake, in JkrCopilotLauncherTests).
    /// </summary>
    public sealed class JkrCopilotSession : ICopilotSession
    {
        private static CopilotViewModel Vm => App.CopilotPaneHost?.Panel?.ViewModel;

        public async Task<string> OpenPaneAsync()
        {
            var dispatcher = App.RevitDispatcher;
            if (dispatcher == null || App.CopilotPaneHost?.Panel == null) return "pane not loaded; restart Revit";
            // Showing a dockable pane needs a Revit API context; the pane click is not one, so go through the dispatcher.
            return await dispatcher.InvokeAsync(uiApp =>
            {
                try
                {
                    if (uiApp.ActiveUIDocument == null) return "open a Revit project first";
                    var pane = uiApp.GetDockablePane(CopilotPaneHost.PaneId);
                    if (pane == null) return "Copilot pane not found; restart Revit";
                    if (!pane.IsShown()) pane.Show();
                    App.CopilotPaneHost?.Panel?.SetRevitContext(uiApp);
                    return (string)null;
                }
                catch (Exception ex) { return ex.Message; }
            }).ConfigureAwait(true);
        }

        /// <summary>The pane's "+" command: empty thread and <c>RevitChatRouter.ResetSession()</c> (a new session id).</summary>
        public void NewChat() => (Vm ?? throw new InvalidOperationException("Copilot is not loaded")).ClearChatCommand.Execute(null);

        public bool IsIndexing => Vm?.IsIndexing ?? false;

        public bool IsSignedIn
        {
            get { try { return BinaConfig.Load()?.IsLoggedIn() ?? false; } catch { return false; } }
        }

        /// <summary>A turn is streaming, or a Ya/Tidak / question / code card still waits for the person. A chunk sent
        /// over an open card would kill it, so the launcher waits.</summary>
        public bool IsTurnBusy
        {
            get
            {
                var vm = Vm;
                if (vm == null) return false;
                return vm.IsSending || vm.Thread.Any(m => !m.ActionsResolved
                    && (m.Kind == CpMsgKind.ConfirmActions || m.ChoiceBatch != null || m.PendingCode != null));
            }
        }

        public void Send(string text) => (Vm ?? throw new InvalidOperationException("Copilot is not loaded")).ChatSend(text);
    }

    /// <summary>AI-494: puts the launcher on the AI-492 seam (<see cref="JkrCopilotFix.Launcher"/>); the button shows once set.</summary>
    public static class JkrCopilotFixInstaller
    {
        /// <summary>AI-493: the prompt parts for a row, in the pane language (<c>bm</c>), none over the element-id cap.
        /// TODO(AI-493): default this to <c>JkrCopilotPrompt.Build</c> once that lands.</summary>
        public static Func<IssueVm, bool, IReadOnlyList<string>> PromptBuilder { get; set; }

        /// <summary>Sets the launcher. Without a prompt builder it sets nothing, so the button stays hidden
        /// (a click with no prompt would be a dead button).</summary>
        public static void Install(Func<string, IssueVm> issueById, Action<string> notify)
        {
            if (PromptBuilder == null) return;
            JkrCopilotFix.Launcher = new JkrCopilotFixLauncher(
                new JkrCopilotSession(),
                req =>
                {
                    var build = PromptBuilder ?? throw new InvalidOperationException("the Copilot prompt builder is not set");
                    var issue = issueById(req.IssueId) ?? throw new InvalidOperationException("the row is no longer in the list");
                    return build(issue, !PaneLanguage.IsEn);
                },
                notify,
                () => !PaneLanguage.IsEn);
        }
    }
}
