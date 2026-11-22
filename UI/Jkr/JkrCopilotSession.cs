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
    public sealed class JkrCopilotSession : ICopilotSession, ICopilotWriteFailure, ICopilotToolError
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

        // Thread position when the current part was sent (and at NewChat): only cards after it belong to this part.
        private int _mark;

        /// <summary>Where this part's turn stands, read from the thread since the last Send. The merged Copilot has no Ya/Tidak
        /// card: a write applies by itself, and the only questions are the ask card (a parked question, including the wide-write
        /// gate "Needs your OK"). So: an unanswered ask card = Open; an ask answered with a decline or with the gate's write_cancel
        /// = Declined; a committed write in the turn's evidence = Approved (the write ran, by itself or after the gate's
        /// write_all); otherwise None. Newest event wins, an open ask beats all.</summary>
        public CopilotCardState Cards
        {
            get
            {
                var vm = Vm;
                if (vm == null) return CopilotCardState.None;
                var cards = new List<CopilotCardInfo>();
                for (var i = Math.Min(_mark, vm.Thread.Count); i < vm.Thread.Count; i++)
                {
                    var m = vm.Thread[i];
                    var ask = m.Ask;
                    if (ask != null)
                    {
                        if (ask.AnsweredSummary == null) { cards.Add(new CopilotCardInfo(false, null)); continue; }
                        if (AnsweredNo(ask)) cards.Add(new CopilotCardInfo(true, false));   // an answered, non-declined question is no state: only a committed write is Approved
                    }
                    var ev = m.Evidence;
                    if (ev != null)
                    {
                        if (ev.AnyCommitted) cards.Add(new CopilotCardInfo(true, true));
                        else if (ev.Receipt.Declined > 0 && ev.Receipt.Done == 0) cards.Add(new CopilotCardInfo(true, false));
                        else if (ev.Receipt.FailedCount > 0) cards.Add(new CopilotCardInfo(true, false, failed: true));   // AI-dc1: tried, Revit refused
                    }
                }
                return CopilotCards.Read(cards);
            }
        }

        /// <summary>AI-dc1: Revit's reason for the first failed write since this part was sent.</summary>
        public string WriteFailure
        {
            get
            {
                var vm = Vm;
                if (vm == null) return null;
                for (var i = Math.Min(_mark, vm.Thread.Count); i < vm.Thread.Count; i++)
                {
                    var why = vm.Thread[i].Evidence?.FirstWriteFailure;
                    if (why != null) return why;
                }
                return null;
            }
        }

        /// <summary>C4.2.2: the newest tool error since this part was sent (what the loop guard hides), or null.</summary>
        public string LastToolError
        {
            get
            {
                var vm = Vm;
                if (vm == null) return null;
                string last = null;
                for (var i = Math.Min(_mark, vm.Thread.Count); i < vm.Thread.Count; i++)
                    last = vm.Thread[i].Evidence?.LastToolError ?? last;
                return last;
            }
        }

        /// <summary>AI-dc1: the partial-rename note of this part ("Copilot renamed 8 of 9 types; Revit refused: ..."), or null.</summary>
        public string PartialNote
        {
            get
            {
                var vm = Vm;
                if (vm == null) return null;
                for (var i = Math.Min(_mark, vm.Thread.Count); i < vm.Thread.Count; i++)
                {
                    var note = vm.Thread[i].Evidence?.RenameRefusal;
                    if (note != null) return note;
                }
                return null;
            }
        }

        /// <summary>AI-dc1: how many changes failed since this part was sent.</summary>
        public int WriteFailedCount
        {
            get
            {
                var vm = Vm;
                if (vm == null) return 0;
                var n = 0;
                for (var i = Math.Min(_mark, vm.Thread.Count); i < vm.Thread.Count; i++)
                    n += vm.Thread[i].Evidence?.Receipt.FailedCount ?? 0;
                return n;
            }
        }

        /// <summary>The ask was answered "no": the gate's write_cancel, or any option that reads like declining.</summary>
        private static bool AnsweredNo(AskCardModel ask)
        {
            var said = ask.AnsweredSummary;
            foreach (var o in ask.Options)
                if (string.Equals(o.Label, said, StringComparison.OrdinalIgnoreCase))
                    return o.Id == WideWriteModel.WriteCancelId || AskCardModel.IsDeclineLike(o.Label);
            return AskCardModel.IsDeclineLike(said);
        }

        public bool IsPaneVisible => App.CopilotPaneHost?.IsVisible ?? false;

        public void Send(string text)
        {
            var vm = Vm ?? throw new InvalidOperationException("Copilot is not loaded");
            _mark = vm.Thread.Count;   // before ChatSend adds its bubbles: cards from here on are this part's
            vm.ChatSend(text);
        }
    }
}
