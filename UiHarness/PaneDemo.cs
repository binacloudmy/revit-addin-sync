using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot;
using RevitWebAppSync.UI.Copilot.Model;

namespace UiHarness
{
    /// <summary>
    /// `UiHarness --pane-demo`: auto-plays the design's "list all doors" run
    /// through the REAL pane rendering (ChatView + AgentActivityView), no
    /// engine needed — streaming thinking prose, steps ticking done, the
    /// determinate scan bar, nested tool cards, then the answer text streaming
    /// in. Mirrors the .dc mock's autoRun timeline so the pane and the design
    /// canvas can be compared playing side by side.
    ///
    /// Drives the VM's public Thread only (same surface the live handlers'
    /// ReplaceLastThinking uses): replaces the one Thinking message per tick.
    /// </summary>
    internal static class PaneDemo
    {
        private const string Thinking =
            "Request: list all doors in this model. I'll filter elements in the 'Doors' " +
            "category across all levels, group them by type_name and level, then validate " +
            "the counts before composing the answer.";

        private const string Answer =
            "**62 doors in this model — 60 on Level 01, 2 on Level 02.**\n\n" +
            "| Door type | Size (mm) | Count |\n|---|---|---|\n" +
            "| PTh300a | 750 × 2100 | 21 |\n| PTt760b | 3700 × 2100 | 15 |\n" +
            "| PTr680a | 900 × 2100 | 8 |\n| PTn520a | 450 × 2100 | 4 |\n" +
            "| PT2p600a | 900 × 2325 | 4 |\n| Others (5 types) | various | 10 |";

        public static async void Run(CopilotPanel panel)
        {
            var vm = panel.ViewModel;
            await Task.Delay(900);

            vm.Thread.Add(new ChatMessage
            {
                Role = "user", Kind = CpMsgKind.User,
                Text = "list all doors in this model",
                Time = DateTime.Now.ToString("h:mm tt"),
            });

            var reasoning = new List<ReasoningStep>
            {
                new ReasoningStep { StepId = "r1", Label = "Thinking", Text = "", State = ReasoningState.Running },
            };
            var steps = new List<ProgressStep>();
            var blocks = new List<TurnBlock>();

            void Show(bool streamingReply = false, string text = "")
            {
                var msg = new ChatMessage
                {
                    Role = "ai", Kind = CpMsgKind.Thinking,
                    StreamingReply = streamingReply, Text = text,
                    LiveReasoningSteps = new List<ReasoningStep>(reasoning),
                    LiveSteps = new List<ProgressStep>(steps),
                    Blocks = blocks.Count > 0 ? new List<TurnBlock>(blocks) : null,
                };
                for (int i = vm.Thread.Count - 1; i >= 0; i--)
                    if (vm.Thread[i].Kind == CpMsgKind.Thinking) { vm.Thread[i] = msg; return; }
                vm.Thread.Add(msg);
            }

            ProgressStep Step(string id, string label, string detail = "")
            {
                var s = new ProgressStep { StepId = id, Label = label, Detail = detail, State = StepState.Running };
                steps.Add(s);
                return s;
            }
            void Done(ProgressStep s) { s.State = StepState.Done; s.EndedUtc = DateTime.UtcNow; }

            // 1 — thinking prose streams in.
            Show();
            for (int i = 3; i <= Thinking.Length; i += 3)
            {
                reasoning[0].Text = Thinking.Substring(0, i);
                Show();
                await Task.Delay(16);
            }
            reasoning[0].Text = Thinking;
            reasoning[0].State = ReasoningState.Done;

            // 2 — read the request.
            var s1 = Step("s1", "Read the request", "list doors → filter category Doors");
            Show(); await Task.Delay(420);
            Done(s1);

            // 3 — query model with the determinate scan.
            var s2 = Step("call_1", "Query model");
            Show(); await Task.Delay(250);
            s2.Total = 62;
            for (int n = 1; n <= 62; n++)
            {
                s2.Current = n;
                Show();
                await Task.Delay(36);
            }
            blocks.Add(new TurnBlock
            {
                Kind = TurnBlockKind.ToolCard,
                ToolResult = new RevitWebAppSync.Services.ToolResultEvent
                {
                    ToolCallId = "call_1", Tool = "find_elements_by_filter", Ok = true, DurationMs = 2600,
                    ArgsDigest = "{\"category\": \"Doors\"}",
                    ResultDigest = "{'category': 'Doors', 'matches': [{'id': 1042809, 'name': '(PTa001a) 1800 x 2100 sp-pl', 'level': 'Level 01'}, … +61 more]}",
                },
            });
            Done(s2); Show(); await Task.Delay(300);

            // 4 — count by type.
            var s3 = Step("call_2", "Count by type");
            Show(); await Task.Delay(500);
            blocks.Add(new TurnBlock
            {
                Kind = TurnBlockKind.ToolCard,
                ToolResult = new RevitWebAppSync.Services.ToolResultEvent
                {
                    ToolCallId = "call_2", Tool = "count_by", Ok = true, DurationMs = 300,
                    ArgsDigest = "{\"by\": [\"type_name\", \"level\"]}",
                    ResultDigest = "{'PTh300a': 21, 'PTt760b': 15, 'PTr680a': 8, 'PTn520a': 4, 'PT2p600a': 4, 'others': 10}",
                },
            });
            Done(s3); Show(); await Task.Delay(250);

            // 5 — validate, compose.
            var s4 = Step("s4", "Validate results", "62 unique · 0 duplicates · 0 errors");
            Show(); await Task.Delay(480);
            Done(s4);
            var s5 = Step("s5", "Compose answer", "summary + door type table");
            Show(); await Task.Delay(200);

            // 6 — the answer streams (the same StreamingReply path the live
            // OnCodeStream handler renders through).
            for (int i = 24; i <= Answer.Length; i += 24)
            {
                Show(streamingReply: true, text: Answer.Substring(0, i));
                await Task.Delay(40);
            }
            Done(s5);

            // 7 — final message replaces the live one (RenderRouteResult's job
            // in a real turn).
            for (int i = vm.Thread.Count - 1; i >= 0; i--)
                if (vm.Thread[i].Kind == CpMsgKind.Thinking)
                {
                    vm.Thread[i] = new ChatMessage
                    {
                        Role = "ai", Kind = CpMsgKind.AiReply,
                        Text = Answer,
                        Time = DateTime.Now.ToString("h:mm tt"),
                        ReasoningSteps = reasoning,
                        Steps = steps,
                        Blocks = blocks,
                    };
                    break;
                }
        }
    }
}
