using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Project selection dialog for choosing which project to sync files to
    /// Provides search functionality and project details display
    /// TODO: Customize UI based on your organization's project structure
    /// </summary>
    public partial class ProjectSelectionDialog : Window, INotifyPropertyChanged
    {
        #region Private Fields

        private readonly ApiService _apiService;
        private ObservableCollection<ProjectInfo> _allProjects;
        private ObservableCollection<ProjectInfo> _filteredProjects;
        private ProjectInfo _selectedProject;
        private FileMetadata _fileMetadata;

        #endregion

        #region Properties

        /// <summary>
        /// The project selected by the user
        /// </summary>
        public ProjectInfo SelectedProject
        {
            get => _selectedProject;
            private set
            {
                if (_selectedProject != value)
                {
                    _selectedProject = value;
                    OnPropertyChanged(nameof(SelectedProject));
                    UpdateSelectedProjectDetails();
                    OkButton.IsEnabled = value != null;
                }
            }
        }

        /// <summary>
        /// File metadata to display context information
        /// </summary>
        public FileMetadata FileMetadata
        {
            get => _fileMetadata;
            set
            {
                if (_fileMetadata != value)
                {
                    _fileMetadata = value;
                    OnPropertyChanged(nameof(FileMetadata));
                    UpdateFileInfo();
                }
            }
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the project selection dialog
        /// </summary>
        public ProjectSelectionDialog()
        {
            InitializeComponent();
            
            _apiService = new ApiService();
            _allProjects = new ObservableCollection<ProjectInfo>();
            _filteredProjects = new ObservableCollection<ProjectInfo>();
            
            ProjectListBox.ItemsSource = _filteredProjects;
            
            // Set up data context for binding
            DataContext = this;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Loads projects from the API and populates the list
        /// TODO: Add error handling and retry logic
        /// </summary>
        public async void LoadProjects()
        {
            try
            {
                ShowLoading(true, "Loading projects...");

                var projects = await _apiService.GetProjectsAsync();
                
                _allProjects.Clear();
                foreach (var project in projects ?? new List<ProjectInfo>())
                {
                    _allProjects.Add(project);
                }

                FilterProjects();
                
                // Try to auto-select project based on file metadata
                TryAutoSelectProject();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load projects: {ex.Message}", 
                    "Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
            finally
            {
                ShowLoading(false);
            }
        }

        #endregion

        #region Event Handlers

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Load projects when dialog is shown
            LoadProjects();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterProjects();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = string.Empty;
        }

        private void ProjectListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedProject = ProjectListBox.SelectedItem as ProjectInfo;
        }

        private void ProjectListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SelectedProject != null)
            {
                DialogResult = true;
                Close();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadProjects();
        }

        private void NewProjectButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement new project creation dialog
            MessageBox.Show(
                "New project creation is not yet implemented.\n\nPlease create the project in your web application first, then refresh this list.",
                "Feature Not Implemented",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProject != null)
            {
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Filters the project list based on search text
        /// </summary>
        private void FilterProjects()
        {
            var searchText = SearchTextBox.Text?.Trim().ToLowerInvariant();
            
            _filteredProjects.Clear();

            var projectsToShow = string.IsNullOrEmpty(searchText) 
                ? _allProjects
                : _allProjects.Where(p => MatchesSearch(p, searchText));

            // Sort projects by relevance/name
            var sortedProjects = projectsToShow
                .OrderByDescending(p => p.IsActive ? 1 : 0)  // Active projects first
                .ThenBy(p => p.DisplayName)
                .ToList();

            foreach (var project in sortedProjects)
            {
                _filteredProjects.Add(project);
            }
        }

        /// <summary>
        /// Checks if project matches search criteria
        /// </summary>
        /// <param name="project">Project to check</param>
        /// <param name="searchText">Search text (lowercase)</param>
        /// <returns>True if project matches</returns>
        private bool MatchesSearch(ProjectInfo project, string searchText)
        {
            if (project == null || string.IsNullOrEmpty(searchText))
                return true;

            // Search in project name
            if (!string.IsNullOrEmpty(project.Name) && 
                project.Name.ToLowerInvariant().Contains(searchText))
                return true;

            // Search in project number
            if (!string.IsNullOrEmpty(project.Number) && 
                project.Number.ToLowerInvariant().Contains(searchText))
                return true;

            // Search in client name
            if (!string.IsNullOrEmpty(project.ClientName) && 
                project.ClientName.ToLowerInvariant().Contains(searchText))
                return true;

            // Search in address
            if (!string.IsNullOrEmpty(project.Address) && 
                project.Address.ToLowerInvariant().Contains(searchText))
                return true;

            // TODO: Add more search criteria as needed

            return false;
        }

        /// <summary>
        /// Attempts to automatically select a project based on file metadata
        /// </summary>
        private void TryAutoSelectProject()
        {
            if (FileMetadata == null || _filteredProjects.Count == 0)
                return;

            try
            {
                // Find the best matching project
                ProjectInfo bestMatch = null;
                int bestScore = 0;

                foreach (var project in _filteredProjects)
                {
                    int score = project.CalculateMatchScore(FileMetadata);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = project;
                    }
                }

                // Only auto-select if we have a reasonably good match
                if (bestMatch != null && bestScore >= 50) // TODO: Adjust threshold as needed
                {
                    ProjectListBox.SelectedItem = bestMatch;
                    ProjectListBox.ScrollIntoView(bestMatch);
                }
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                // Auto-selection failure shouldn't break the dialog
            }
        }

        /// <summary>
        /// Updates the file information display
        /// </summary>
        private void UpdateFileInfo()
        {
            if (FileMetadata != null)
            {
                FileInfoText.Text = $"File: {FileMetadata.FileName} ({FileMetadata.FileSizeFormatted}) - Project: {FileMetadata.DisplayName}";
            }
            else
            {
                FileInfoText.Text = "File: No file information available";
            }
        }

        /// <summary>
        /// Updates the selected project details display
        /// </summary>
        private void UpdateSelectedProjectDetails()
        {
            if (SelectedProject != null)
            {
                ProjectDetailsGrid.Visibility = Visibility.Visible;
                NoSelectionText.Visibility = Visibility.Collapsed;

                SelectedProjectNameText.Text = SelectedProject.Name ?? "Not specified";
                SelectedProjectNumberText.Text = SelectedProject.Number ?? "Not specified";
                SelectedClientText.Text = SelectedProject.ClientName ?? "Not specified";
                SelectedStatusText.Text = SelectedProject.Status ?? "Unknown";
                
                LastSyncText.Text = SelectedProject.LastSyncDate?.ToString("yyyy-MM-dd HH:mm") ?? "Never";
                SyncCountText.Text = SelectedProject.SyncCount.ToString();
            }
            else
            {
                ProjectDetailsGrid.Visibility = Visibility.Collapsed;
                NoSelectionText.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Shows or hides the loading overlay
        /// </summary>
        /// <param name="show">Whether to show loading</param>
        /// <param name="message">Loading message</param>
        private void ShowLoading(bool show, string message = "Loading...")
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message;
            
            // Disable controls while loading
            ProjectListBox.IsEnabled = !show;
            SearchTextBox.IsEnabled = !show;
            RefreshButton.IsEnabled = !show;
            NewProjectButton.IsEnabled = !show;
            OkButton.IsEnabled = !show && SelectedProject != null;
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region Cleanup

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            
            // Clean up resources
            _apiService?.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Converter to hide TextBlocks when string is null or empty
    /// TODO: Move to separate Converters file if you have many converters
    /// </summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public static readonly StringToVisibilityConverter Instance = new StringToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}