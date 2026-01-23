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
            RoleText.Text = FormatRole(config.BimRole);
            DisciplinesText.Text = FormatDisciplines(config.DisciplineTypes);
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

        private string FormatRole(string bimRole)
        {
            if (string.IsNullOrEmpty(bimRole))
                return "No role assigned";

            return bimRole switch
            {
                "BIM_MANAGER" => "BIM Manager",
                "BIM_COORDINATOR" => "BIM Coordinator",
                "BIM_MODELLER" => "BIM Modeller",
                _ => bimRole
            };
        }

        private string FormatDisciplines(System.Collections.Generic.List<string> disciplines)
        {
            if (disciplines == null || disciplines.Count == 0)
                return "All disciplines";

            return string.Join(", ", disciplines);
        }
    }
}
