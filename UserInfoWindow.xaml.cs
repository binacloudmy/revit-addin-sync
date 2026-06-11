using System;
using System.Windows;

namespace RevitWebAppSync
{
    public partial class UserInfoWindow : Window
    {
        private readonly BinaConfig _config;

        public bool LoggedOut { get; private set; }
        public bool SwitchProject { get; private set; }

        public UserInfoWindow(BinaConfig config)
        {
            InitializeComponent();
            _config = config;

            // Display user info
            UserNameText.Text = config.UserName ?? config.Email ?? "Unknown";
            ProjectNameText.Text = config.ProjectName ?? $"Project ID: {config.ProjectId}";
            OrgIdTextBox.Text = config.OrgId.HasValue ? config.OrgId.Value.ToString() : "";
        }

        private void SaveOrgIdButton_Click(object sender, RoutedEventArgs e)
        {
            var raw = (OrgIdTextBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                _config.OrgId = null;
            }
            else if (int.TryParse(raw, out var id) && id > 0)
            {
                _config.OrgId = id;
            }
            else
            {
                MessageBox.Show(
                    "Team ID must be a positive whole number (or empty to clear).",
                    "Invalid team id",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            _config.Save();
            MessageBox.Show(
                _config.OrgId.HasValue
                    ? $"Team id set to {_config.OrgId.Value}. The 'My team' option is now available in the command save dialog."
                    : "Team id cleared. Commands will save as personal only.",
                "Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to logout?",
                "Confirm Logout",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                LoggedOut = true;
                DialogResult = true;
                Close();
            }
        }

        private void SwitchProjectButton_Click(object sender, RoutedEventArgs e)
        {
            SwitchProject = true;
            DialogResult = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
