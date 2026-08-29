// BinaVibe.DocState — Revit adapter over RevisionLedger (spec §8.4).
//
// Subscribes once to Application.DocumentChanged (read-only handler — no
// transactions here) and keeps one RevisionLedger per open document, keyed by
// fingerprint. The job drainer calls:
//   * StaleError(doc, expected, fingerprint) BEFORE invoking a mutation;
//   * Stamp(doc, result) after every tool so results carry
//     document_fingerprint + document_revision;
//   * ChangesSince(doc, args) for the changes_since tool.
// All three are no-ops / passthroughs unless VibeFlags.RevisionTracking is on.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace BinaVibe.DocState
{
    public static class DocumentRevisionTracker
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, RevisionLedger> _ledgers = new(StringComparer.Ordinal);
        private static bool _subscribed;

        public static void EnsureSubscribed(UIApplication app)
        {
            lock (_lock)
            {
                if (_subscribed || app?.Application == null) return;
                app.Application.DocumentChanged += OnDocumentChanged;
                _subscribed = true;
            }
        }

        public static string Fingerprint(Autodesk.Revit.DB.Document doc)
        {
            string? uid = null;
            try { uid = doc.ProjectInformation?.UniqueId; } catch { /* family docs etc. */ }
            string? path = null;
            bool workshared = false;
            try
            {
                workshared = doc.IsWorkshared;
                if (workshared)
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    path = central != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(central) : doc.PathName;
                }
                else
                {
                    path = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
                }
            }
            catch { path = doc.PathName; }
            return RevisionLedger.ComputeFingerprint(uid, path, workshared);
        }

        public static RevisionLedger LedgerFor(Autodesk.Revit.DB.Document doc)
        {
            var fp = Fingerprint(doc);
            lock (_lock)
            {
                if (!_ledgers.TryGetValue(fp, out var l))
                {
                    l = new RevisionLedger(fp);
                    _ledgers[fp] = l;
                }
                return l;
            }
        }

        private static long IdValue(ElementId id)
        {
#if REVIT2023_24
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        // READ-ONLY handler — pure recorder, must never throw into a commit.
        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null) return;
                var added = e.GetAddedElementIds().Select(IdValue).ToArray();
                var modified = e.GetModifiedElementIds().Select(IdValue).ToArray();
                var deleted = e.GetDeletedElementIds().Count;
                LedgerFor(doc).Record(added, modified, deleted, DateTime.UtcNow);
            }
            catch { /* recorder must never break a commit */ }
        }

        /// <summary>Typed stale_document refusal for a mutation frame, or null
        /// when the frame may proceed. Runs BEFORE any transaction is opened.</summary>
        public static Dictionary<string, object?>? StaleError(Autodesk.Revit.DB.Document doc, long expectedRevision, string? expectedFingerprint)
            => LedgerFor(doc).StaleError(expectedRevision, expectedFingerprint);

        /// <summary>Add document_fingerprint + document_revision to a tool result.</summary>
        public static Dictionary<string, object?> Stamp(Autodesk.Revit.DB.Document doc, Dictionary<string, object?> result)
        {
            try
            {
                var l = LedgerFor(doc);
                result["document_fingerprint"] = l.Fingerprint;
                result["document_revision"] = l.Revision;
            }
            catch { /* stamping is best-effort */ }
            return result;
        }

        /// <summary>changes_since tool: {from_revision}.</summary>
        public static Dictionary<string, object?> ChangesSince(Autodesk.Revit.DB.Document doc, JsonElement args)
        {
            long from = 0;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("from_revision", out var f))
            {
                if (f.ValueKind == JsonValueKind.Number) from = f.GetInt64();
                else if (f.ValueKind == JsonValueKind.String && long.TryParse(f.GetString(), out var parsed)) from = parsed;
            }
            var l = LedgerFor(doc);
            return l.ChangesSince(from, DateTime.UtcNow).ToDictionary(l.Fingerprint);
        }
    }
}
