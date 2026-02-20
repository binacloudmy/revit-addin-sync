using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI;

namespace RevitWebAppSync.Events
{
    /// <summary>
    /// Listens to Revit DocumentChanged events and triggers cost recalculation
    /// with debouncing to avoid lag during heavy editing.
    /// </summary>
    public class CostUpdateHandler
    {
        private readonly UIControlledApplication _app;
        private Timer _debounceTimer;
        private readonly object _lock = new object();
        private bool _isSubscribed;

        // Tracks pending changes for the notification banner
        private readonly List<ChangeRecord> _pendingChanges = new List<ChangeRecord>();

        // Debounce interval in milliseconds
        private const int DebounceMs = 2000;

        public CostUpdateHandler(UIControlledApplication app)
        {
            _app = app;
        }

        /// <summary>
        /// Start listening for document changes
        /// </summary>
        public void Subscribe()
        {
            if (_isSubscribed) return;

            _app.ControlledApplication.DocumentChanged += OnDocumentChanged;
            _isSubscribed = true;

            System.Diagnostics.Debug.WriteLine("[BINA Cost] Live update handler subscribed.");
        }

        /// <summary>
        /// Stop listening for document changes
        /// </summary>
        public void Unsubscribe()
        {
            if (!_isSubscribed) return;

            _app.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            _isSubscribed = false;

            lock (_lock)
            {
                _debounceTimer?.Stop();
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }

            System.Diagnostics.Debug.WriteLine("[BINA Cost] Live update handler unsubscribed.");
        }

        public bool IsSubscribed => _isSubscribed;

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                // Collect what changed
                var added = e.GetAddedElementIds();
                var deleted = e.GetDeletedElementIds();
                var modified = e.GetModifiedElementIds();

                int totalChanges = added.Count + deleted.Count + modified.Count;
                if (totalChanges == 0) return;

                // Quick-check: does this affect priceable elements?
                // We record the change and let the dashboard decide on refresh
                var doc = e.GetDocument();

                var record = new ChangeRecord
                {
                    Timestamp = DateTime.Now,
                    AddedCount = added.Count,
                    DeletedCount = deleted.Count,
                    ModifiedCount = modified.Count,
                    TransactionName = e.GetTransactionNames().FirstOrDefault() ?? "Edit",
                    AffectedCategories = GetAffectedCategories(doc, added, modified)
                };

                lock (_lock)
                {
                    _pendingChanges.Add(record);

                    // Reset debounce timer
                    _debounceTimer?.Stop();
                    _debounceTimer?.Dispose();

                    _debounceTimer = new Timer(DebounceMs);
                    _debounceTimer.AutoReset = false;
                    _debounceTimer.Elapsed += OnDebounceElapsed;
                    _debounceTimer.Start();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] DocumentChanged error: {ex.Message}");
            }
        }

        private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            List<ChangeRecord> changes;

            lock (_lock)
            {
                changes = new List<ChangeRecord>(_pendingChanges);
                _pendingChanges.Clear();

                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }

            if (changes.Count == 0) return;

            // Summarize the batch
            var summary = new ChangeSummary
            {
                TotalAdded = changes.Sum(c => c.AddedCount),
                TotalDeleted = changes.Sum(c => c.DeletedCount),
                TotalModified = changes.Sum(c => c.ModifiedCount),
                AffectedCategories = changes
                    .SelectMany(c => c.AffectedCategories)
                    .Distinct()
                    .ToList(),
                TransactionNames = changes
                    .Select(c => c.TransactionName)
                    .Distinct()
                    .ToList(),
                Timestamp = DateTime.Now
            };

            System.Diagnostics.Debug.WriteLine(
                $"[BINA Cost] Debounce fired: +{summary.TotalAdded} -{summary.TotalDeleted} ~{summary.TotalModified} | {string.Join(", ", summary.AffectedCategories)}");

            // Notify the dashboard panel on the UI thread
            try
            {
                var host = App.CostDashboardHost;
                if (host?.DashboardPanel == null) return;

                host.Dispatcher.Invoke(() =>
                {
                    host.DashboardPanel.OnModelChanged(summary);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] UI update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Get category names of affected elements (for notification display)
        /// </summary>
        private HashSet<string> GetAffectedCategories(Document doc, ICollection<ElementId> added, ICollection<ElementId> modified)
        {
            var categories = new HashSet<string>();

            foreach (var id in added.Concat(modified))
            {
                try
                {
                    Element elem = doc.GetElement(id);
                    if (elem?.Category != null)
                        categories.Add(elem.Category.Name);
                }
                catch { /* Element may be invalid */ }

                // Cap at 5 to keep it lightweight
                if (categories.Count >= 5) break;
            }

            return categories;
        }
    }

    /// <summary>
    /// A single batch of changes from one DocumentChanged event
    /// </summary>
    public class ChangeRecord
    {
        public DateTime Timestamp { get; set; }
        public int AddedCount { get; set; }
        public int DeletedCount { get; set; }
        public int ModifiedCount { get; set; }
        public string TransactionName { get; set; }
        public HashSet<string> AffectedCategories { get; set; } = new HashSet<string>();
    }

    /// <summary>
    /// Aggregated summary of changes after debounce
    /// </summary>
    public class ChangeSummary
    {
        public int TotalAdded { get; set; }
        public int TotalDeleted { get; set; }
        public int TotalModified { get; set; }
        public List<string> AffectedCategories { get; set; } = new List<string>();
        public List<string> TransactionNames { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; }

        public int TotalChanges => TotalAdded + TotalDeleted + TotalModified;

        /// <summary>
        /// Human-readable summary for the notification banner
        /// </summary>
        public string ToNotificationText()
        {
            var parts = new List<string>();

            if (TotalAdded > 0)
                parts.Add($"+{TotalAdded} added");
            if (TotalDeleted > 0)
                parts.Add($"-{TotalDeleted} removed");
            if (TotalModified > 0)
                parts.Add($"~{TotalModified} modified");

            string changeText = string.Join(", ", parts);

            if (AffectedCategories.Count > 0)
            {
                string cats = string.Join(", ", AffectedCategories.Take(3));
                if (AffectedCategories.Count > 3)
                    cats += $" +{AffectedCategories.Count - 3} more";
                return $"{changeText} ({cats})";
            }

            return changeText;
        }
    }
}
