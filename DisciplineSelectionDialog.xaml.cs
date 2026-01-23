using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace RevitWebAppSync
{
    public partial class DisciplineSelectionDialog : Window
    {
        public DisciplineType? SelectedDiscipline { get; private set; }
        public int? SelectedFolderId { get; private set; }
        public string SelectedFolderName { get; private set; }
        public bool Confirmed { get; private set; }

        private readonly int _projectId;
        private readonly string _accessToken;
        private List<WipFolderInfo> _allFolders;

        public DisciplineSelectionDialog(string fileName, List<string> allowedDisciplines = null, string bimRole = null, int projectId = 0, string accessToken = null)
        {
            InitializeComponent();
            FileNameText.Text = fileName;
            _projectId = projectId;
            _accessToken = accessToken;

            // Filter discipline options based on user's access
            FilterDisciplineOptions(allowedDisciplines, bimRole);

            // Hook up discipline change events
            ArchitectureOption.Checked += DisciplineOption_Checked;
            StructureOption.Checked += DisciplineOption_Checked;
            MechanicalOption.Checked += DisciplineOption_Checked;
            ElectricalOption.Checked += DisciplineOption_Checked;
            MainFileOption.Checked += DisciplineOption_Checked;

            // Load initial folders
            LoadFoldersForSelectedDiscipline();
        }

        private void DisciplineOption_Checked(object sender, RoutedEventArgs e)
        {
            LoadFoldersForSelectedDiscipline();
        }

        private async void LoadFoldersForSelectedDiscipline()
        {
            LogToDesktop($"LoadFoldersForSelectedDiscipline: accessToken={!string.IsNullOrEmpty(_accessToken)}, projectId={_projectId}");

            if (string.IsNullOrEmpty(_accessToken) || _projectId == 0)
            {
                FolderComboBox.IsEnabled = false;
                NoFoldersMessage.Text = "Login required to load folders";
                NoFoldersMessage.Visibility = Visibility.Visible;
                return;
            }

            // Get selected discipline type
            string disciplineType = GetSelectedDisciplineType();
            LogToDesktop($"LoadFoldersForSelectedDiscipline: disciplineType={disciplineType}");

            // Show loading state
            FolderLoadingPanel.Visibility = Visibility.Visible;
            FolderComboBox.IsEnabled = false;
            FolderComboBox.ItemsSource = null;
            NoFoldersMessage.Visibility = Visibility.Collapsed;

            try
            {
                // Fetch folders from API
                var folders = await Task.Run(() => BinaApiService.GetWipFoldersAsync(_accessToken, _projectId, disciplineType));

                // Update UI on main thread
                FolderLoadingPanel.Visibility = Visibility.Collapsed;

                LogToDesktop($"LoadFoldersForSelectedDiscipline: folders returned = {folders?.Count ?? -1}");

                if (folders == null || folders.Count == 0)
                {
                    FolderComboBox.IsEnabled = false;
                    NoFoldersMessage.Visibility = Visibility.Visible;
                    _allFolders = new List<WipFolderInfo>();
                }
                else
                {
                    _allFolders = folders;
                    FolderComboBox.ItemsSource = folders;
                    FolderComboBox.IsEnabled = true;
                    FolderComboBox.SelectedIndex = 0; // Select first folder by default
                    NoFoldersMessage.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                LogToDesktop($"LoadFoldersForSelectedDiscipline: Exception: {ex.Message}");
                FolderLoadingPanel.Visibility = Visibility.Collapsed;
                FolderComboBox.IsEnabled = false;
                NoFoldersMessage.Text = "Failed to load folders";
                NoFoldersMessage.Visibility = Visibility.Visible;
            }
        }

        private static void LogToDesktop(string message)
        {
            try
            {
                string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "bina_folder_log.txt");
                string timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                System.IO.File.AppendAllText(logPath, timestampedMessage + Environment.NewLine);
            }
            catch { /* Ignore logging errors */ }
        }

        private string GetSelectedDisciplineType()
        {
            if (ArchitectureOption.IsChecked == true)
                return "Architecture";
            if (StructureOption.IsChecked == true)
                return "Structure";
            if (MechanicalOption.IsChecked == true)
                return "Mechanical";
            if (ElectricalOption.IsChecked == true)
                return "Electrical";
            if (MainFileOption.IsChecked == true)
                return null; // MainFile doesn't filter by discipline
            return null;
        }

        private void FilterDisciplineOptions(List<string> allowedDisciplines, string bimRole)
        {
            // BIM_MANAGER has access to all disciplines (allowedDisciplines is null/empty)
            bool hasFullAccess = bimRole == "BIM_MANAGER" || allowedDisciplines == null || allowedDisciplines.Count == 0;

            if (hasFullAccess)
            {
                // Show all options, default to MainFile
                MainFileOption.IsChecked = true;
                return;
            }

            // Hide options the user doesn't have access to
            ArchitectureOption.Visibility = allowedDisciplines.Contains("Architecture") ? Visibility.Visible : Visibility.Collapsed;
            StructureOption.Visibility = allowedDisciplines.Contains("Structure") ? Visibility.Visible : Visibility.Collapsed;
            MechanicalOption.Visibility = allowedDisciplines.Contains("Mechanical") ? Visibility.Visible : Visibility.Collapsed;
            ElectricalOption.Visibility = allowedDisciplines.Contains("Electrical") ? Visibility.Visible : Visibility.Collapsed;

            // MainFile is only available for BIM_MANAGER
            MainFileOption.Visibility = Visibility.Collapsed;

            // Select the first visible option as default
            if (ArchitectureOption.Visibility == Visibility.Visible)
                ArchitectureOption.IsChecked = true;
            else if (StructureOption.Visibility == Visibility.Visible)
                StructureOption.IsChecked = true;
            else if (MechanicalOption.Visibility == Visibility.Visible)
                MechanicalOption.IsChecked = true;
            else if (ElectricalOption.Visibility == Visibility.Visible)
                ElectricalOption.IsChecked = true;
        }

        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            // Determine which option is selected
            if (ArchitectureOption.IsChecked == true)
                SelectedDiscipline = DisciplineType.Architecture;
            else if (StructureOption.IsChecked == true)
                SelectedDiscipline = DisciplineType.Structure;
            else if (MechanicalOption.IsChecked == true)
                SelectedDiscipline = DisciplineType.Mechanical;
            else if (ElectricalOption.IsChecked == true)
                SelectedDiscipline = DisciplineType.Electrical;
            else if (MainFileOption.IsChecked == true)
                SelectedDiscipline = DisciplineType.MainFile;
            else
                SelectedDiscipline = DisciplineType.MainFile; // Default fallback

            // Get selected folder
            if (FolderComboBox.SelectedItem is WipFolderInfo selectedFolder)
            {
                SelectedFolderId = selectedFolder.Id;
                SelectedFolderName = selectedFolder.Name;
                LogToDesktop($"UploadButton_Click: Selected folder ID={selectedFolder.Id}, Name={selectedFolder.Name}");
            }
            else
            {
                SelectedFolderId = null;
                SelectedFolderName = null;
                LogToDesktop($"UploadButton_Click: No folder selected!");
            }

            Confirmed = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            DialogResult = false;
            Close();
        }
    }
}
