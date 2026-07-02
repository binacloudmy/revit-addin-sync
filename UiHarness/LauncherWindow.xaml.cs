using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync;
using RevitWebAppSync.Models;

namespace UiHarness
{
    /// <summary>
    /// Standalone WPF host for the addin's windows so UI work doesn't need a
    /// Revit boot cycle. Every window opens with mock data; Revit-dependent
    /// actions (running commands, model context) are expected to no-op or fail.
    /// </summary>
    public partial class LauncherWindow : Window
    {
        public LauncherWindow() => InitializeComponent();

        private static void Open(Func<Window> create)
        {
            try
            {
                create().Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Harness: window failed to open",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenLogin(object sender, RoutedEventArgs e) =>
            Open(() => new LoginWindow("friend@bina.cloud"));

        // Fake token: the picker opens, fires its load, and shows its error state.
        private void OpenProjectPicker(object sender, RoutedEventArgs e) =>
            Open(() => new ProjectPickerWindow("harness-fake-token"));

        private void OpenSyncResults(object sender, RoutedEventArgs e) =>
            Open(() => new SyncResultsWindow(new SyncResultData
            {
                FileName = "Hospital_Structure.rvt",
                DisciplineType = "Structure",
                FileSize = 45_000_000,
                Version = "12",
                BinaObsSuccess = true,
                BinaLocation = "projects/demo/structure",
                AutodeskOssSuccess = true,
                AutodeskUrn = "urn:adsk.objects:os.object:demo/hospital_structure.rvt",
                RegistrationSuccess = true,
                LinkedFiles = new List<LinkedFileInfo>(),
            }));

        private void OpenDownloadResults(object sender, RoutedEventArgs e) =>
            Open(() => new DownloadResultsWindow(new DownloadResultData
            {
                ProjectName = "Demo Hospital",
                DownloadLocation = @"C:\Temp\BinaDownloads",
                DownloadedFiles = new List<DownloadedFileInfo>
                {
                    new DownloadedFileInfo { DisciplineName = "Architecture", FileName = "arch.rvt", FilePath = @"C:\Temp\arch.rvt", Success = true },
                    new DownloadedFileInfo { DisciplineName = "Structure", FileName = "str.rvt", FilePath = @"C:\Temp\str.rvt", Success = true },
                    new DownloadedFileInfo { DisciplineName = "MEP", FileName = "mep.rvt", FilePath = @"C:\Temp\mep.rvt", Success = false },
                },
            }));

        private void OpenUserInfo(object sender, RoutedEventArgs e) =>
            Open(() => new UserInfoWindow(new BinaConfig
            {
                UserName = "Harness User",
                Email = "friend@bina.cloud",
                ProjectName = "Demo Hospital",
                ProjectId = 42,
                OrgId = 7,
            }));

        private void OpenUpdate(object sender, RoutedEventArgs e) =>
            Open(() => new RevitWebAppSync.UI.UpdateWindow());

        private void OpenCommandRun(object sender, RoutedEventArgs e) =>
            Open(() => new RevitWebAppSync.UI.CommandRunWindow(new CommandTemplate
            {
                Id = "harness-1",
                Name = "Tag all doors on level",
                Description = "Places door tags on every door of the chosen level.",
                Category = "Annotation",
                Scope = "public",
                PromptTemplate = "Tag all doors on {level} using {tag_type}",
                Variables = new List<CommandVariable>
                {
                    new CommandVariable { Name = "level", Label = "Level", Type = "select", Options = new List<string> { "L1", "L2", "Roof" } },
                    new CommandVariable { Name = "tag_type", Label = "Tag type", Type = "text", Default = "Door Tag" },
                },
            }));

        private void OpenCopilot(object sender, RoutedEventArgs e) =>
            Open(() => new Window
            {
                Title = "Copilot Panel",
                Width = 420,
                Height = 800,
                Content = new Frame { Content = new RevitWebAppSync.UI.Copilot.CopilotPanel() },
            });
    }
}
