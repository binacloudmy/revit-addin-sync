using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

            // Mount the palette in THIS element's own resources. Changing
            // App.Resources does not re-invalidate a {DynamicResource} binding
            // inside Revit's dockable-pane host — a local-scope Remove+Insert does.
            // Without this the pane's chrome stays light while the styled controls
            // and code-drawn screens flip to dark: measured 2026-08-04, white
            // background behind dark cards and dark input boxes.
            _localTheme = CopilotTheme.NewThemeDictionary();
            _localThemeDark = CopilotTheme.IsDark;
            Resources.MergedDictionaries.Add(_localTheme);
            Loaded += (_, __) =>
            {
                CopilotTheme.ThemeChanged -= SwapLocalTheme;
                CopilotTheme.ThemeChanged += SwapLocalTheme;
                SwapLocalTheme();   // re-sync if the theme flipped while hidden
            };
            Unloaded += (_, __) => CopilotTheme.ThemeChanged -= SwapLocalTheme;

            ThemeBtn.Click += (_, __) => CopilotTheme.Toggle();
            _vm.PropertyChanged += OnVm;
            UpdateThemeIcon();
            ShowScreen();
        }

        private ResourceDictionary _localTheme;
        private bool _localThemeDark;

        private void SwapLocalTheme()
        {
            // No-op when the mounted dictionary already matches — every pane re-show
            // reaches this via Loaded; only an actual flip needs a rebuild.
            if (_localTheme != null && _localThemeDark == CopilotTheme.IsDark)
            {
                UpdateThemeIcon();
                return;
            }
            var dicts = Resources.MergedDictionaries;
            var next = CopilotTheme.NewThemeDictionary();
            var i = _localTheme != null ? dicts.IndexOf(_localTheme) : -1;
            if (i >= 0) { dicts.RemoveAt(i); dicts.Insert(i, next); }
            else dicts.Add(next);
            _localTheme = next;
            _localThemeDark = CopilotTheme.IsDark;
            UpdateThemeIcon();
        }

        // Same glyphs as the Copilot header: moon offers dark, sun offers light.
        private const string MoonData = "M21,12.8 A9,9 0 1 1 11.2,3 A7,7 0 0 0 21,12.8 Z";
        private const string SunData =
            "M12,7.5 A4.5,4.5 0 1 1 11.99,7.5 Z M12,1.5 V3.6 M12,20.4 V22.5 " +
            "M1.5,12 H3.6 M20.4,12 H22.5 M4.4,4.4 L5.9,5.9 M18.1,18.1 L19.6,19.6 " +
            "M19.6,4.4 L18.1,5.9 M5.9,18.1 L4.4,19.6";

        private void UpdateThemeIcon()
        {
            if (ThemeGlyph == null) return;
            ThemeGlyph.Data = Geometry.Parse(CopilotTheme.IsDark ? SunData : MoonData);
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
