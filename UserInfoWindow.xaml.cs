using System;
using System.Windows;

namespace BinaConnector
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
            UserNameText.Text = config.UserName ?? "Unknown";
            ProjectNameText.Text = config.ProjectName ?? $"Project ID: {config.ProjectId}";
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
