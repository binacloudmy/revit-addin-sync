using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    // AI-605 demo: "tunjuk 3D" after read_dwg_plan. Answered in the add-in (no bina-ai tool): a one-line
    // summary on the existing Ya/Tidak gate card, and on Ya the #272 builder for that one plan (DwgShow3d).
    public partial class CopilotViewModel
    {
        private const string Dwg3dAskPrefix = "local-dwg3d-";
        // One request per card (RequestId -> request): Ya on an older card builds that card's plan.
        private readonly Dictionary<string, BuildRequest> _dwg3dPending = new Dictionary<string, BuildRequest>();
        private int _dwg3dCards;
        private bool _dwg3dHooked;

        /// <summary>ChatSend's hook: true when the text was a "tunjuk 3D" answered here (user bubble added).</summary>
        private bool DwgShow3dAsk(string text, string shown, List<string> images, List<FileAttachment> files, SlashTool slashChip)
        {
            if (slashChip != null) return false;
            if (_dwg3dHandOn) return false;
            var (path, sheet) = DwgPlanReader.LastRead;
            string toRead = DwgShow3d.ToRead(AttachedDwgPath(files), path, text);
            if (toRead != null)
            {
                ReadThenAnswer(toRead, text, shown, images, files);
                return true;
            }
            if (!DwgShow3d.Intercepts(sheet, text, out DwgPlan plan, out string problem)) return false;
            Thread.Add(UserBubble(shown, images, files));
            Answer(path, plan, problem);
            return true;
        }

        private bool _dwg3dHandOn;

        private ChatMessage UserBubble(string shown, List<string> images, List<FileAttachment> files) =>
            new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = shown, ImagesBase64 = images, Files = files, Time = System.DateTime.Now.ToString("h:mm tt") };

        /// <summary>The DWG on this message, else the latest one attached earlier in this chat. null = none.</summary>
        private string AttachedDwgPath(List<FileAttachment> files)
        {
            IEnumerable<FileAttachment> Dwgs(List<FileAttachment> fs) => fs?.Where(f => f.Kind == AttachmentKind.Dwg) ?? Enumerable.Empty<FileAttachment>();
            var all = Dwgs(files).Concat(Thread.Reverse().Where(m => m.Kind == CpMsgKind.User).SelectMany(m => Dwgs(m.Files)));
            foreach (var f in all)
            {
                string p = !string.IsNullOrEmpty(f.Path) ? f.Path
                    : BinaVibe.Mcp.Tools.DwgScratchCache.IsAttachmentRef(f.Ref) ? BinaVibe.Mcp.Tools.DwgScratchCache.GetPath(f.Ref) : null;
                if (p != null && System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>"tunjuk 3D" before read_dwg_plan read the attached DWG: read it here, off the UI thread (the same
        /// DwgPlanReader.ReadFile read_dwg_plan calls, so its cache serves the tool too), then answer or hand the turn on.</summary>
        private async void ReadThenAnswer(string dwg, string text, string shown, List<string> images, List<FileAttachment> files)
        {
            var bubble = UserBubble(shown, images, files);
            var note = new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = "Membaca " + System.IO.Path.GetFileName(dwg) + "…", Time = System.DateTime.Now.ToString("h:mm tt") };
            Thread.Add(bubble);
            Thread.Add(note);
            DwgPlanSheet sheet = null;
            var settings = CadToBimSettings.Load();
            try { sheet = await System.Threading.Tasks.Task.Run(() => DwgPlanReader.ReadFile(dwg, settings)); }
            catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] tunjuk 3D read failed: " + ex.Message); }
            Thread.Remove(note);
            if (DwgShow3d.Intercepts(sheet, text, out DwgPlan plan, out string problem))
            {
                Answer(dwg, plan, problem);
                return;
            }
            // Not a plan this sheet can show (or unreadable): the server answers, as it did before.
            Thread.Remove(bubble);
            _dwg3dHandOn = true;
            try { ChatSend(text, images, files, displayText: shown); }
            finally { _dwg3dHandOn = false; }
        }

        private void Answer(string path, DwgPlan plan, string problem)
        {
            if (plan == null)
            {
                Reply(problem);
                return;
            }

            string id = Dwg3dAskPrefix + (++_dwg3dCards);
            _dwg3dPending[id] = DwgShow3d.Request(path, plan, CadToBimSettings.Load());
            var ask = new AskDto
            {
                RequestId = id,
                Question = DwgShow3d.Summary(plan),
                // The gate's ids: the one pair the ask card keeps for approval-like labels (AskCardModel.From).
                Options = new List<AskOptionDto>
                {
                    new AskOptionDto { Id = WideWriteModel.WriteAllId, Label = "Ya" },
                    new AskOptionDto { Id = WideWriteModel.WriteCancelId, Label = "Tidak" },
                },
            };
            var card = AskCardModel.From(ask, ReceiptLanguage.Ms);
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.Clarify, Question = card.Question, Ask = card, Time = System.DateTime.Now.ToString("h:mm tt") });
        }

        /// <summary>Ya / Tidak on the local card. True when the card was ours.</summary>
        private bool TrySubmitDwg3d(ChatMessage m, AnswerDto answer, string summary)
        {
            string id = m?.Ask?.RequestId;
            if (id == null || !id.StartsWith(Dwg3dAskPrefix, System.StringComparison.Ordinal)) return false;
            if (m.Ask.AnsweredSummary != null || answer == null) return true;
            m.Ask.AnsweredSummary = string.IsNullOrWhiteSpace(summary) ? "✓" : summary;
            int i = Thread.IndexOf(m);
            if (i >= 0) Thread[i] = Thread[i];

            _dwg3dPending.TryGetValue(id, out BuildRequest request);
            _dwg3dPending.Remove(id);
            if (request == null || !answer.Values.ContainsKey(WideWriteModel.WriteAllId))
            {
                Reply("Baik, tiada apa dibina.");
                return true;
            }

            var handler = App.CadToBimBuildHandler;
            if (handler == null || App.CadToBimBuildEvent == null) { Reply("CAD to BIM tidak tersedia dalam sesi Revit ini."); return true; }
            if (!_dwg3dHooked)
            {
                Dispatcher ui = Dispatcher.CurrentDispatcher;
                handler.Completed += report => ui.BeginInvoke(new System.Action(() => OnDwg3dBuilt(report)));
                _dwg3dHooked = true;
            }
            _dwg3dWaiting = true;
            handler.Request = request;
            if (!new ExternalEventRaiser(App.CadToBimBuildEvent).Raise())
            {
                _dwg3dWaiting = false;
                if (ReferenceEquals(handler.Request, request)) handler.Request = null;
                Reply("Revit sibuk, cuba lagi.");
                return true;
            }
            Reply("Membina " + request.Walls.Count + " dinding…");
            return true;
        }

        private bool _dwg3dWaiting;

        private void OnDwg3dBuilt(BuildReport report)
        {
            // The handler is shared with the CAD to BIM pane: only the build this chat asked for is reported here.
            if (!_dwg3dWaiting || report == null || report.Kind != BuildJobKind.Build) return;
            _dwg3dWaiting = false;
            Reply(report.Ok
                ? "Siap: " + report.Walls + " dinding, " + report.Doors + " pintu, " + report.Windows + " tingkap, " + report.Rooms + " bilik. Ctrl+Z membatalkannya."
                : "Tidak dapat dibina: " + report.Error);
        }

        private void Reply(string text) =>
            Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = text, Time = System.DateTime.Now.ToString("h:mm tt") });
    }
}
