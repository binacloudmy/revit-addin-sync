using System.Collections.Generic;
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
        private const string Dwg3dAskId = "local-dwg3d";
        private BuildRequest _dwg3dPending;
        private bool _dwg3dHooked;

        /// <summary>ChatSend's hook: true when the text was a "tunjuk 3D" answered here (user bubble added).</summary>
        private bool DwgShow3dAsk(string text, string shown, List<string> images, List<FileAttachment> files, SlashTool slashChip)
        {
            if (slashChip != null || !DwgShow3d.IsAsk(text)) return false;
            Thread.Add(new ChatMessage { Role = "user", Kind = CpMsgKind.User, Text = shown, ImagesBase64 = images, Files = files, Time = System.DateTime.Now.ToString("h:mm tt") });
            return TryDwg3d(text);
        }

        private bool TryDwg3d(string text)
        {
            var (path, sheet) = DwgPlanReader.LastRead;
            DwgPlan plan = DwgShow3d.Pick(sheet, text, out string problem);
            if (plan == null)
            {
                Thread.Add(new ChatMessage { Role = "ai", Kind = CpMsgKind.AiReply, Text = problem, Time = System.DateTime.Now.ToString("h:mm tt") });
                return true;
            }

            _dwg3dPending = DwgShow3d.Request(path, plan, CadToBimSettings.Load());
            var ask = new AskDto
            {
                RequestId = Dwg3dAskId,
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
            return true;
        }

        /// <summary>Ya / Tidak on the local card. True when the card was ours.</summary>
        private bool TrySubmitDwg3d(ChatMessage m, AnswerDto answer, string summary)
        {
            if (m?.Ask?.RequestId != Dwg3dAskId) return false;
            if (m.Ask.AnsweredSummary != null || answer == null) return true;
            m.Ask.AnsweredSummary = string.IsNullOrWhiteSpace(summary) ? "✓" : summary;
            int i = Thread.IndexOf(m);
            if (i >= 0) Thread[i] = Thread[i];

            BuildRequest request = _dwg3dPending;
            _dwg3dPending = null;
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
