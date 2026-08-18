using System.Collections.Generic;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Lists the issues pulled from BINA and lets one be picked (ClickUp 86d3y5jtz).
    ///
    /// Deliberately plain: a list and a button. This is the first slice — the
    /// dockable panel with filters, thumbnails and replies comes next, and it
    /// should be built on plumbing that has already proven itself in Revit.
    /// </summary>
    public partial class IssuePickerWindow : Window
    {
        public BinaIssue SelectedIssue { get; private set; }

        public IssuePickerWindow(IReadOnlyList<BinaIssue> issues, string modelName, int? versionNumber)
        {
            InitializeComponent();

            SubtitleText.Text = modelName == null
                ? $"{issues.Count} issue(s) in this project."
                : $"{issues.Count} issue(s) for \"{modelName}\"" +
                  (versionNumber.HasValue ? $" (v{versionNumber})." : ".") +
                  " Issues raised on earlier versions are included.";

            IssueList.ItemsSource = issues;
            if (issues.Count > 0) IssueList.SelectedIndex = 0;

            StatusText.Text = "Pick an issue and choose Show in model — the elements it points at " +
                              "will be selected and the viewpoint restored.";
        }

        private void ShowButton_Click(object sender, RoutedEventArgs e) => Accept();

        private void IssueList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Accept();

        private void Accept()
        {
            SelectedIssue = IssueList.SelectedItem as BinaIssue;
            if (SelectedIssue == null)
            {
                StatusText.Text = "Pick an issue first.";
                return;
            }

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
