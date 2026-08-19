using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Issues
{
    /// <summary>
    /// One issue as the pane draws it (ClickUp 86d3y5jtz).
    ///
    /// The colours are lifted from BINA Cloud's own issue list
    /// (BimCommentDetailDrawer's STATUS_CONFIG / PRIORITY_CONFIG) so a person
    /// reading issues in Revit sees the same badge they saw in the browser —
    /// a red Open, an amber In Progress, a green Resolved.
    /// </summary>
    public class IssueCardModel
    {
        public BinaIssue Source { get; }

        public IssueCardModel(BinaIssue issue)
        {
            Source = issue;
        }

        public string Guid => Source.Guid;

        public string Title =>
            string.IsNullOrWhiteSpace(Source.Title) ? $"({Source.TopicType})" : Source.Title;

        public string Preview => (Source.Text ?? "").Trim();

        public string StatusLabel => Source.Status == "InProgress" ? "In Progress" : Source.Status;

        public Brush StatusBackground => Brush(Source.Status switch
        {
            "Open" => "#FEF2F2",
            "InProgress" => "#FEF3C7",
            "Resolved" => "#DCFCE7",
            "Closed" => "#F3F4F6",
            _ => "#F3F4F6"
        });

        public Brush StatusForeground => Brush(Source.Status switch
        {
            "Open" => "#991B1B",
            "InProgress" => "#92400E",
            "Resolved" => "#166534",
            "Closed" => "#374151",
            _ => "#374151"
        });

        public string Priority => Source.Priority;

        public Visibility PriorityVisibility =>
            string.IsNullOrEmpty(Source.Priority) ? Visibility.Collapsed : Visibility.Visible;

        public Brush PriorityBackground => Brush(Source.Priority switch
        {
            "Low" => "#F0FDF4",
            "Medium" => "#FEF9C3",
            "High" => "#FED7AA",
            "Critical" => "#FEE2E2",
            _ => "#F3F4F6"
        });

        public Brush PriorityForeground => Brush(Source.Priority switch
        {
            "Low" => "#166534",
            "Medium" => "#854D0E",
            "High" => "#9A3412",
            "Critical" => "#991B1B",
            _ => "#374151"
        });

        /// <summary>"Ammar · 3 Aug 2026", the web list's footer line.</summary>
        public string Byline
        {
            get
            {
                string who = Source.Author?.Name;
                string when = Source.UpdatedAt?.ToLocalTime().ToString("d MMM yyyy");
                if (string.IsNullOrEmpty(who)) return when ?? "";
                return when == null ? who : $"{who} · {when}";
            }
        }

        /// <summary>The markup snapshot, fetched lazily by the panel.</summary>
        public BitmapImage Thumbnail { get; set; }

        public Visibility ThumbnailVisibility =>
            Thumbnail == null ? Visibility.Collapsed : Visibility.Visible;

        private static Brush Brush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
