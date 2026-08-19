using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Issues
{
    /// <summary>
    /// The Issues pane (ClickUp 86d3y5jtz): issues raised in BINA, listed the way
    /// BINA lists them, and openable against the model.
    ///
    /// Read-only in this release — the web is the source of truth. Nothing here
    /// touches the Revit API directly: the pane has no API context, so showing an
    /// issue is queued onto App.IssueShowEvent and run by Revit when it is safe.
    /// </summary>
    public partial class IssuesPanel : UserControl
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly List<IssueCardModel> _all = new List<IssueCardModel>();
        private int _projectId;
        private int? _designId;
        private string _modelLabel;
        private bool _loading;

        public IssuesPanel()
        {
            InitializeComponent();

            StatusFilter.ItemsSource = new[] { "All statuses", "Open", "In Progress", "Resolved", "Closed" };
            StatusFilter.SelectedIndex = 0;
            ScopeFilter.ItemsSource = new[] { "This model", "Whole project" };
            ScopeFilter.SelectedIndex = 0;
        }

        /// <summary>
        /// Told by the command which model is open, so Sync knows what to ask for.
        /// </summary>
        public void SetContext(int projectId, int? designId, string modelLabel)
        {
            _projectId = projectId;
            _designId = designId;
            _modelLabel = modelLabel;
            SubtitleText.Text = modelLabel ?? $"Project #{projectId}";
        }

        public async Task SyncAsync()
        {
            if (_loading || _projectId <= 0) return;

            try
            {
                SetBusy(true, "Loading issues from BINA…");

                var config = BinaConfig.Load();
                string token = await BinaCloudSession.EnsureValidTokenAsync(config);
                if (string.IsNullOrEmpty(token))
                {
                    SetBusy(false, "Your Cloud Docs session has expired — click 'Login to CDE' and sync again.");
                    return;
                }

                using (var api = new SyncApiClient(config.ResolvedApiBaseUrl, token, http: null,
                           refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    // "This model" reads the whole version chain, so an issue
                    // raised on v3 still belongs to the model at v7.
                    bool modelOnly = ScopeFilter.SelectedIndex == 0 && _designId.HasValue;
                    var page = await api.GetIssuesAsync(_projectId, modelOnly ? _designId : null);

                    _all.Clear();
                    _all.AddRange(page.Issues.Select(issue => new IssueCardModel(issue)));
                    ApplyFilter();

                    SetBusy(false, page.HasMore
                        ? $"Showing the first {page.Count}. Narrow by status to see the rest."
                        : "Read-only — edit issues in BINA Cloud.");

                    // Thumbnails after the list is on screen: the text is what the
                    // user reads first, and a slow image should not hold it back.
                    await LoadThumbnailsAsync();
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Could not load issues: {ex.Message}");
            }
        }

        private async Task LoadThumbnailsAsync()
        {
            foreach (var card in _all.ToList())
            {
                string url = card.Source.SnapshotUrl;
                if (string.IsNullOrEmpty(url) || card.Thumbnail != null) continue;

                try
                {
                    // The bytes, not the URL: presigned links expire within the
                    // hour, so a cached URL would render a broken image later.
                    byte[] bytes = await Http.GetByteArrayAsync(url);

                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 160;   // twice the drawn width, for crispness
                    image.StreamSource = new MemoryStream(bytes);
                    image.EndInit();
                    image.Freeze();

                    card.Thumbnail = image;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA Issues] thumbnail failed: {ex.Message}");
                }
            }

            ApplyFilter();   // rebind so the images appear
        }

        private void ApplyFilter()
        {
            string status = StatusFilter.SelectedItem as string;
            IEnumerable<IssueCardModel> shown = _all;

            if (!string.IsNullOrEmpty(status) && status != "All statuses")
            {
                string wanted = status == "In Progress" ? "InProgress" : status;
                shown = shown.Where(card => card.Source.Status == wanted);
            }

            var list = shown.ToList();
            IssueItems.ItemsSource = null;
            IssueItems.ItemsSource = list;

            CountText.Text = list.Count == _all.Count
                ? $"{list.Count} issue{(list.Count == 1 ? "" : "s")}"
                : $"{list.Count} of {_all.Count}";

            EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0 && _all.Count > 0) EmptyText.Text = "No issues match this filter.";
            else if (list.Count == 0) EmptyText.Text = "No issues for this model in BINA.";
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e) => await SyncAsync();

        private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;

            // Scope is a different question to the server; status is local.
            if (ReferenceEquals(sender, ScopeFilter)) await SyncAsync();
            else ApplyFilter();
        }

        /// <summary>
        /// Clicking a card fetches the issue in full and hands it to Revit.
        /// </summary>
        private async void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var card = (sender as FrameworkElement)?.Tag as IssueCardModel;
            if (card == null) return;

            try
            {
                StatusText.Text = $"Opening \"{card.Title}\"…";

                var config = BinaConfig.Load();
                string token = await BinaCloudSession.EnsureValidTokenAsync(config);
                if (string.IsNullOrEmpty(token))
                {
                    StatusText.Text = "Your Cloud Docs session has expired — click 'Login to CDE'.";
                    return;
                }

                BinaIssueDetail detail;
                using (var api = new SyncApiClient(config.ResolvedApiBaseUrl, token, http: null,
                           refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    detail = await api.GetIssueAsync(card.Guid);
                }

                // Revit work happens on Revit's thread, not this one.
                App.IssueShowHandler?.Queue(detail);
                App.IssueShowEvent?.Raise();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open the issue: {ex.Message}";
            }
        }

        /// <summary>Called back from the handler once Revit has done the work.</summary>
        public void ReportShown(BinaIssueDetail issue, IssueViewpointApplier.Result result, string error)
        {
            Dispatcher.Invoke(() =>
            {
                if (error != null)
                {
                    StatusText.Text = $"Could not show the issue: {error}";
                    return;
                }

                var parts = new List<string>();
                parts.Add(result.Found > 0
                    ? $"{result.Found} element{(result.Found == 1 ? "" : "s")} selected"
                    : "no elements from this issue are in the open model");

                if (result.NotFound > 0) parts.Add($"{result.NotFound} not found in this version");
                if (result.SwitchedView) parts.Add($"switched to \"{result.ViewName}\"");
                parts.Add(result.CameraApplied ? "viewpoint restored" : $"viewpoint skipped ({result.CameraNote})");

                StatusText.Text = char.ToUpper(parts[0][0]) + parts[0].Substring(1) + " — " +
                                  string.Join(", ", parts.Skip(1)) + ".";
            });
        }

        private void SetBusy(bool busy, string status)
        {
            _loading = busy;
            SyncButton.IsEnabled = !busy;
            SyncButton.Content = busy ? "…" : "Sync";
            if (status != null) StatusText.Text = status;
        }
    }
}
