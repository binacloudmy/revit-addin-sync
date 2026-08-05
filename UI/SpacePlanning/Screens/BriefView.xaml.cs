using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitWebAppSync.UI.SpacePlanning.Screens
{
    /// <summary>
    /// The brief form — where the flow starts now that there is no chat composer to
    /// type "/massing …" into.
    ///
    /// The three optional fields are read on submit rather than data-bound: a
    /// nullable-double TwoWay binding turns a half-typed "12." into a validation
    /// error and silently keeps the OLD value, which is exactly the wrong behaviour
    /// for a field whose empty state is meaningful (blank = don't send it at all).
    /// </summary>
    public partial class BriefView : UserControl
    {
        private SpacePlanningViewModel Vm => DataContext as SpacePlanningViewModel;
        private SpacePlanningViewModel _hooked;

        public BriefView()
        {
            InitializeComponent();

            SuggestBtn.Click += (_, __) => Submit();
            // Ctrl+Enter submits from the brief box; plain Enter inserts a newline,
            // because a brief is genuinely multi-line.
            BriefBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    Submit();
                    e.Handled = true;
                }
            };
            BriefBox.TextChanged += (_, __) =>
            {
                if (Vm != null) Vm.Brief = BriefBox.Text;
                SyncEnabled();
            };

            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => { Hook(); Render(); BriefBox.Focus(); };
            Unloaded += (_, __) =>
            {
                if (_hooked != null) _hooked.PropertyChanged -= OnVm;
                _hooked = null;
            };
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            Render();
        }

        private void OnVm(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SpacePlanningViewModel.BriefError)
                || e.PropertyName == nameof(SpacePlanningViewModel.Brief)
                || e.PropertyName == nameof(SpacePlanningViewModel.SiteSummary)
                || e.PropertyName == nameof(SpacePlanningViewModel.Screen))
                Render();
        }

        private void Render()
        {
            var vm = Vm;
            if (vm == null) return;

            // Restore the brief when coming back from the plan — the user pressed
            // Back to tweak it, not to retype it.
            if (BriefBox.Text != (vm.Brief ?? "")) BriefBox.Text = vm.Brief ?? "";

            var site = vm.SiteSummary;
            SiteFound.Text = site ?? "";
            SiteCard.Visibility = string.IsNullOrWhiteSpace(site)
                ? Visibility.Collapsed : Visibility.Visible;

            var err = vm.BriefError;
            ErrorText.Text = err ?? "";
            ErrorCard.Visibility = string.IsNullOrWhiteSpace(err) ? Visibility.Collapsed : Visibility.Visible;
            SyncEnabled();
        }

        private void SyncEnabled() =>
            SuggestBtn.IsEnabled = !string.IsNullOrWhiteSpace(BriefBox.Text);

        private void Submit()
        {
            var vm = Vm;
            if (vm == null) return;
            var brief = (BriefBox.Text ?? "").Trim();
            if (brief.Length == 0) return;

            vm.Brief = brief;
            vm.SiteAreaM2 = ParseOptional(SiteAreaBox.Text);
            vm.SetbackM = ParseOptional(SetbackBox.Text);
            vm.TargetGfaM2 = ParseOptional(TargetGfaBox.Text);
            vm.SuggestCommand?.Execute(null);
        }

        /// <summary>Blank or unparseable → null, i.e. "don't send this field". Parsed
        /// invariantly so a comma decimal on a Malay Windows locale can't silently
        /// become a different number.</summary>
        private static double? ParseOptional(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var cleaned = text.Trim().Replace(",", "").Replace("m²", "").Replace("m2", "").Trim();
            return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                   && v > 0
                ? (double?)v
                : null;
        }
    }
}
