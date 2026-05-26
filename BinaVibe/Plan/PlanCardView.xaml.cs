// PlanCardView — WPF UserControl rendering a v2 Plan inside the Copilot
// pane chat thread. Bound to a PlanViewModel that mirrors the
// orchestrator's Plan JSON.
//
// Stub only; wires into the Copilot pane in Step 2 once SSE consumption
// lands in CopilotViewModel.

using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace BinaVibe.Plan
{
    public partial class PlanCardView : UserControl
    {
        public event EventHandler? Approve;
        public event EventHandler? Edit;
        public event EventHandler? Cancel;

        public PlanCardView()
        {
            InitializeComponent();
            ApproveButton.Click += (s, e) => Approve?.Invoke(this, EventArgs.Empty);
            EditButton.Click += (s, e) => Edit?.Invoke(this, EventArgs.Empty);
            CancelButton.Click += (s, e) => Cancel?.Invoke(this, EventArgs.Empty);
        }

        public void Bind(PlanViewModel vm)
        {
            IntentText.Text = vm.Intent;
            StepsList.ItemsSource = vm.Steps;
        }
    }

    public sealed class PlanStepViewModel
    {
        public int Id { get; init; }
        public string Type { get; init; } = "INSPECT";
        public string Goal { get; init; } = "";
        public string? Tool { get; init; }
        public string ExpectedOutcome { get; init; } = "";
        public bool RequiresApproval { get; init; }
    }

    public sealed class PlanViewModel
    {
        public string Intent { get; init; } = "";
        public List<PlanStepViewModel> Steps { get; init; } = new();
        public List<string> Ambiguities { get; init; } = new();
        public double EstimatedSeconds { get; init; }
    }
}
