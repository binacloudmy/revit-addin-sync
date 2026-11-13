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
    // Saved commands: the sheet, catalog sync and delete. Split mechanically out of CopilotViewModel.cs (AI-310).
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
            var token = _pane.LoadConfig()?.AccessToken;
            if (string.IsNullOrEmpty(token)) return;
            bool ok;
            try { ok = await new AIService().DeleteCommandAsync(t.Id, token, System.Threading.CancellationToken.None); }
            catch { ok = false; }
            await RefreshCommandCatalogAsync(force: true);
            Thread.Add(new ChatMessage
            {
                Role = "ai", Kind = CpMsgKind.AiReply,
                Text = ok ? $"Deleted `/{t.Id}`." : $"`/{t.Id}` was already gone.",
            });
        }

        /// <summary>Save (or update, when the draft carries EditingId) — returns
        /// null on success or an error string the sheet displays.</summary>
        public async System.Threading.Tasks.Task<string> SaveDraftAsync(SavedCommandDraft d)
        {
            var token = _pane.LoadConfig()?.AccessToken;
            if (string.IsNullOrEmpty(token)) return "Sign in to save commands.";
            try
            {
                var body = d.ToRequest();
                var res = d.EditingId == null
                    ? await new AIService().SaveCommandAsync(body, token, System.Threading.CancellationToken.None)
                    : await new AIService().UpdateCommandAsync(d.EditingId, body, token, System.Threading.CancellationToken.None);
                if (res == null) return "That command no longer exists.";
                await RefreshCommandCatalogAsync(force: true);
                Thread.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply,
                    Text = d.EditingId == null
                        ? $"Saved — run it any time with `/{res.Command.Id}`."
                        : $"Updated `/{res.Command.Id}`.",
                });
                return null;
            }
            catch (System.InvalidOperationException ex) { return ex.Message; }
            catch (System.Exception ex) { return "Could not save: " + ex.Message; }
        }

        /// <summary>GET the catalog (ETag-cached) and merge the Mine tier into
        /// ToolCatalog; persists the rows so an offline start still shows them.</summary>
        public async System.Threading.Tasks.Task RefreshCommandCatalogAsync(bool force = false)
        {
            var prefs = _pane.LoadPrefs();
            var token = _pane.LoadConfig()?.AccessToken;
            if (string.IsNullOrEmpty(token)) { RestoreCachedMine(prefs); return; }
            var svc = new AIService();
            Models.CatalogResponseDto res = null;
            try
            {
                res = await svc.GetCommandsAsync(token, force ? null : prefs.SavedCommandsEtag,
                                                 System.Threading.CancellationToken.None);
            }
            catch { /* transport — fall through to cache */ }
            if (res == null) { RestoreCachedMine(prefs); return; }   // 304 or failure → keep what we have
            var mine = res.Commands.Where(c => c.Group == "mine").ToList();
            ToolCatalog.MergeRemote(mine.Select(ToolCatalog.FromCatalogEntry));
            try
            {
                prefs.SavedCommandsJson = Newtonsoft.Json.JsonConvert.SerializeObject(mine);
                prefs.SavedCommandsEtag = svc.LastCommandsEtag ?? "";
                prefs.Save();
            }
            catch { /* cache is a convenience */ }
            PaletteInvalidated?.Invoke();
        }

        private void RestoreCachedMine(CopilotPrefs prefs)
        {
            if (string.IsNullOrEmpty(prefs?.SavedCommandsJson)) return;
            try
            {
                var mine = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    System.Collections.Generic.List<Models.CatalogCommandDto>>(prefs.SavedCommandsJson);
                ToolCatalog.MergeRemote(mine.Select(ToolCatalog.FromCatalogEntry));
                PaletteInvalidated?.Invoke();
            }
            catch { /* stale cache — ignore */ }
        }

    }
}
