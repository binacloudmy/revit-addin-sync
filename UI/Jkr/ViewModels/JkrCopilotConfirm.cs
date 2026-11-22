using System;
using System.Collections.Generic;
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

        public static CopilotConfirmContent Build(CopilotPromptFacts facts)
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
            if (variant.LevelBased)
            {
                // WP-3 (C1.5.2): a create, not a change. Say what is made, that nothing existing is touched, and how it is undone.
                c.Heading = $"Let Copilot create a 3D view for {ids.Count} level{(ids.Count == 1 ? "" : "s")} for check {checkId}?";
                c.Body = "Copilot makes one new 3D view per level listed below, named \"<level> (07d_Dokumen)\", in the browser folder 07d_Dokumen, with the JKR Dokumen 3D template (when the project has it) and a section box around the level. "
                    + "A level that already has such a view is skipped. No existing view or element is changed, renamed or deleted. "
                    + (parts > 1 ? $"Sent in {parts} parts, with this one confirmation. " : "")
                    + "Applied at once; you can Undo from the Copilot chat. The JKR re-check decides whether the row is fixed.";
                c.Details = string.Join(Environment.NewLine, ids.Take(MaxListed).Select(i => i + " — " + JkrCopilotPrompt.LevelNameOf(facts, i)).Concat(ids.Count > MaxListed ? new[] { $"... and {ids.Count - MaxListed} more" } : new string[0]));
                return c;
            }
            var scheduleIds = variant.ColumnsFromFacts ? ids.Where(i => JkrCopilotPrompt.IsScheduleId(facts, i)).ToList() : new List<long>();
            if (typeLevel)
                c.Heading = $"Let Copilot change {ids.Count} type{(ids.Count == 1 ? "" : "s")} ({instances} instance{(instanceNo == 1 && !capped ? "" : "s")}) for check {checkId}?";
            else
                c.Heading = $"Let Copilot change {ids.Count} element{(ids.Count == 1 ? "" : "s")} for check {checkId}?";

            var body = new StringBuilder();
            if (typeLevel)
            {
                body.Append(variant.Change).Append($" on the TYPE of each element listed below (one id per type). The change applies to ALL {(atLeast ? "at least " : "")}{instances} instance{(instanceNo == 1 && !atLeast ? "" : "s")} of those types, listed or not. Category: {Cat(facts)}. Nothing is deleted. ");
            }
            else if (scheduleIds.Count > 0)
            {
                body.Append("Add the mandatory columns to the " + scheduleIds.Count + " room schedule" + (scheduleIds.Count == 1 ? "" : "s") + " listed below with the stored playbook edit-schedule")
                    .Append(scheduleIds.Count < ids.Count ? ", and fill empty values on the " + (ids.Count - scheduleIds.Count) + " room" + (ids.Count - scheduleIds.Count == 1 ? "" : "s") + " listed below" : "")
                    .Append(". Only the items listed are changed: no schedule is made, filtered or deleted and nothing else is deleted. ");
            }
            else if (variant.Playbook != null || variant.NoUndo)
            {
                body.Append(variant.Change).Append(" for the items listed below only. No other element is touched and nothing is deleted. ");
            }
            else
            {
                body.Append(variant.Change).Append(" on the elements listed below only. ");
                if (level == CopilotWriteLevel.Either)
                    body.Append("If the parameter is a type parameter, it changes the element's type and so EVERY instance of that type, listed or not. ");
                else
                    body.Append("No other element is touched and nothing is deleted. ");
            }
            if (variant.NamesFromFacts)
            {
                var src = ids.Select(i => JkrCopilotPrompt.RenameOf(facts, i)).Where(r => r != null && !r.Ask).Select(r => JkrCopilotPrompt.CleanLabel(r.Source)).Where(x => x.Length > 0).Distinct().ToList();
                var asks = ids.Select(i => JkrCopilotPrompt.RenameOf(facts, i)).Count(r => r == null || r.Ask);
                if (asks < ids.Count)
                {
                    body.Append("The new names were derived by the JKR pane, not by Copilot");
                    if (src.Count > 0) body.Append(" (evidence: ").Append(string.Join("; ", src)).Append(')');
                    body.Append("; Copilot applies exactly those names and the category is not changed. ");
                }
                if (asks > 0)
                    body.Append(asks).Append(asks == 1 ? " type has" : " types have").Append(" no JKR code the pane can derive (ASK): Copilot asks you for the code and never picks one; a type you do not answer for is left as it is. ");
                body.Append("Old name -> new name is listed below. Applied at once; you can Undo from the Copilot chat. ");
            }
            else if (variant.NoUndo)
                body.Append("Copilot loads the links at once. Loading a link clears Revit's Undo history, so this cannot be undone and the Undo entries made before it are lost. If a link's file is missing, Revit's file picker opens: choose the file. ");
            else if (scheduleIds.Count > 0)
                body.Append("Copilot adds the columns at once; room values it cannot take from the model it asks you for, and never guesses. If the project has no parameter for a column, the column is skipped and Copilot says so. You can Undo from the Copilot chat. ");
            else if (variant.Playbook != null)
                body.Append("Copilot runs a stored playbook and applies it at once; if it needs a choice from you it asks first. You can Undo from the Copilot chat. ");
            else
            body.Append("Copilot works out the new values from the model and applies them at once; you can Undo from the Copilot chat. ");
            if (parts > 1)
                body.Append($"Sent in {parts} parts, with this one confirmation. ");
            body.Append("The JKR re-check decides whether the row is fixed.");
            c.Body = body.ToString();

            var d = new StringBuilder();
            foreach (var id in ids.Take(MaxListed))
            {
                string label = null;
                if (facts.ElementLabels != null) facts.ElementLabels.TryGetValue(id, out label);
                label = JkrCopilotPrompt.CleanLabel(label);
                d.Append(id);
                if (variant.NamesFromFacts) d.Append(" — ").Append(JkrCopilotPrompt.RenameLine(facts, id));
                else if (variant.SetFromFacts) d.Append(label.Length > 0 ? " — " + label : "").Append(" — ").Append(JkrCopilotPrompt.SetLine(facts, id));
                else if (scheduleIds.Contains(id)) d.Append(" — schedule \"").Append(JkrCopilotPrompt.ScheduleNameOf(facts, id)).Append("\" — add columns: ").Append(string.Join(", ", JkrCopilotPrompt.ColumnsOf(facts, id)));
                else if (label.Length > 0) d.Append(" — ").Append(label);
                var matLine = checkId == "Db.2" ? JkrCopilotPrompt.MaterialLine(facts, id) : "";
                if (matLine.Length > 0) d.Append(" — ").Append(matLine);
                d.AppendLine();
            }
            if (ids.Count > MaxListed)
                d.AppendLine($"... and {ids.Count - MaxListed} more");
            c.Details = d.ToString().TrimEnd();
            return c;
        }
    }
}
