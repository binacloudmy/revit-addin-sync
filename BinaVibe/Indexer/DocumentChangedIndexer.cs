// BinaVibe.Indexer — subscribes to Revit DocumentChanged and ships deltas
// to the v2 backend so Channel 3 (indexed model snapshot, PRD §7.3) stays
// fresh.
//
// Step-3 additions:
//   - BulkIndexAsync: one-time walk of definitional layers + hot-category
//     instances on connect, so the mirror has a full baseline.
//   - BuildBulkPayload: pure static helper (unit-testable, no Revit refs).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace BinaVibe.Indexer
{
    /// <summary>
    /// Wire format sent to POST /vibe/snapshot/{tenant}/{project}.
    /// Matches the backend SnapshotPayload schema:
    ///   { mode, docs, deleted_ids, version }
    /// </summary>
    public sealed class SnapshotPayload
    {
        public string Mode { get; set; } = "";
        public List<SnapshotDoc> Docs { get; set; } = new();
        public List<string> DeletedIds { get; set; } = new();
        public int Version { get; set; }
    }

    public sealed class DocumentChangedIndexer : IDisposable
    {
        private readonly ControlledApplication _app;
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _tenantId;
        private readonly string _projectId;
        private readonly ConcurrentQueue<SnapshotDoc> _pendingUpserts = new();
        private readonly ConcurrentQueue<string> _pendingDeletes = new();

        // HOT categories walked during bulk index — instances only.
        private static readonly BuiltInCategory[] HotCategories = new[]
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_Grids,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_GenericModel,
        };

        // Per-category cap for bulk walk — prevents runaway payloads on
        // huge models. A warning is logged when truncation occurs.
        private const int BulkCategoryCapPerCategory = 5000;

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
                    ["element_id"] = id.Value.ToString(),
                    ["name"] = el.Name ?? "",
                    ["level_id"] = el.LevelId?.Value.ToString() ?? "",
                    ["params"] = new Dictionary<string, object>
                    {
                        ["name"] = el.Name ?? "",
                    },
                }));
        }

        /// <summary>
        /// Flush the pending delta queue to the backend. Caller decides cadence
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

        // ─── Bulk index ──────────────────────────────────────────────────────

        /// <summary>
        /// Pure static helper — builds a bulk SnapshotPayload from a list of
        /// already-constructed SnapshotDocs. No Revit references; unit-testable.
        /// </summary>
        public static SnapshotPayload BuildBulkPayload(List<SnapshotDoc> docs, int version)
        {
            return new SnapshotPayload
            {
                Mode = "bulk",
                Docs = docs ?? new List<SnapshotDoc>(),
                DeletedIds = new List<string>(),
                Version = version,
            };
        }

        /// <summary>
        /// Walk definitional layers (Levels, WallTypes, ViewTemplates, Worksets,
        /// ProjectInfo, FamilyTypes) and hot-category instances, build SnapshotDocs,
        /// and POST a single bulk payload. Call from a background Task on connect
        /// so the UI thread is not blocked.
        /// </summary>
        public async Task BulkIndexAsync(Document doc, int version)
        {
            var docs = new List<SnapshotDoc>();

            // ── Levels ───────────────────────────────────────────────────────
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();
            foreach (var lv in levels)
            {
                docs.Add(new SnapshotDoc(
                    Id: lv.Id.Value.ToString(),
                    Text: $"Level {lv.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "Level",
                        ["element_id"] = lv.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["name"] = lv.Name ?? "",
                            ["elevation"] = lv.Elevation,
                        },
                    }));
            }

            // ── WallTypes ────────────────────────────────────────────────────
            var wallTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .ToList();
            foreach (var wt in wallTypes)
            {
                docs.Add(new SnapshotDoc(
                    Id: wt.Id.Value.ToString(),
                    Text: $"WallType {wt.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "WallType",
                        ["element_id"] = wt.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["name"] = wt.Name ?? "",
                        },
                    }));
            }

            // ── ViewTemplates ────────────────────────────────────────────────
            var viewTemplates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate)
                .ToList();
            foreach (var vt in viewTemplates)
            {
                docs.Add(new SnapshotDoc(
                    Id: vt.Id.Value.ToString(),
                    Text: $"ViewTemplate {vt.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "ViewTemplate",
                        ["element_id"] = vt.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["name"] = vt.Name ?? "",
                        },
                    }));
            }

            // ── Worksets (worksharing models only) ───────────────────────────
            if (doc.IsWorkshared)
            {
                var worksets = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToList();
                foreach (var ws in worksets)
                {
                    docs.Add(new SnapshotDoc(
                        Id: ws.Id.IntegerValue.ToString(),
                        Text: $"Workset {ws.Name}",
                        Metadata: new Dictionary<string, object>
                        {
                            ["category"] = "Workset",
                            ["element_id"] = ws.Id.IntegerValue.ToString(),
                            ["params"] = new Dictionary<string, object>
                            {
                                ["name"] = ws.Name ?? "",
                            },
                        }));
                }
            }

            // ── ProjectInfo ──────────────────────────────────────────────────
            var projInfo = doc.ProjectInformation;
            if (projInfo != null)
            {
                docs.Add(new SnapshotDoc(
                    Id: projInfo.Id.Value.ToString(),
                    Text: $"ProjectInfo {projInfo.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "ProjectInfo",
                        ["element_id"] = projInfo.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["name"] = projInfo.Name ?? "",
                        },
                    }));
            }

            // ── FamilyTypes ──────────────────────────────────────────────────
            var familySymbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .ToList();
            foreach (var fs in familySymbols)
            {
                docs.Add(new SnapshotDoc(
                    Id: fs.Id.Value.ToString(),
                    Text: $"FamilyType {fs.Family?.Name} {fs.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "FamilyType",
                        ["element_id"] = fs.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["name"] = fs.Name ?? "",
                            ["family"] = fs.Family?.Name ?? "",
                        },
                    }));
            }

            // ── Views (non-templates) ────────────────────────────────────────
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();
            foreach (var v in views)
            {
                docs.Add(new SnapshotDoc(
                    Id: v.Id.Value.ToString(),
                    Text: $"View {v.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "View",
                        ["element_id"] = v.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["name"] = v.Name ?? "",
                            ["view_type"] = v.ViewType.ToString(),
                            ["id"] = v.Id.Value.ToString(),
                        },
                    }));
            }

            // ── Sheets ───────────────────────────────────────────────────────
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .ToList();
            foreach (var s in sheets)
            {
                docs.Add(new SnapshotDoc(
                    Id: s.Id.Value.ToString(),
                    Text: $"Sheet {s.SheetNumber} {s.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "Sheet",
                        ["element_id"] = s.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["number"] = s.SheetNumber ?? "",
                            ["name"] = s.Name ?? "",
                            ["id"] = s.Id.Value.ToString(),
                        },
                    }));
            }

            // ── Schedules (non-templates) ────────────────────────────────────
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTemplate)
                .ToList();
            foreach (var vs in schedules)
            {
                docs.Add(new SnapshotDoc(
                    Id: vs.Id.Value.ToString(),
                    Text: $"Schedule {vs.Name}",
                    Metadata: new Dictionary<string, object>
                    {
                        ["category"] = "Schedule",
                        ["element_id"] = vs.Id.Value.ToString(),
                        ["params"] = new Dictionary<string, object>
                        {
                            ["name"] = vs.Name ?? "",
                            ["id"] = vs.Id.Value.ToString(),
                        },
                    }));
            }

            // ── Hot-category instances ────────────────────────────────────────
            foreach (var bic in HotCategories)
            {
                List<Element> instances;
                try
                {
                    instances = new FilteredElementCollector(doc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                        .ToList();
                }
                catch
                {
                    continue;
                }

                if (instances.Count > BulkCategoryCapPerCategory)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BinaVibe] BulkIndex: {bic} has {instances.Count} elements — " +
                        $"truncating to {BulkCategoryCapPerCategory}");
                    instances = instances.Take(BulkCategoryCapPerCategory).ToList();
                }

                foreach (var el in instances)
                {
                    var catName = el.Category?.Name ?? bic.ToString();
                    docs.Add(new SnapshotDoc(
                        Id: el.Id.Value.ToString(),
                        Text: $"{catName} {el.Name}",
                        Metadata: new Dictionary<string, object>
                        {
                            ["category"] = catName,
                            ["element_id"] = el.Id.Value.ToString(),
                            ["params"] = new Dictionary<string, object>
                            {
                                ["name"] = el.Name ?? "",
                            },
                        }));
                }
            }

            var payload = BuildBulkPayload(docs, version);

            // Serialise with camelCase property names so field names match
            // the backend SnapshotPayload schema (mode, docs, deleted_ids, version).
            var serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_baseUrl}/vibe/snapshot/{_tenantId}/{_projectId}");
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload, serializerOptions),
                Encoding.UTF8,
                "application/json");

            System.Diagnostics.Debug.WriteLine(
                $"[BinaVibe] BulkIndex: posting {docs.Count} docs to " +
                $"/vibe/snapshot/{_tenantId}/{_projectId}");

            try
            {
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe] BulkIndex: response {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe] BulkIndex: POST failed — {ex.Message}");
            }
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
