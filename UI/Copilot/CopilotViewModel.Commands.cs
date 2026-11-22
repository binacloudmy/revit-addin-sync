using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using HL = RevitWebAppSync.UI.Copilot.Highlights;


namespace RevitWebAppSync.UI.Copilot
{
    // Saved commands: the sheet, the list, delete. They live on this PC (LocalCommandStore, a file beside
    // copilot-prefs.json), not on the account: nothing here asks the backend. Signing in is still needed to save
    // (and for the Commands tab); the saved commands then show for whoever is signed in on this PC.
    public partial class CopilotViewModel
    {
        // ─── Saved Commands J1 ───────────────────────────────────────────────

        /// <summary>ChatView subscribes and shows the Save sheet.</summary>
        public event System.Action<SavedCommandDraft> SaveCommandRequested;

        /// <summary>Palette rebuilds its list on next open after a catalog change.</summary>
        public event System.Action PaletteInvalidated;

        public void OpenSaveCommandSheet(ChatMessage m)
        {
            if (m == null) return;
            if (IsSignedOut || string.IsNullOrEmpty(_pane.LoadConfig()?.AccessToken))
            {
                Thread.Add(new ChatMessage
                { Role = "ai", Kind = CpMsgKind.AiReply, Text = "Sign in to save commands." });
                return;
            }
            SaveCommandRequested?.Invoke(SavedCommandDraft.FromReply(m.SourcePrompt, m.ToolsUsed, m.RunId));
        }

        public void OnEditCommand(SlashTool t)
        {
            if (t == null || !t.Editable) return;
            SaveCommandRequested?.Invoke(SavedCommandDraft.FromTool(t));
        }

        public async System.Threading.Tasks.Task OnDeleteCommandAsync(SlashTool t)
        {
            if (t == null || !t.Editable) return;
            var ok = LocalCommandStore.Default.Delete(t.Id);
            await RefreshCommandCatalogAsync(force: true);
            Thread.Add(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply,
                Text = ok ? $"Deleted `/{t.Id}`." : $"`/{t.Id}` was already gone.",
            });
        }

        /// <summary>Save (or update, when the draft carries EditingId) on this PC — returns
        /// null on success or an error string the sheet displays.</summary>
        public async System.Threading.Tasks.Task<string> SaveDraftAsync(SavedCommandDraft d)
        {
            if (string.IsNullOrEmpty(_pane.LoadConfig()?.AccessToken)) return "Sign in to save commands.";
            try
            {
                var store = LocalCommandStore.Default;
                var (command, error) = d.EditingId == null ? store.Add(d) : store.Update(d.EditingId, d);
                if (error != null) return error;
                if (command == null) return "That command no longer exists.";
                await RefreshCommandCatalogAsync(force: true);
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = d.EditingId == null
                        ? $"Saved — run it any time with `/{command.Id}`."
                        : $"Updated `/{command.Id}`.",
                });
                return null;
            }
            catch (System.Exception ex) { return "Could not save: " + ex.Message; }
        }

        /// <summary>Load the commands saved on this PC into the catalog's Mine tier (palette, Home, Commands tab).
        /// No network: the backend no longer holds a Mine tier. Old cached backend rows in the prefs are dropped.</summary>
        public System.Threading.Tasks.Task RefreshCommandCatalogAsync(bool force = false)
        {
            try
            {
                ToolCatalog.MergeMine(LocalCommandStore.Default.ToTools());
                DropCachedBackendMine();
                PaletteInvalidated?.Invoke();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] saved commands: " + ex.Message); }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        // The previous Mine tier came from the account and was cached in the prefs; it is not read any more, and the
        // cache is emptied so it cannot show. Nothing on the backend changes.
        private void DropCachedBackendMine()
        {
            try
            {
                var prefs = _pane.LoadPrefs();
                if (prefs == null || (string.IsNullOrEmpty(prefs.SavedCommandsJson) && string.IsNullOrEmpty(prefs.SavedCommandsEtag))) return;
                prefs.SavedCommandsJson = "";
                prefs.SavedCommandsEtag = "";
                prefs.Save();
            }
            catch { /* the cache is a leftover */ }
        }

        /// <summary>A command of this PC ran: one more run for its "n runs" line.</summary>
        private void CountSavedRun(SlashTool tool)
        {
            try
            {
                if (tool != null && tool.Editable && LocalCommandStore.Default.CountRun(tool.Id))
                    ToolCatalog.MergeMine(LocalCommandStore.Default.ToTools());
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] run count: " + ex.Message); }
        }
    }
}
