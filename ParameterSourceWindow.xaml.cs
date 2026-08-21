using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Names the BINA model a set of parameters should come from, for a document
    /// that cannot name itself (ClickUp 86d3y5jxx).
    ///
    /// A model synced from Revit carries its BINA identity in ExtensibleStorage
    /// and needs none of this. One uploaded through the web does not, and BINA's
    /// idea of "the same file" is project + folder + file name — so the folder
    /// is what has to be asked for. Discipline is asked first only because it is
    /// what scopes the folder list, exactly as the sync dialog does it.
    /// </summary>
    public partial class ParameterSourceWindow : Window
    {
        private readonly SyncApiClient _api;
        private bool _loading;

        public int SelectedProjectId { get; private set; }
        public string SelectedProjectName { get; private set; }
        public int? SelectedFolderId { get; private set; }

        private sealed class DisciplineChoice
        {
            public string ApiValue { get; set; }
            public string Label { get; set; }
        }

        public ParameterSourceWindow(
            SyncApiClient api,
            string fileName,
            int defaultProjectId,
            string defaultProjectName,
            string suggestedDiscipline)
        {
            InitializeComponent();

            _api = api;
            SelectedProjectId = defaultProjectId;
            SelectedProjectName = defaultProjectName;

            IntroText.Text =
                $"\"{fileName}\" carries no BINA identity yet, so tell us which folder it came from.";

            DisciplineCombo.ItemsSource = DisciplineTypes.Selectable
                .Select(d => new DisciplineChoice { ApiValue = d.ApiValue, Label = d.Label })
                .ToList();
            DisciplineCombo.SelectedIndex = Math.Max(
                0,
                DisciplineTypes.Selectable
                    .ToList()
                    .FindIndex(d => d.ApiValue == DisciplineTypes.ToApiValue(suggestedDiscipline)));

            Loaded += async (_, __) => await LoadProjectsAsync();
        }

        private async System.Threading.Tasks.Task LoadProjectsAsync()
        {
            try
            {
                SetBusy(true, "Loading projects…");
                List<ProjectInfo> projects = await _api.GetProjectsAsync();
                ProjectCombo.ItemsSource = projects;

                var current = projects.FirstOrDefault(p => p.Id == SelectedProjectId);
                ProjectCombo.SelectedItem = current ?? projects.FirstOrDefault();
                if (ProjectCombo.SelectedItem is ProjectInfo chosen)
                {
                    SelectedProjectId = chosen.Id;
                    SelectedProjectName = chosen.Name;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load projects: {ex.Message}";
                ContinueButton.IsEnabled = false;
                return;
            }
            finally
            {
                SetBusy(false, null);
            }

            // Loaded explicitly: setting SelectedItem above fires
            // SelectionChanged while the busy flag is still set, and that
            // handler bails — which would leave the folder list empty.
            await LoadFoldersAsync();
        }

        private async System.Threading.Tasks.Task LoadFoldersAsync()
        {
            try
            {
                SetBusy(true, "Loading folders…");
                string discipline = (DisciplineCombo.SelectedItem as DisciplineChoice)?.ApiValue;
                var folders = await _api.GetFoldersAsync(SelectedProjectId, BimArea.Wip, discipline);
                FolderCombo.ItemsSource = folders;
                FolderCombo.SelectedItem = folders.FirstOrDefault();

                if (!folders.Any())
                {
                    string label = (DisciplineCombo.SelectedItem as DisciplineChoice)?.Label ?? "this discipline";
                    StatusText.Text = $"No work-in-progress folders under {label} in this project.";
                    ContinueButton.IsEnabled = false;
                    return;
                }

                StatusText.Text = null;
                ContinueButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load folders: {ex.Message}";
                ContinueButton.IsEnabled = false;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async void ProjectCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (!(ProjectCombo.SelectedItem is ProjectInfo project)) return;

            SelectedProjectId = project.Id;
            SelectedProjectName = project.Name;
            await LoadFoldersAsync();
        }

        private async void DisciplineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            await LoadFoldersAsync();
        }

        private void SetBusy(bool busy, string status)
        {
            _loading = busy;
            ProjectCombo.IsEnabled = !busy;
            DisciplineCombo.IsEnabled = !busy;
            FolderCombo.IsEnabled = !busy;
            ContinueButton.IsEnabled = !busy;
            if (status != null) StatusText.Text = status;
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedFolderId = (FolderCombo.SelectedItem as BimFolder)?.Id;
            if (SelectedFolderId == null)
            {
                StatusText.Text = "Pick the folder this model lives in.";
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
