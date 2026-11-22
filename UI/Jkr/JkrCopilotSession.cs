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
        public void NewChat()
        {
            (Vm ?? throw new InvalidOperationException("Copilot is not loaded")).ClearChatCommand.Execute(null);
            _mark = Vm.Thread.Count;
        }

        public bool IsIndexing => Vm?.IsIndexing ?? false;

        public bool IsSignedIn
        {
            get { try { return BinaConfig.Load()?.IsLoggedIn() ?? false; } catch { return false; } }
        }

        public bool IsSending => Vm?.IsSending ?? false;

        // Thread position at the last NewChat: cards before it belong to an old chat.
        private int _mark;

        /// <summary>Ya/Tidak (and question / code) cards on the thread since the last NewChat: any open one wins, else the
        /// newest resolved one (Ya = Approved, Tidak = Declined). A chunk sent over an open card would kill it.</summary>
        public CopilotCardState Cards
        {
            get
            {
                var vm = Vm;
                if (vm == null) return CopilotCardState.None;
                var state = CopilotCardState.None;
                for (var i = Math.Min(_mark, vm.Thread.Count); i < vm.Thread.Count; i++)
                {
                    var m = vm.Thread[i];
                    if (!(m.Kind == CpMsgKind.ConfirmActions || m.ChoiceBatch != null || m.PendingCode != null)) continue;
                    if (!m.ActionsResolved) return CopilotCardState.Open;
                    state = m.ActionsApproved == false ? CopilotCardState.Declined : CopilotCardState.Approved;
                }
                return state;
            }
        }

        public bool IsPaneVisible => App.CopilotPaneHost?.IsVisible ?? false;

        public void Send(string text) => (Vm ?? throw new InvalidOperationException("Copilot is not loaded")).ChatSend(text);
    }
}
