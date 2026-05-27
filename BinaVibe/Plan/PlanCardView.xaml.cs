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
            StepsList.ItemsSource = plan?.Steps?
                .OrderBy(s => s.Id)
                .Select(s => new PlanStepRow
                {
                    Type = s.Type ?? "",
                    Goal = s.Goal ?? "",
                    RequiresApproval = s.RequiresApproval,
                })
                .ToList();
        }

        public sealed class PlanStepRow
        {
            public string Type { get; set; }
            public string Goal { get; set; }
            public bool RequiresApproval { get; set; }
        }
    }
}
