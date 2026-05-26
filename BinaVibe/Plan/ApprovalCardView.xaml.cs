// ApprovalCardView — gated MUTATE prompt with Preview / Approve / Reject.
// Stub only; wires into Copilot pane in Step 5 alongside policy DSL.

using System;
using System.Windows.Controls;

namespace BinaVibe.Plan
{
    public partial class ApprovalCardView : UserControl
    {
        public event EventHandler? Preview;
        public event EventHandler? Approve;
        public event EventHandler? Reject;

        public ApprovalCardView()
        {
            InitializeComponent();
            PreviewButton.Click += (s, e) => Preview?.Invoke(this, EventArgs.Empty);
            ApproveButton.Click += (s, e) => Approve?.Invoke(this, EventArgs.Empty);
            RejectButton.Click += (s, e) => Reject?.Invoke(this, EventArgs.Empty);
        }

        public void Bind(ApprovalViewModel vm)
        {
            ReasonText.Text = vm.Reason;
            GoalText.Text = vm.Goal;
            AffectedText.Text = vm.AffectedSummary;
            TimeoutText.Text = $"Times out in {vm.TimeoutSeconds}s";
        }
    }

    public sealed class ApprovalViewModel
    {
        public int StepId { get; init; }
        public string Reason { get; init; } = "";
        public string Goal { get; init; } = "";
        public string AffectedSummary { get; init; } = "";
        public int TimeoutSeconds { get; init; } = 300;
    }
}
