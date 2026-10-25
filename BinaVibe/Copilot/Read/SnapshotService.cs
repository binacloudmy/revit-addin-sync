#nullable enable
// Keeps the context snapshot (spec §7.9, plan §2.4) current from Revit events
// (SelectionChanged, ViewActivated, DocumentChanged, DocumentOpened/Created/
// Closed, saves) and fingerprints the active document so a call can fail
// doc_changed when the drafter switched documents mid-turn.
//
// Events only mark what is dirty (selection and view are cheap and refreshed in
// the handler); the next Idling rebuilds the rest, so a DocumentChanged storm
// costs one rebuild. A refresh that throws keeps the last good snapshot and
// sets stale:true. Current() is thread-safe and returns a deep copy.
//
// Attach() also wires RevitThread (the copilot read pump). Only W5 calls it,
// from App.OnStartup (ruling R4).

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public static class SnapshotService
    {
        private static readonly object Gate = new();
        private static SnapshotModel _current = SnapshotBuilder.Stale(null);   // stale until the first refresh
        private static readonly Dictionary<string, string> Nonces = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ChangeSeqs = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> SavingAs = new(StringComparer.Ordinal);
        private static bool _attached;
        private static string _revitVersion = "";

        // Dirty flags, set by events and cleared by Refresh.
        private static volatile bool _docDirty = true;
        private static volatile bool _viewDirty = true;
        private static volatile bool _pruneNonces;

        // Doc-level parts, rebuilt only when the document changed.
        private static string? _cachedKey;
        private static List<SnapshotLink> _links = new();
        private static int _warnings;
        private static List<SnapshotRoom>? _rooms;
        private static DateTime _lastDocRefreshUtc = DateTime.MinValue;
        private static readonly TimeSpan DocRefreshEvery = TimeSpan.FromSeconds(1);

        public static void Attach(UIControlledApplication app)
        {
            lock (Gate)
            {
                if (_attached) return;
                _attached = true;
            }
            RevitThread.Attach(app);
            var ca = app.ControlledApplication;
            try { _revitVersion = ca.VersionNumber ?? ""; } catch { _revitVersion = ""; }
            ca.DocumentOpened += OnDocumentOpened;
            ca.DocumentCreated += OnDocumentCreated;
            ca.DocumentClosed += OnDocumentClosed;
            ca.DocumentChanged += OnDocumentChanged;
            ca.DocumentSavingAs += OnDocumentSavingAs;
            ca.DocumentSavedAs += OnDocumentSavedAs;
            ca.DocumentSaved += OnDocumentSaved;
            ca.DocumentSynchronizedWithCentral += OnDocumentSynced;
            app.ViewActivated += OnViewActivated;
            app.SelectionChanged += OnSelectionChanged;
            app.Idling += OnIdling;
        }

        /// <summary>A deep copy of the latest snapshot. stale is true until the first
        /// refresh after Attach, and whenever the last refresh failed.</summary>
        public static SnapshotModel Current()
        {
            lock (Gate) return SnapshotBuilder.Clone(_current);
        }

        /// <summary>Rebuilds the snapshot now (Revit thread) and returns a copy. For
        /// callers that are about to send it and cannot wait for the next Idling.</summary>
        public static SnapshotModel RefreshNow(UIApplication app)
        {
            Refresh(app, force: true);
            return Current();
        }

        /// <summary>The identity of an opened document: changes when it is closed and
        /// reopened, survives Save As. Compare with DocIdentity.FromSnapshot(turn snapshot).</summary>
        public static string DocFingerprint(Document doc) => DocIdentity.Fingerprint(SessionNonceOf(doc));

        /// <summary>The random per-open nonce of a document (created on first use).</summary>
        public static string SessionNonceOf(Document doc)
        {
            var key = DocKey(doc);
            lock (Gate)
            {
                if (!Nonces.TryGetValue(key, out var n))
                {
                    n = SnapshotBuilder.NewSessionNonce();
                    Nonces[key] = n;
                }
                return n;
            }
        }

        /// <summary>DocumentChanged events seen for this document since it was opened.</summary>
        public static long ChangeSeqOf(Document doc)
        {
            lock (Gate) return ChangeSeqs.TryGetValue(DocKey(doc), out var s) ? s : 0;
        }

        private static string DocKey(Document doc)
        {
            string path = "";
            try { path = doc.PathName ?? ""; } catch { }
            if (path.Length > 0) return "path:" + path;
            string title = "";
            try { title = doc.Title ?? ""; } catch { }
            return "unsaved:" + title;
        }

        // ── Events (never throw into Revit) ────────────────────────────────

        private static void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null || doc.IsLinked) return;
                NewNonce(doc);
                _docDirty = true;
            }
            catch { }
        }

        private static void OnDocumentCreated(object? sender, DocumentCreatedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null) return;
                NewNonce(doc);
                _docDirty = true;
            }
            catch { }
        }

        private static void NewNonce(Document doc)
        {
            var key = DocKey(doc);
            lock (Gate)
            {
                Nonces[key] = SnapshotBuilder.NewSessionNonce();
                ChangeSeqs[key] = 0;
            }
        }

        // DocumentClosed only carries an id: prune nonces of documents no longer
        // open on the next Idling instead.
        private static void OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
        {
            _pruneNonces = true;
            _docDirty = true;
            _viewDirty = true;
        }

        private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;
                var key = DocKey(doc);
                lock (Gate) ChangeSeqs[key] = (ChangeSeqs.TryGetValue(key, out var s) ? s : 0) + 1;
                _docDirty = true;
            }
            catch { }
        }

        private static void OnDocumentSavingAs(object? sender, DocumentSavingAsEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null) return;
                lock (Gate) SavingAs[e.PathName ?? ""] = DocKey(doc);
            }
            catch { }
        }

        // Save As changes the path, so the key moves; the nonce (the identity) stays.
        private static void OnDocumentSavedAs(object? sender, DocumentSavedAsEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null) return;
                var newKey = DocKey(doc);
                lock (Gate)
                {
                    string? oldKey = null;
                    foreach (var kv in SavingAs)
                        if (string.Equals("path:" + kv.Key, newKey, StringComparison.OrdinalIgnoreCase)) { oldKey = kv.Value; break; }
                    SavingAs.Clear();
                    if (oldKey != null && oldKey != newKey && Nonces.TryGetValue(oldKey, out var n))
                    {
                        Nonces[newKey] = n;
                        Nonces.Remove(oldKey);
                        if (ChangeSeqs.TryGetValue(oldKey, out var s)) { ChangeSeqs[newKey] = s; ChangeSeqs.Remove(oldKey); }
                    }
                }
                _docDirty = true;
            }
            catch { }
        }

        private static void OnDocumentSaved(object? sender, DocumentSavedEventArgs e) => _docDirty = true;

        private static void OnDocumentSynced(object? sender, DocumentSynchronizedWithCentralEventArgs e) => _docDirty = true;

        // A switch to another document shows up in Refresh as a new doc key.
        private static void OnViewActivated(object? sender, ViewActivatedEventArgs e) => _viewDirty = true;

        // Cheap enough to apply in the handler, so a "these" right after a click is current.
        private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;
                var selection = BuildSelection(doc, e.GetSelectedElements());
                lock (Gate)
                {
                    if (_current.Doc != null && _cachedKey == DocKey(doc))
                    {
                        _current.Selection = selection;
                        return;
                    }
                }
                _viewDirty = true;
            }
            catch { _viewDirty = true; }
        }

        private static void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (sender is not UIApplication app) return;
            try { RevitThread.OnIdling(app, e); } catch { }
            try
            {
                if (_pruneNonces) PruneNonces(app);
                if (_docDirty || _viewDirty) Refresh(app, force: false);
            }
            catch { }
            try { OverviewService.OnIdling(app); } catch { }
        }

        private static void PruneNonces(UIApplication app)
        {
            _pruneNonces = false;
            var open = new HashSet<string>(StringComparer.Ordinal);
            foreach (Document d in app.Application.Documents)
                if (!d.IsLinked) open.Add(DocKey(d));
            lock (Gate)
            {
                foreach (var key in Nonces.Keys.Where(k => !open.Contains(k)).ToList())
                {
                    Nonces.Remove(key);
                    ChangeSeqs.Remove(key);
                }
            }
        }

        // ── Refresh ────────────────────────────────────────────────────────

        private static void Refresh(UIApplication app, bool force)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
            {
                lock (Gate)
                {
                    _current = SnapshotBuilder.Empty();
                    _cachedKey = null;
                }
                _docDirty = _viewDirty = false;
                return;
            }

            var key = DocKey(doc);
            bool docPartsDue = force || key != _cachedKey
                || (_docDirty && DateTime.UtcNow - _lastDocRefreshUtc >= DocRefreshEvery);
            if (!docPartsDue && !_viewDirty) return;   // doc-level refresh throttled; try next Idling

            try
            {
                if (docPartsDue)
                {
                    _links = BuildLinks(doc);
                    _warnings = SafeCount(() => doc.GetWarnings().Count);
                    _rooms = BuildRooms(doc);
                    _cachedKey = key;
                    _lastDocRefreshUtc = DateTime.UtcNow;
                    _docDirty = false;
                }

                var view = doc.ActiveView;
                var snap = new SnapshotModel
                {
                    Doc = BuildDoc(doc, view),
                    View = view == null ? null : BuildView(view),
                    Selection = BuildSelection(doc, uidoc!.Selection.GetElementIds()),
                    Worksharing = new SnapshotWorksharing { ActiveWorkset = ActiveWorkset(doc) },
                    Links = _links.Select(l => new SnapshotLink { Name = l.Name, Loaded = l.Loaded }).ToList(),
                    WarningsCount = _warnings,
                    Rooms = _rooms?.ToList(),
                    ChangeSeq = ChangeSeqOf(doc),
                    Stale = false,
                };
                lock (Gate) _current = snap;
                _viewDirty = false;
            }
            catch
            {
                lock (Gate) _current = SnapshotBuilder.Stale(_current);
            }
        }

        private static int SafeCount(Func<int> f)
        {
            try { return f(); } catch { return 0; }
        }

        private static SnapshotDoc BuildDoc(Document doc, View? view)
        {
            bool workshared = doc.IsWorkshared;
            bool central = false;
            if (workshared)
            {
                try
                {
                    var cp = doc.GetWorksharingCentralModelPath();
                    var centralPath = cp == null ? "" : ModelPathUtils.ConvertModelPathToUserVisiblePath(cp);
                    central = centralPath.Length > 0
                        && string.Equals(centralPath, doc.PathName, StringComparison.OrdinalIgnoreCase);
                }
                catch { central = false; }
            }

            string length = "";
            try { length = DisplayUnits.SymbolFor(doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId); }
            catch { }

            string versionGuid = "";
            try { versionGuid = Document.GetDocumentVersion(doc)?.VersionGUID.ToString() ?? ""; } catch { }

            return new SnapshotDoc
            {
                Kind = doc.IsFamilyDocument ? SnapshotDocKinds.Family : SnapshotDocKinds.Project,
                Title = doc.Title ?? "",
                PathHash = SnapshotBuilder.PathHash(doc.PathName),
                IsWorkshared = workshared,
                Mode = SnapshotBuilder.Mode(workshared, central, doc.IsDetached, doc.IsReadOnly),
                Units = new SnapshotUnits { Length = length },
                Phase = ViewPhaseName(doc, view),
                ActiveDesignOption = ActiveDesignOptionName(doc),
                VersionGuid = versionGuid,
                SessionNonce = SessionNonceOf(doc),
                RevitVersion = _revitVersion,
            };
        }

        internal static string? ViewPhaseName(Document doc, View? view)
        {
            try
            {
                var id = view?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return null;
                return doc.GetElement(id)?.Name;
            }
            catch { return null; }
        }

        private static string? ActiveDesignOptionName(Document doc)
        {
            try
            {
                var id = DesignOption.GetActiveDesignOptionId(doc);
                if (id == null || id == ElementId.InvalidElementId) return null;
                return doc.GetElement(id)?.Name;
            }
            catch { return null; }
        }

        private static SnapshotView BuildView(View view)
        {
            int? scale = null;
            try { if (view.Scale > 0) scale = view.Scale; } catch { }
            string? level = null;
            try { level = view.GenLevel?.Name; } catch { }
            return new SnapshotView
            {
                Id = view.Id.Value,
                Name = view.Name ?? "",
                Type = view.ViewType.ToString(),
                Level = level,
                Scale = scale,
                IsTemplate = view.IsTemplate,
            };
        }

        private static SnapshotSelection BuildSelection(Document doc, ICollection<ElementId> ids)
        {
            var picked = new List<(long Id, string? Category)>(ids.Count);
            foreach (var id in ids)
            {
                string? cat = null;
                try { cat = doc.GetElement(id)?.Category?.Name; } catch { }
                picked.Add((id.Value, cat));
            }
            return SnapshotBuilder.Selection(picked);
        }

        private static string? ActiveWorkset(Document doc)
        {
            if (!doc.IsWorkshared) return null;
            try
            {
                var table = doc.GetWorksetTable();
                return table.GetWorkset(table.GetActiveWorksetId())?.Name;
            }
            catch { return null; }
        }

        private static List<SnapshotLink> BuildLinks(Document doc)
        {
            var byName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var name = LinkNames.Display(LinkTypeName(doc, inst));
                    bool loaded = false;
                    try { loaded = inst.GetLinkDocument() != null; } catch { }
                    byName[name] = (byName.TryGetValue(name, out var was) && was) || loaded;
                }
            }
            catch { }
            return byName.Select(kv => new SnapshotLink { Name = kv.Key, Loaded = kv.Value })
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string LinkTypeName(Document doc, RevitLinkInstance inst)
        {
            try
            {
                var t = doc.GetElement(inst.GetTypeId());
                if (t != null && !string.IsNullOrEmpty(t.Name)) return t.Name;
            }
            catch { }
            return inst.Name ?? "";
        }

        private static List<SnapshotRoom>? BuildRooms(Document doc)
        {
            try
            {
                var col = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType();
                if (col.GetElementCount() > SnapshotLimits.MaxRooms) return null;
                var rooms = new List<SnapshotRoom>();
                foreach (var el in col)
                {
                    if (el is not SpatialElement room) continue;
                    rooms.Add(new SnapshotRoom
                    {
                        Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "",
                        Number = room.Number ?? "",
                        Level = room.Level?.Name ?? "",
                    });
                }
                return SnapshotBuilder.Rooms(rooms);
            }
            catch { return null; }
        }
    }
}
