// BinaVibe.Indexer — subscribes to Revit DocumentChanged and ships deltas
// to the v2 backend so Channel 3 (indexed model snapshot, PRD §7.3) stays
// fresh.
//
// Step-1 scope: subscribe, batch deltas, POST `/vibe/snapshot/...`. No
// initial bulk index yet — that arrives in Step 3 alongside event
// debouncing and conflict resolution for collaborative sessions.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace BinaVibe.Indexer
{
    public sealed class DocumentChangedIndexer : IDisposable
    {
        private readonly ControlledApplication _app;
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _tenantId;
        private readonly string _projectId;
        private readonly ConcurrentQueue<SnapshotDoc> _pendingUpserts = new();
        private readonly ConcurrentQueue<string> _pendingDeletes = new();

        public DocumentChangedIndexer(
            ControlledApplication app, HttpClient http,
            string baseUrl, string tenantId, string projectId)
        {
            _app = app;
            _http = http;
            _baseUrl = baseUrl.TrimEnd('/');
            _tenantId = tenantId;
            _projectId = projectId;
            _app.DocumentChanged += OnDocumentChanged;
        }

        private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            var doc = e.GetDocument();
            foreach (var id in e.GetAddedElementIds()) Enqueue(doc, id);
            foreach (var id in e.GetModifiedElementIds()) Enqueue(doc, id);
            foreach (var id in e.GetDeletedElementIds())
                _pendingDeletes.Enqueue(id.Value.ToString());
        }

        private void Enqueue(Document doc, ElementId id)
        {
            var el = doc.GetElement(id);
            if (el is null) return;
            _pendingUpserts.Enqueue(new SnapshotDoc(
                Id: id.Value.ToString(),
                Text: $"{el.Category?.Name} {el.Name}",
                Metadata: new Dictionary<string, object>
                {
                    ["category"] = el.Category?.Name ?? "",
                    ["name"] = el.Name ?? "",
                    ["level_id"] = el.LevelId?.Value.ToString() ?? "",
                }));
        }

        /// <summary>
        /// Flush the pending queue to the backend. Caller decides cadence
        /// (post-tool-call hook in the Bridge, plus a debounced timer).
        /// </summary>
        public async Task<int> FlushAsync()
        {
            var docs = new List<SnapshotDoc>();
            while (_pendingUpserts.TryDequeue(out var d)) docs.Add(d);
            var deleted = new List<string>();
            while (_pendingDeletes.TryDequeue(out var id)) deleted.Add(id);
            if (docs.Count == 0 && deleted.Count == 0) return 0;

            var body = new { mode = "delta", docs, deleted_ids = deleted };
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_baseUrl}/vibe/snapshot/{_tenantId}/{_projectId}");
            req.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? docs.Count + deleted.Count : 0;
        }

        public void Dispose()
        {
            _app.DocumentChanged -= OnDocumentChanged;
        }
    }

    public sealed record SnapshotDoc(
        string Id,
        string Text,
        Dictionary<string, object> Metadata);
}
