// PlanCardView — WPF UserControl rendering a v2 Plan inside the Copilot
// pane chat thread. Bound to a PlanModel (live backend Plan, not a
// stub viewmodel).
//
// Layout: intent header, ambiguity (if any), step list with
// INSPECT/DECIDE/MUTATE/VERIFY badges, Approve / Cancel buttons.
// Edit Plan deferred to V2.1 per PRD §10.3 FR-PLAN-10.

using System;
using System.Linq;
using System.Windows.Controls;
using RevitWebAppSync.Models;

namespace BinaVibe.Plan
{
    public partial class PlanCardView : UserControl
    {
        public event EventHandler Approve;
        public event EventHandler Cancel;

        public PlanCardView()
        {
            InitializeComponent();
            ApproveButton.Click += (s, e) => Approve?.Invoke(this, EventArgs.Empty);
            EditButton.IsEnabled = false;     // V2.1 work — disabled in MVP
            CancelButton.Click += (s, e) => Cancel?.Invoke(this, EventArgs.Empty);
        }

        public void Bind(PlanModel plan)
        {
            IntentText.Text = plan?.Intent ?? "";
            int i = 0;
            StepsList.ItemsSource = plan?.Steps?
                .OrderBy(s => s.Id)
                .Select(s =>
                {
                    var (bg, fg) = BadgeColors(s.Type, s.RequiresApproval);
                    return new PlanStepRow
                    {
                        Index = (++i).ToString() + ".",
                        Type = s.Type ?? "",
                        TypeLabel = (s.Type ?? "step").ToUpperInvariant(),
                        Goal = s.Goal ?? "",
                        RequiresApproval = s.RequiresApproval,
                        BadgeBg = bg,
                        BadgeFg = fg,
                    };
                })
                .ToList();
        }

        // Category palette mirrors the Copilot tool-tile colors. MUTATE steps
        // (and anything approval-gated) read amber/red so the drafter's eye
        // lands on the consequential rows.
        private static (string bg, string fg) BadgeColors(string type, bool gated)
        {
            string t = (type ?? "").Trim().ToLowerInvariant();
            if (gated || t == "mutate") return ("#fee2e2", "#b91c1c");   // red
            switch (t)
            {
                case "inspect": return ("#dbeafe", "#1d4ed8");           // blue
                case "decide": return ("#ede9fe", "#6d28d9");            // violet
                case "verify": return ("#dcfce7", "#15803d");           // green
                default: return ("#f1f5f9", "#475569");                  // slate
            }
        }

        public sealed class PlanStepRow
        {
            public string Index { get; set; }
            public string Type { get; set; }
            public string TypeLabel { get; set; }
            public string Goal { get; set; }
            public bool RequiresApproval { get; set; }
            public string BadgeBg { get; set; }
            public string BadgeFg { get; set; }
        }
    }
}
