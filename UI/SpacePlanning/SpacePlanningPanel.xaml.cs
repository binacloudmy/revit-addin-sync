using System.ComponentModel;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot;                      // CopilotTheme (shared chrome)
using RevitWebAppSync.UI.SpacePlanning.Model;
using RevitWebAppSync.UI.SpacePlanning.Screens;

namespace RevitWebAppSync.UI.SpacePlanning
{
    /// <summary>
    /// The standalone Space Planning pane: brief → SOA + schemes → Build.
    ///
    /// Screens are swapped into a single ContentControl and built lazily, the same
    /// way CopilotPanel hosts its own — a pane that is opened and never used should
    /// not pay for four views.
    ///
    /// This pane needs NO Revit context pushed into it: McpJobPump is wired once in
    /// App.OnStartup and captures the UIApplication from Revit's own Idling event,
    /// so Build works whether or not the Copilot pane was ever opened.
    /// </summary>
    public partial class SpacePlanningPanel : Page
    {
        private readonly SpacePlanningViewModel _vm = new SpacePlanningViewModel();

        private BriefView _brief;
        private RunView _run;
        private PlanningView _plan;
        private OutcomeView _outcome;

        public SpacePlanningPanel()
        {
            CopilotTheme.EnsureLoaded();
            InitializeComponent();
            DataContext = _vm;

            ThemeBtn.Click += (_, __) => CopilotTheme.Toggle();
            _vm.PropertyChanged += OnVm;
            ShowScreen();
        }

        /// <summary>The view-model, so a host (or the UiHarness) can drive the pane.</summary>
        public SpacePlanningViewModel Vm => _vm;

        /// <summary>Drop a ready-made result onto the Plan screen — harness/preview only.</summary>
        public void ShowPlanningPreview(SuggestResult result, string brief = null) =>
            _vm.ShowPlanningPreview(result, brief);

        private void OnVm(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SpacePlanningViewModel.Screen)) ShowScreen();
        }

        private void ShowScreen()
        {
            switch (_vm.Screen)
            {
                case SpScreen.Running:
                    BodyHost.Content = _run ?? (_run = new RunView { DataContext = _vm });
                    return;
                case SpScreen.Plan:
                    BodyHost.Content = _plan ?? (_plan = new PlanningView { DataContext = _vm });
                    return;
                case SpScreen.Result:
                    BodyHost.Content = _outcome ?? (_outcome = new OutcomeView { DataContext = _vm });
                    return;
                default:
                    BodyHost.Content = _brief ?? (_brief = new BriefView { DataContext = _vm });
                    return;
            }
        }
    }
}
