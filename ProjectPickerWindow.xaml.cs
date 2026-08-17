using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RevitWebAppSync
{
    public partial class ProjectPickerWindow : Window
    {
        private readonly string _accessToken;
        private readonly int _currentProjectId;
        private List<ProjectInfo> _allProjects = new List<ProjectInfo>();

        public int SelectedProjectId { get; private set; }
        public string SelectedProjectName { get; private set; }

        /// <param name="currentProjectId">
        /// Pre-selects and labels the project already in use, so the user can see
        /// what they are changing away from instead of guessing.
        /// </param>
        public ProjectPickerWindow(string accessToken, int currentProjectId = 0)
        {
            InitializeComponent();
            _accessToken = accessToken;
            _currentProjectId = currentProjectId;

            ProjectsListBox.SelectionChanged += ProjectsListBox_SelectionChanged;
            Loaded += ProjectPickerWindow_Loaded;
        }

        private async void ProjectPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadProjectsAsync();
            SearchBox.Focus();   // straight to typing; the list is long
        }

        private async Task LoadProjectsAsync()
        {
            try
            {
                SetLoading(true);
                HideError();

                var projects = await Task.Run(() => BinaApiService.GetUserProjectsAsync(_accessToken));

                if (projects == null || projects.Count == 0)
                {
                    SetLoading(false);
                    ShowError("No projects found for your account. Ask a project administrator to add you, then try again.");
                    CountText.Text = "";
                    return;
                }

                // Alphabetical: the API order is arbitrary, and a user scanning for
                // a name should not have to read the whole list to be sure.
                _allProjects = projects
                    .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                SetLoading(false);
                ApplyFilter();

                var current = _allProjects.FirstOrDefault(p => p.Id == _currentProjectId);
                if (current != null)
                {
                    ProjectsListBox.SelectedItem = current;
                    ProjectsListBox.ScrollIntoView(current);
                    SubtitleText.Text = $"Currently using “{current.Name}”. Your Revit syncs will be filed under the project you pick.";
                }
            }
            catch (Exception ex)
            {
                SetLoading(false);
                ShowError($"Could not load your projects: {ex.Message}");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility =
                string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string term = (SearchBox.Text ?? "").Trim();
            var shown = string.IsNullOrEmpty(term)
                ? _allProjects
                : _allProjects
                    .Where(p => (p.Name ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                                || p.Id.ToString() == term)
                    .ToList();

            ProjectsListBox.ItemsSource = shown;
            EmptyPanel.Visibility = shown.Count == 0 && _allProjects.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            CountText.Text = _allProjects.Count == 0
                ? ""
                : shown.Count == _allProjects.Count
                    ? $"{_allProjects.Count} projects"
                    : $"{shown.Count} of {_allProjects.Count} projects";

            // Typing narrows to one result often enough that pre-selecting it
            // makes Enter do the obvious thing.
            if (shown.Count == 1) ProjectsListBox.SelectedIndex = 0;
        }

        private void SetLoading(bool loading)
        {
            LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            ProjectsListBox.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            SearchBox.IsEnabled = !loading;
        }

        private void ProjectsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectButton.IsEnabled = ProjectsListBox.SelectedItem != null;
        }

        private void ProjectsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ProjectsListBox.SelectedItem != null) SelectProject();
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e) => SelectProject();

        private void SelectProject()
        {
            var selected = ProjectsListBox.SelectedItem as ProjectInfo;
            if (selected == null)
            {
                ShowError("Pick a project from the list first.");
                return;
            }

            SelectedProjectId = selected.Id;
            SelectedProjectName = selected.Name;

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
            ErrorBox.Visibility = Visibility.Visible;
        }

        private void HideError() => ErrorBox.Visibility = Visibility.Collapsed;
    }
}
