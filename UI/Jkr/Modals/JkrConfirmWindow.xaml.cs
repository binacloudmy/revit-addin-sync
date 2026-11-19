using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    /// <summary>
    /// Confirm dialog before every model write (Clear Sheet "confirm changes").
    /// Lists each change as "title · scope / old → new", ready-made fixes and AI
    /// fixer proposals in their own groups. From 500 affected elements it says
    /// Revit may pause.
    ///
    /// Esc, Cancel and a click on the scrim cancel; Enter confirms when the confirm
    /// button is enabled. The scrim covers the window that owns the pane.
    /// </summary>
    public partial class JkrConfirmWindow : Window
    {
        private readonly JkrConfirmModel _model;

        private JkrConfirmWindow(JkrConfirmModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            InitializeComponent();

            DataContext = model;
            AutomationPropertiesHelper(model.Title);
            Refresh();

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, __) =>
            {
                ConfirmBtn.Focus();
            };
        }

        /// <summary>Show the dialog modally over Revit's main window. Returns true
        /// only when the user confirmed; the caller writes only then.</summary>
        public static bool Ask(JkrConfirmModel model, FrameworkElement anchor)
        {
            var w = new JkrConfirmWindow(model);
            if (!w.CoverRevitWindow(anchor))
            {
                // Fallback: a card-sized window (SizeToContent) centred on the pane's
                // window; the scrim margin leaves room for the shadow.
                Window owner = null;
                try { owner = anchor != null ? Window.GetWindow(anchor) : null; } catch { }
                if (owner != null && owner != w) w.Owner = owner;
                w.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
                w.Card.MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 120);
            }
            w.ShowDialog();
            return model.Confirmed;
        }

        /// <summary>Cover Revit's main window with the scrim (see <see cref="RevitWindowCover"/>).
        /// The card is capped to the window height; its change list scrolls.</summary>
        private bool CoverRevitWindow(FrameworkElement anchor)
        {
            return RevitWindowCover.Cover(this, anchor,
                h => Card.MaxHeight = Math.Max(240, h - Card.Margin.Top - Card.Margin.Bottom));
        }

        private void AutomationPropertiesHelper(string name)
        {
            System.Windows.Automation.AutomationProperties.SetName(Card, name);
            Title = name;
        }

        private void Refresh() => ConfirmBtn.IsEnabled = _model.CanConfirm;

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_model.TryConfirm()) Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => Close();

        // Clicks inside the card must not reach the scrim.
        private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                // Enter on Cancel cancels; otherwise Enter confirms when allowed.
                if (Keyboard.FocusedElement == CancelBtn) { Close(); e.Handled = true; return; }
                if (_model.TryConfirm()) Close();
                e.Handled = true;
            }
        }
    }
}
