using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitWebAppSync
{
    public partial class ProjectPickerWindow : Window
    {
        private readonly string _accessToken;

        public int SelectedProjectId { get; private set; }
        public string SelectedProjectName { get; private set; }
        public string SelectedBimRole { get; private set; }
        public List<string> SelectedDisciplineTypes { get; private set; }

        public ProjectPickerWindow(string accessToken)
        {
            InitializeComponent();
            _accessToken = accessToken;

            // Enable select button when selection changes
            ProjectsListBox.SelectionChanged += ProjectsListBox_SelectionChanged;

            // Load projects on window loaded
            Loaded += ProjectPickerWindow_Loaded;
        }

        private async void ProjectPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadProjectsAsync();
        }

        private async Task LoadProjectsAsync()
        {
            try
            {
                LoadingPanel.Visibility = Visibility.Visible;
                ProjectsListBox.Visibility = Visibility.Collapsed;
                HideError();

                var projects = await Task.Run(() => BinaApiService.GetUserProjectsAsync(_accessToken));

                if (projects == null || projects.Count == 0)
                {
                    ShowError("No projects found. Please contact your administrator.");
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    return;
                }

                ProjectsListBox.ItemsSource = projects;
                ProjectsListBox.Visibility = Visibility.Visible;
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"Failed to load projects: {ex.Message}");
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ProjectsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectButton.IsEnabled = ProjectsListBox.SelectedItem != null;
        }

        private void ProjectsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProjectsListBox.SelectedItem != null)
            {
                SelectProject();
            }
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            SelectProject();
        }

        private void SelectProject()
        {
            var selectedProject = ProjectsListBox.SelectedItem as ProjectInfo;
            if (selectedProject == null)
            {
                ShowError("Please select a project.");
                return;
            }

            SelectedProjectId = selectedProject.Id;
            SelectedProjectName = selectedProject.Name;
            SelectedBimRole = selectedProject.BimRole;
            SelectedDisciplineTypes = selectedProject.DisciplineTypes;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorMessage.Text = message;
            ErrorMessage.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorMessage.Visibility = Visibility.Collapsed;
        }
    }
}
