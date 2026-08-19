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
        private BinaIssueDetail _open;
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
            SourceFilter.ItemsSource = new[] { "All issues", "Design", "Coordination" };
            SourceFilter.SelectedIndex = 0;
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

            // Without a design there is no model to filter by, and offering the
            // choice anyway was dishonest: "This model" quietly returned the
            // whole project, so the two options looked broken rather than
            // unavailable. Say why instead.
            bool identified = designId.HasValue;
            ScopeFilter.IsEnabled = identified;
            if (!identified)
            {
                ScopeFilter.SelectedIndex = 1;   // whole project
                ScopeFilter.ToolTip =
                    "This model is not in BINA yet, so its issues cannot be told apart from the rest " +
                    "of the project. Sync it to BINA once and this filter becomes available.";
                SubtitleText.Text = $"{modelLabel} — not in BINA, showing the whole project";
            }
            else
            {
                ScopeFilter.ToolTip =
                    "This model reads its whole version chain, plus coordination issues raised with it loaded.";
            }
        }

        public async Task SyncAsync()
        {
            if (_loading || _projectId <= 0) return;

            bool modelOnly = ScopeFilter.SelectedIndex == 0 && _designId.HasValue;
            int? designId = modelOnly ? _designId : null;
            string source = SourceFilter.SelectedIndex == 1 ? "design"
                : SourceFilter.SelectedIndex == 2 ? "coordination"
                : null;

            try
            {
                SetBusy(true, "Loading issues from BINA…");

                var config = BinaConfig.Load();
                string token = await BinaCloudSession.EnsureValidTokenAsync(config);
                if (string.IsNullOrEmpty(token))
                {
                    if (ShowCached(designId, source, "your Cloud Docs session has expired")) return;

                    SetBusy(false, "Your Cloud Docs session has expired — click 'Login to CDE' and sync again.");
                    return;
                }

                using (var api = new SyncApiClient(config.ResolvedApiBaseUrl, token, http: null,
                           refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    // "This model" reads the whole version chain for a design
                    // issue, and matches a coordination issue when this model
                    // was one of those loaded when it was raised.
                    var page = await api.GetIssuesAsync(_projectId, designId, source: source);

                    Render(page.Issues, showModel: !modelOnly);

                    SetBusy(false, page.HasMore
                        ? $"Showing the first {page.Count}. Narrow by status to see the rest."
                        : "Read-only — edit issues in BINA Cloud.");

                    // Kept for the next time there is no signal (decision 6).
                    IssueCache.SavePage(_projectId, designId, source, page);

                    // Thumbnails after the list is on screen: the text is what
                    // the user reads first, and a slow image should not hold it
                    // back.
                    await LoadThumbnailsAsync();
                }
            }
            catch (Exception ex)
            {
                // A site with no signal is the case this cache exists for, so
                // the last list is shown rather than an error page — labelled
                // with when it was taken, never passed off as current.
                if (ShowCached(designId, source, ex.Message)) return;

                SetBusy(false, $"Could not load issues: {ex.Message}");
            }
        }

        /// <returns>True when a cached list was shown.</returns>
        private bool ShowCached(int? designId, string source, string reason)
        {
            var cached = IssueCache.LoadPage(_projectId, designId, source);
            if (cached == null) return false;

            Render(cached.Value.Page.Issues, showModel: !designId.HasValue, fromCache: true);

            string when = cached.Value.SavedAt.ToString("d MMM HH:mm");
            SetBusy(false, $"Offline — showing the list from {when}. ({reason})");
            return true;
        }

        private void Render(List<BinaIssue> issues, bool showModel, bool fromCache = false)
        {
            _all.Clear();
            // Rows name their model only when the list spans the project;
            // scoped to one model the header already says which.
            _all.AddRange((issues ?? new List<BinaIssue>()).Select(issue => new IssueCardModel(issue, showModel)));

            if (fromCache)
            {
                // The images were kept as bytes, so they survive the presigned
                // URLs in the cached JSON having expired.
                foreach (var card in _all)
                {
                    byte[] bytes = IssueCache.LoadThumbnail(card.Guid);
                    if (bytes != null) card.Thumbnail = ToImage(bytes, 160);
                }
            }

            ApplyFilter();
        }

        private static BitmapImage ToImage(byte[] bytes, int decodeWidth)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] could not decode an image: {ex.Message}");
                return null;
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

                    card.Thumbnail = ToImage(bytes, 160);
                    IssueCache.SaveThumbnail(card.Guid, bytes);
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
            if (ReferenceEquals(sender, ScopeFilter) || ReferenceEquals(sender, SourceFilter)) await SyncAsync();
            else ApplyFilter();
        }

        /// <summary>
        /// Clicking a card fetches the issue in full and hands it to Revit.
        /// </summary>
        /// <summary>
        /// Opening a card fetches the issue in full and shows it in the pane —
        /// description, markup image and replies — rather than throwing the user
        /// straight into the model. Showing it in Revit is then their choice.
        /// </summary>
        private async void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var card = (sender as FrameworkElement)?.Tag as IssueCardModel;
            if (card == null) return;

            try
            {
                ShowDetail(card, null);
                DetailStatusLine.Text = "Loading…";

                var config = BinaConfig.Load();
                string token = await BinaCloudSession.EnsureValidTokenAsync(config);
                if (string.IsNullOrEmpty(token))
                {
                    _open = IssueCache.LoadDetail(card.Guid);
                    if (_open != null)
                    {
                        ShowDetail(card, _open);
                        DetailStatusLine.Text = "Signed out — showing the copy saved the last time this issue was opened.";
                        return;
                    }

                    DetailStatusLine.Text = "Your Cloud Docs session has expired — click 'Login to CDE'.";
                    return;
                }

                using (var api = new SyncApiClient(config.ResolvedApiBaseUrl, token, http: null,
                           refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    _open = await api.GetIssueAsync(card.Guid);
                }

                IssueCache.SaveDetail(_open);
                ShowDetail(card, _open);
                await LoadDetailImageAsync(_open);
            }
            catch (Exception ex)
            {
                // An issue opened once can be opened again with no connection —
                // including Show in model, since the elements and the viewpoint
                // are in the cached copy (decision 6).
                _open = IssueCache.LoadDetail(card.Guid);
                if (_open != null)
                {
                    ShowDetail(card, _open);
                    DetailStatusLine.Text = "Offline — showing the copy saved the last time this issue was opened.";
                    return;
                }

                DetailStatusLine.Text = $"Could not open the issue: {ex.Message}";
            }
        }

        private void ShowDetail(IssueCardModel card, BinaIssueDetail detail)
        {
            ListView.Visibility = Visibility.Collapsed;
            DetailView.Visibility = Visibility.Visible;

            DetailTitle.Text = card.Title;
            DetailStatusPill.Background = card.StatusBackground;
            DetailStatusText.Text = card.StatusLabel;
            DetailStatusText.Foreground = card.StatusForeground;
            DetailPriorityPill.Background = card.PriorityBackground;
            DetailPriorityPill.Visibility = card.PriorityVisibility;
            DetailPriorityText.Text = card.Priority;
            DetailPriorityText.Foreground = card.PriorityForeground;
            DetailByline.Text = card.Byline;

            DetailImage.Source = card.Thumbnail;                 // the list's copy, until the full one loads
            DetailImageBox.Visibility = card.Thumbnail == null ? Visibility.Collapsed : Visibility.Visible;

            DetailText.Text = detail?.Text ?? card.Preview;

            int elements = detail?.CapturedComponents?.Sum(c => (c.Selection?.Count ?? 0) + (c.Isolated?.Count ?? 0)) ?? 0;

            if (detail == null)
            {
                DetailElements.Text = "";
            }
            else if (elements == 0)
            {
                DetailElements.Text = "This issue records a viewpoint but no elements — Show in model will restore the view only.";
            }
            else if (card.IsCoordination)
            {
                // Spanning several models, some of those elements are in files
                // that are not open. Saying so beforehand beats a summary that
                // reads like a failure.
                var others = (detail.Models ?? new List<BinaIssueModel>())
                    .Select(m => m.FileName).Where(n => !string.IsNullOrEmpty(n)).ToList();
                DetailElements.Text =
                    $"Points at {elements} element{(elements == 1 ? "" : "s")} across {others.Count} model" +
                    $"{(others.Count == 1 ? "" : "s")}: {string.Join(", ", others)}. " +
                    "Only elements in the model you have open can be selected.";
            }
            else
            {
                DetailElements.Text = $"Points at {elements} element{(elements == 1 ? "" : "s")} in the model.";
            }

            var replies = (detail?.Replies ?? new List<BinaIssueReply>())
                .Select(r => new { Who = r.Author?.Name ?? "Someone", r.Text })
                .ToList();
            RepliesItems.ItemsSource = replies;
            RepliesHeader.Visibility = replies.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (detail != null) DetailStatusLine.Text = "";
        }

        /// <summary>The full-size markup image, once the detail is open.</summary>
        private async Task LoadDetailImageAsync(BinaIssueDetail detail)
        {
            if (string.IsNullOrEmpty(detail?.SnapshotUrl)) return;

            try
            {
                byte[] bytes = await Http.GetByteArrayAsync(detail.SnapshotUrl);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();

                DetailImage.Source = image;
                DetailImageBox.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] detail image failed: {ex.Message}");
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            DetailView.Visibility = Visibility.Collapsed;
            ListView.Visibility = Visibility.Visible;
            _open = null;
        }

        /// <summary>Hands the open issue to Revit, on Revit's own thread.</summary>
        private void ShowInModel_Click(object sender, RoutedEventArgs e)
        {
            if (_open == null) return;

            DetailStatusLine.Text = "Showing in the model…";
            App.IssueShowHandler?.Queue(_open);
            App.IssueShowEvent?.Raise();
        }

        /// <summary>Called back from the handler once Revit has done the work.</summary>
        public void ReportShown(BinaIssueDetail issue, IssueViewpointApplier.Result result, string error)
        {
            Dispatcher.Invoke(() =>
            {
                if (error != null)
                {
                    DetailStatusLine.Text = $"Could not show the issue: {error}";
                    return;
                }

                var parts = new List<string>();
                parts.Add(result.Found > 0
                    ? $"{result.Found} element{(result.Found == 1 ? "" : "s")} selected"
                    : "no elements from this issue are in the open model");

                if (result.NotFound > 0) parts.Add($"{result.NotFound} not found in this version");
                if (result.SwitchedView) parts.Add($"switched to \"{result.ViewName}\"");
                parts.Add(result.CameraApplied ? "viewpoint restored" : $"viewpoint skipped ({result.CameraNote})");

                DetailStatusLine.Text = char.ToUpper(parts[0][0]) + parts[0].Substring(1) + " — " +
                                        string.Join(", ", parts.Skip(1)) + ".";
            });
        }

        private void SetBusy(bool busy, string status)
        {
            _loading = busy;
            SyncButton.IsEnabled = !busy;
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (status != null) StatusText.Text = status;
        }
    }
}
