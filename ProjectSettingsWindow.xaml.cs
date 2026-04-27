using System.Windows;
using System.Windows.Controls;

namespace BinaConnector
{
    public partial class ProjectSettingsWindow : Window
    {
        private readonly BinaConfig _config;
        private Settings _settings;
        private bool _projectChanged;
        private int _newProjectId;
        private string _newProjectName;

        public ProjectSettingsWindow(BinaConfig config, Settings settings)
        {
            InitializeComponent();
            _config = config;
            _settings = settings;

            ProjectNameText.Text = string.IsNullOrEmpty(config.ProjectName)
                ? "(no project selected)"
                : config.ProjectName;

            // Select the discipline ComboBoxItem matching settings.DefaultDiscipline
            string current = string.IsNullOrEmpty(settings.DefaultDiscipline) ? "Ask" : settings.DefaultDiscipline;
            foreach (ComboBoxItem item in DisciplineComboBox.Items)
            {
                if ((item.Tag as string) == current)
                {
                    DisciplineComboBox.SelectedItem = item;
                    break;
                }
            }
            if (DisciplineComboBox.SelectedItem == null) DisciplineComboBox.SelectedIndex = 0;

            ConfirmCheckBox.IsChecked = settings.ConfirmBeforeUploading;
        }

        private void ChangeProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.AccessToken))
            {
                ShowMessage("Your session has expired. Please sign in again before changing project.");
                return;
            }

            var picker = new ProjectPickerWindow(_config.AccessToken) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                _projectChanged = true;
                _newProjectId = picker.SelectedProjectId;
                _newProjectName = picker.SelectedProjectName;
                ProjectNameText.Text = _newProjectName;
                ClearMessage();
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Persist preferences
            _settings.DefaultDiscipline = (DisciplineComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ask";
            _settings.ConfirmBeforeUploading = ConfirmCheckBox.IsChecked == true;
            SettingsStore.Save(_settings);

            // Persist project change (if any)
            if (_projectChanged)
            {
                _config.ProjectId = _newProjectId;
                _config.ProjectName = _newProjectName;
                _config.Save();
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowMessage(string text)
        {
            MessageText.Text = text;
            MessageText.Visibility = Visibility.Visible;
        }

        private void ClearMessage()
        {
            MessageText.Text = string.Empty;
            MessageText.Visibility = Visibility.Collapsed;
        }
    }
}
