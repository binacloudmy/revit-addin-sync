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
        private readonly bool _showModel;

        /// <param name="showModel">
        /// True when the list spans the whole project, where a row is ambiguous
        /// without naming the model it belongs to. Scoped to one model, the
        /// header already says which, and repeating it on every card is noise.
        /// </param>
        public IssueCardModel(BinaIssue issue, bool showModel = false)
        {
            Source = issue;
            _showModel = showModel;
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

        /// <summary>The dot the web puts inside the status pill.</summary>
        public Brush StatusDot => Brush(Source.Status switch
        {
            "Open" => "#DC2626",
            "InProgress" => "#F59E0B",
            "Resolved" => "#16A34A",
            "Closed" => "#6B7280",
            _ => "#6B7280"
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

        public bool IsCoordination =>
            string.Equals(Source.Source, "coordination", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Coordination issues are flagged because they behave differently: they
        /// span several models, so some of their elements will belong to a model
        /// the user does not have open.
        /// </summary>
        public string SourceLabel => IsCoordination ? "Coordination" : "Design";

        public Visibility CoordinationVisibility =>
            IsCoordination ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>"jkrAR24_5a_… · v6" — which model this issue belongs to.</summary>
        public string ModelName
        {
            get
            {
                // A coordination issue names its federated set, and there are
                // often six of them — a count reads better than a wall of names.
                if (IsCoordination)
                {
                    int count = Source.Models?.Count ?? 0;
                    return count > 1 ? $"{count} models" : Source.DesignName ?? "";
                }

                string name = Source.DesignName;
                if (string.IsNullOrEmpty(name)) return "";
                return Source.VersionNumber.HasValue ? $"{name} · v{Source.VersionNumber}" : name;
            }
        }

        public Visibility ModelVisibility =>
            (_showModel || IsCoordination) && !string.IsNullOrEmpty(ModelName)
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>The markup snapshot, fetched lazily by the panel.</summary>
        public BitmapImage Thumbnail { get; set; }

        public Visibility ThumbnailVisibility =>
            Thumbnail == null ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Keeps the row aligned when an issue has no markup image.</summary>
        public Visibility PlaceholderVisibility =>
            Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        private static Brush Brush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
