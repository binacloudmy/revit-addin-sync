#nullable enable
// ChatMessage as a projection of the turn model (AI-305, staged).
//
// The pane's thread is still a list of ChatMessage (the bag of ~45 fields in
// CopilotModels.cs). This builds that list FROM Turn state, one way: nothing
// is written back, so a message can be dropped and rebuilt at any time and
// no more "Thread[k] = Thread[k]" redraw trick is needed once the pane binds
// to Turn. ChatMessage itself stays until the pane stops reading it
// (AI-306 / AI-310 remove it).
//
// Per turn, in order:
//   user bubble
//   for each question card the turn showed:
//       a Clarify message (the text before the question, and the card), or
//       an AiReply with the text written before a question card, then the card
//   then, by state:
//       running -> one Thinking message (live steps, streaming reply)
//       awaiting answer            -> nothing more (the Clarify message is last)
//       ended                      -> one AiReply (the reply after what was already shown,
//                                      the pane note, steps, result card)
//
// Evidence: the turn owns ONE TurnEvidence. It rides only on the last
// message of an idle turn (the open question, or the final reply), never on
// the earlier ones. So "does a later message carry my evidence" (ChatView.
// OwnsEvidence) is true by construction.

using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TurnProjection
    {
        public const string DefaultToolId = "ai-generated";

        public static IReadOnlyList<ChatMessage> Messages(ChatSession session, string toolId = DefaultToolId)
        {
            var all = new List<ChatMessage>();
            foreach (var t in session.Turns.ToList()) all.AddRange(Messages(t, toolId));
            return all;
        }

        public static IReadOnlyList<ChatMessage> Messages(Turn turn, string toolId = DefaultToolId)
        {
            var time = turn.Started.ToString("h:mm tt");
            var lang = turn.Language;
            var list = new List<ChatMessage>
            {
                new ChatMessage
                {
                    Role = "user", Kind = CpMsgKind.User, Text = turn.Prompt,
                    ImagesBase64 = turn.Images.Count > 0 ? turn.Images.ToList() : null,
                    Files = turn.Files.Count > 0 ? turn.Files.ToList() : null,
                    SlashCommand = turn.Slash,
                    Time = time,
                },
            };

            // Questions, in the order the turn asked them.
            foreach (var q in turn.Questions.OrderBy(x => x.Order))
            {
                list.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Clarify,
                    Text = q.LeadText, Question = q.Card.Question, Ask = q.Card,
                    TurnId = turn.Id, Lang = lang, Time = time,
                });
            }

            var state = turn.State;
            var steps = turn.StepsSnapshot();
            var idle = true;
            if (state == TurnPhase.Running)
            {
                idle = false;
                var text = turn.UnshownReply;
                list.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Thinking,
                    Text = string.IsNullOrWhiteSpace(text) ? "Thinking…" : text,
                    StreamingReply = !string.IsNullOrWhiteSpace(text),
                    LiveSteps = steps,
                    Lang = lang,
                });
            }
            else if (Turn.IsTerminal(state))
            {
                var tools = turn.ToolsUsed;
                list.Add(new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.AiReply, ToolId = toolId,
                    Text = turn.UnshownReply,
                    PaneNote = turn.PaneNote,
                    ToolsUsed = tools.Count > 0 ? tools.ToList() : null,
                    SourcePrompt = turn.SourcePrompt,
                    Interrupted = state == TurnPhase.Interrupted,
                    ResultCard = turn.Result,
                    Lang = lang, Time = time,
                });
            }

            // The one owner of the turn's evidence, steps and ids: the last message of an idle turn.
            if (idle)
            {
                var last = list[list.Count - 1];
                if (last.Kind == CpMsgKind.AiReply || last.Kind == CpMsgKind.Clarify)
                {
                    last.Evidence = turn.Evidence;
                    last.Steps = steps;
                    last.TurnId = turn.Id;
                    last.Lang = lang;
                }
            }
            return list;
        }
    }
}
