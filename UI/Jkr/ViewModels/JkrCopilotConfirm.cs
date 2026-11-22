using System;
using System.Linq;
using System.Text;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>What the confirm dialog says (plain strings, WPF- and Revit-free; the dialog itself is in the panel).</summary>
    public sealed class CopilotConfirmContent
    {
        /// <summary>The question: "Let Copilot change 12 elements for check Da.1?"</summary>
        public string Heading { get; set; } = "";
        /// <summary>What changes, what is fenced, how to undo, who decides pass/fail.</summary>
        public string Body { get; set; } = "";
        /// <summary>One line per element id (with its label), at most <see cref="JkrCopilotConfirm.MaxListed"/>, then a count of the rest.</summary>
        public string Details { get; set; } = "";
    }

    /// <summary>
    /// AI-490 rework: the confirm for "Fix via Copilot", asked by the add-in BEFORE anything is sent. On the protocol-3 engine a
    /// prompt cannot reliably make Copilot ask Ya/Tidak: its rule 7 and the ask_user description forbid asking permission, and the
    /// pane drops an ask_user option worded like approval (AskCardModel). So the one confirm per check (all its ids, however many
    /// prompt parts) is ours, and Tidak sends nothing and leaves the chat as it was. The launcher sends <c>confirmed_write</c> with the turn (JkrCopilotConfirmedWrite) so the engine's "Needs your OK" card
    /// is skipped for what this dialog showed; if the engine still asks (a write outside it), the launcher waits for that card.
    /// </summary>
    public static class JkrCopilotConfirm
    {
        public const int MaxListed = 50;

        private static string Cat(CopilotPromptFacts f) { var c = JkrCopilotPrompt.CleanLabel(f.Category); return c.Length > 0 ? c : "-"; }

        public static CopilotConfirmContent Build(CopilotPromptFacts facts, bool bm)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            var ids = JkrCopilotPrompt.TargetIds(facts);
            var checkId = (facts.CheckId ?? "").Trim();
            var variant = JkrCopilotPrompt.VariantFor(checkId);
            var level = variant.Level;
            var typeLevel = level == CopilotWriteLevel.Type;
            var instanceNo = JkrCopilotConfirmedWrite.InstanceCount(facts);
            var capped = JkrCopilotConfirmedWrite.InstanceCountCapped(facts);
            var instances = instanceNo + (capped ? "+" : "");   // "2000+": a type hit the reader's id cap, the real count is larger
            var atLeast = capped;
            var parts = typeLevel ? 1 : (ids.Count + JkrCopilotPrompt.MaxIdsPerPrompt - 1) / Math.Max(1, JkrCopilotPrompt.MaxIdsPerPrompt);

            var c = new CopilotConfirmContent();
            if (typeLevel)
                c.Heading = bm
                    ? $"Benarkan Copilot mengubah {ids.Count} type ({instances} instance) untuk semakan {checkId}?"
                    : $"Let Copilot change {ids.Count} type{(ids.Count == 1 ? "" : "s")} ({instances} instance{(instanceNo == 1 && !capped ? "" : "s")}) for check {checkId}?";
            else
                c.Heading = bm
                    ? $"Benarkan Copilot mengubah {ids.Count} elemen untuk semakan {checkId}?"
                    : $"Let Copilot change {ids.Count} element{(ids.Count == 1 ? "" : "s")} for check {checkId}?";

            var body = new StringBuilder();
            if (typeLevel)
            {
                body.Append(variant.Change(bm)).Append(bm
                    ? $" pada TYPE bagi elemen yang disenaraikan di bawah (satu id bagi setiap type). Perubahan ini terpakai pada SEMUA {(atLeast ? "sekurang-kurangnya " : "")}{instances} instance type itu, yang disenaraikan atau tidak. Kategori: {Cat(facts)}. Tiada apa dipadam. "
                    : $" on the TYPE of each element listed below (one id per type). The change applies to ALL {(atLeast ? "at least " : "")}{instances} instance{(instanceNo == 1 && !atLeast ? "" : "s")} of those types, listed or not. Category: {Cat(facts)}. Nothing is deleted. ");
            }
            else
            {
                body.Append(variant.Change(bm)).Append(bm ? " pada elemen yang disenaraikan di bawah sahaja. " : " on the elements listed below only. ");
                if (level == CopilotWriteLevel.Either)
                    body.Append(bm
                        ? "Jika parameter itu ialah parameter type, ia mengubah type elemen itu dan SEMUA instancenya, yang disenaraikan atau tidak. "
                        : "If the parameter is a type parameter, it changes the element's type and so EVERY instance of that type, listed or not. ");
                else
                    body.Append(bm ? "Tiada elemen lain disentuh dan tiada apa dipadam. " : "No other element is touched and nothing is deleted. ");
            }
            body.Append(bm
                ? "Copilot menentukan nilai baharu daripada model dan melaksanakannya serta-merta; anda boleh Undo dalam sembang Copilot. "
                : "Copilot works out the new values from the model and applies them at once; you can Undo from the Copilot chat. ");
            if (parts > 1)
                body.Append(bm ? $"Dihantar dalam {parts} bahagian, dengan satu pengesahan ini. " : $"Sent in {parts} parts, with this one confirmation. ");
            body.Append(bm ? "Semakan semula JKR yang memutuskan sama ada baris ini selesai." : "The JKR re-check decides whether the row is fixed.");
            c.Body = body.ToString();

            var d = new StringBuilder();
            foreach (var id in ids.Take(MaxListed))
            {
                string label = null;
                if (facts.ElementLabels != null) facts.ElementLabels.TryGetValue(id, out label);
                label = JkrCopilotPrompt.CleanLabel(label);
                d.Append(id);
                if (label.Length > 0) d.Append(" — ").Append(label);
                d.AppendLine();
            }
            if (ids.Count > MaxListed)
                d.AppendLine(bm ? $"... dan {ids.Count - MaxListed} lagi" : $"... and {ids.Count - MaxListed} more");
            c.Details = d.ToString().TrimEnd();
            return c;
        }
    }
}
