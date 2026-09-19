using System;
using System.Windows;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>Small modal behind the pane's gear: the six numbers, the two hint lists, the
    /// exclusion globs and the template override. OK validates, writes back and saves.</summary>
    public partial class CadToBimSettingsWindow : Window
    {
        private readonly CadToBimSettings _settings;
        private readonly CadToBimSettingsDraft _draft;

        public CadToBimSettingsWindow(CadToBimSettings settings)
        {
            CopilotTheme.EnsureLoaded();
            CadToBimTheme.EnsureLoaded();
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _draft = CadToBimSettingsDraft.From(settings);
            InitializeComponent();
            DataContext = _draft;
            // A Window is not inside the pane, so it mounts the theme brushes itself.
            Resources.MergedDictionaries.Add(CopilotTheme.NewThemeDictionary());
            Resources.MergedDictionaries.Add(CadToBimTheme.NewThemeDictionary());
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the project template for new files",
                Filter = "Revit template (*.rte)|*.rte|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) == true) _draft.TemplatePath = dialog.FileName;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            string error = _draft.Validate();
            if (error != null)
            {
                ErrorText.Text = error;
                return;
            }

            _draft.WriteTo(_settings);
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                // Applied for this session even when %APPDATA% is read-only; say so.
                ErrorText.Text = "Settings applied, but could not be saved: " + ex.Message;
                DialogResult = true;
                return;
            }
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
