// ApprovalCardView — per-gate approval card in the Copilot chat.
// Wired to a real PendingApproval from /execute-plan. Three actions:
//   • Preview  → CopilotViewModel selects affected elements in Revit
//                so the drafter can eyeball before deciding.
//   • Approve  → CopilotViewModel adds the gate_id to approval_tokens
//                and re-posts /execute-plan.
//   • Reject   → CopilotViewModel marks the card cancelled; the agent
//                stops at this step on the next round.

using System;
using System.Linq;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Models;

namespace BinaVibe.Plan
{
    public partial class ApprovalCardView : UserControl
    {
        public event EventHandler Approved;
        public event EventHandler Rejected;
        public event EventHandler PreviewRequested;

        public ApprovalCardView()
        {
            InitializeComponent();
            PreviewButton.Click += (s, e) => PreviewRequested?.Invoke(this, EventArgs.Empty);
            ApproveButton.Click += (s, e) => Approved?.Invoke(this, EventArgs.Empty);
            RejectButton.Click += (s, e) => Rejected?.Invoke(this, EventArgs.Empty);
            TimeoutText.Text = "";
        }

        public void Bind(PendingApproval pa)
        {
            if (pa == null) return;
            ReasonText.Text = pa.Reason ?? "policy gate";
            GoalText.Text = $"{pa.Tool}";
            AffectedText.Text = SummarizeArgs(pa.Args);
        }

        private static string SummarizeArgs(JObject args)
        {
            if (args == null) return "(no arguments)";
            var ids = TryExtractIds(args);
            if (ids != null && ids.Count > 0)
                return $"{ids.Count} element(s): {string.Join(", ", ids.Take(10))}" + (ids.Count > 10 ? "…" : "");
            return string.Join(", ", args.Properties().Select(p => $"{p.Name}={Trim(p.Value?.ToString())}").Take(5));
        }

        private static System.Collections.Generic.List<long> TryExtractIds(JObject args)
        {
            if (args == null) return null;
            var list = new System.Collections.Generic.List<long>();
            JToken arr = args["element_ids"] ?? args["elementIds"];
            if (arr is JArray ja)
            {
                foreach (var v in ja)
                    if (long.TryParse(v?.ToString(), out var n)) list.Add(n);
            }
            else
            {
                JToken one = args["element_id"] ?? args["elementId"];
                if (one != null && long.TryParse(one.ToString(), out var n)) list.Add(n);
            }
            return list;
        }

        private static string Trim(string s, int cap = 30)
            => s == null ? "" : (s.Length <= cap ? s : s.Substring(0, cap - 1) + "…");

        public JObject CurrentArgs { get; private set; }

        public void BindWithArgs(PendingApproval pa)
        {
            CurrentArgs = pa?.Args;
            Bind(pa);
        }
    }
}
