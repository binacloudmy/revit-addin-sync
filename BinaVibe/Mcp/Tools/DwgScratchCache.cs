// DwgScratchCache — makes a standalone DWG readable through the same Revit
// code path as CAD that is already in the model.
//
// A DWG the drafter attaches in the pane has no ImportInstance anywhere, so we
// make one: link the file into a scratch document and keep that document open,
// keyed by an "att:<guid>" ref, so the summary call and every later drill-down
// call reuse one link instead of re-linking per tool call.
//
// Fallback: a blank or missing default project template yields a scratch
// document with no ViewPlan to link into. Rather than fail the turn, we link
// into the ACTIVE document inside a TransactionGroup and roll it back once the
// read is done — the user's model is untouched (RollBack adds no undo entry)
// and the cost is a re-link per call. `Use` hides which mode is in play, so
// ToolRegistry and DwgReader never branch on it.
//
// Nothing here mutates the user's document in a way that survives the call, and
// no DWG bytes leave the machine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    public static class DwgScratchCache
    {
        private sealed class Entry
        {
            public string Path = "";
            public string Name = "";
            public Document? Doc;          // null => fallback mode (re-link per call)
            public ElementId? ImportId;
            public DateTime LastUsed;
        }

        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

        private static readonly HashSet<string> SupportedExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".dwg", ".dxf" };

        /// <summary>Open an attached drawing and return its dwg_ref. Throws with
        /// a drafter-readable reason (missing file, wrong type, link refused) —
        /// the pane turns that into a one-line "could not be read" note and
        /// still sends the turn.</summary>
        public static string OpenAttachment(UIApplication app, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("no file path given");
            if (!File.Exists(path))
                throw new InvalidOperationException("file not found: " + path);
            if (!SupportedExtensions.Contains(System.IO.Path.GetExtension(path)))
                throw new InvalidOperationException("not a DWG/DXF file: " + System.IO.Path.GetFileName(path));

            Sweep();

            // Same file attached twice in a session: reuse the open link.
            foreach (var kv in _entries)
                if (string.Equals(kv.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    kv.Value.LastUsed = DateTime.UtcNow;
                    return kv.Key;
                }

            var entry = new Entry
            {
                Path = path,
                Name = System.IO.Path.GetFileName(path),
                LastUsed = DateTime.UtcNow,
            };

            var scratch = TryOpenScratch(app, path, out var importId);
            if (scratch != null)
            {
                entry.Doc = scratch;
                entry.ImportId = importId;
            }
            // else: fallback mode — Use() links into the active document and
            // rolls back around each read. Verify it works ONCE here so an
            // unreadable file fails at attach time, not mid-answer.
            else
            {
                WithActiveDocLink(app, path, (_, imp) => imp.Id);
            }

            var dwgRef = "att:" + Guid.NewGuid().ToString("N");
            _entries[dwgRef] = entry;
            return dwgRef;
        }

        /// <summary>Run <paramref name="body"/> against the ImportInstance behind
        /// an "att:" ref, whichever mode the entry is in.</summary>
        public static T Use<T>(UIApplication app, string dwgRef, Func<Document, ImportInstance, T> body)
        {
            if (!_entries.TryGetValue(dwgRef, out var entry))
                throw new InvalidOperationException(
                    "unknown dwg_ref " + dwgRef + " — the attachment is no longer open; ask the user to re-attach the drawing");
            entry.LastUsed = DateTime.UtcNow;

            if (entry.Doc != null && entry.Doc.IsValidObject && entry.ImportId != null)
            {
                if (entry.Doc.GetElement(entry.ImportId) is ImportInstance imp)
                    return body(entry.Doc, imp);
            }

            // Scratch document gone (closed by Revit / never existed) — fall back.
            entry.Doc = null;
            entry.ImportId = null;
            return WithActiveDocLink(app, entry.Path, body);
        }

        public static bool IsAttachmentRef(string dwgRef) =>
            !string.IsNullOrEmpty(dwgRef) && dwgRef.StartsWith("att:", StringComparison.Ordinal);

        /// <summary>Close every scratch document. Called when the pane's session
        /// ends and when the host document closes — a scratch document left open
        /// would show up in Revit's window list and hold a file lock.</summary>
        public static void CloseAll()
        {
            foreach (var entry in _entries.Values) Close(entry);
            _entries.Clear();
        }

        // ─── internals ──────────────────────────────────────────────────

        private static void Sweep()
        {
            var stale = _entries.Where(kv => DateTime.UtcNow - kv.Value.LastUsed > Ttl)
                                .Select(kv => kv.Key).ToList();
            foreach (var key in stale)
            {
                Close(_entries[key]);
                _entries.Remove(key);
            }
        }

        private static void Close(Entry entry)
        {
            try { if (entry.Doc != null && entry.Doc.IsValidObject) entry.Doc.Close(false); }
            catch { /* already closed by Revit */ }
            entry.Doc = null;
            entry.ImportId = null;
        }

        private static Document? TryOpenScratch(UIApplication app, string path, out ElementId? importId)
        {
            importId = null;
            Document? doc = null;
            try
            {
                var a = app.Application;
                var template = a.DefaultProjectTemplate;
                doc = !string.IsNullOrEmpty(template) && File.Exists(template)
                    ? a.NewProjectDocument(template)
                    : a.NewProjectDocument(UnitSystem.Metric);

                var view = FirstPlanView(doc);
                if (view == null) { Discard(doc); return null; }

                var id = LinkInto(doc, view, path);
                if (id == null) { Discard(doc); return null; }

                importId = id;
                return doc;
            }
            catch
            {
                // No template, no write access to the template, NewProjectDocument
                // unavailable in this host — all mean "use the fallback".
                if (doc != null) Discard(doc);
                return null;
            }
        }

        private static void Discard(Document doc)
        {
            try { doc.Close(false); } catch { /* never opened properly */ }
        }

        /// <summary>Fallback read: link into the active document, run the body,
        /// roll the link back out. The user's model is unchanged.</summary>
        private static T WithActiveDocLink<T>(UIApplication app, string path, Func<Document, ImportInstance, T> body)
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("no active document — open a Revit project first");
            var view = app.ActiveUIDocument?.ActiveView as ViewPlan ?? FirstPlanView(doc);
            if (view == null)
                throw new InvalidOperationException(
                    "cannot read this DWG: no plan view to link it into (open a floor plan and try again)");

            using var group = new TransactionGroup(doc, "BINA: read DWG");
            group.Start();
            try
            {
                var id = LinkInto(doc, view, path)
                    ?? throw new InvalidOperationException(
                        "Revit could not link this DWG (corrupt, password-protected, or an unsupported version)");
                if (doc.GetElement(id) is not ImportInstance imp)
                    throw new InvalidOperationException("Revit linked the DWG but produced no readable import");
                return body(doc, imp);
            }
            finally
            {
                // RollBack, never Commit: the link is a read fixture, not an edit.
                try { group.RollBack(); } catch { /* already closed by a failed txn */ }
            }
        }

        private static ElementId? LinkInto(Document doc, View view, string path)
        {
            var options = new DWGImportOptions
            {
                ThisViewOnly = false,
                Placement = ImportPlacement.Origin,
                ColorMode = ImportColorMode.Preserved,
            };
            using var tx = new Transaction(doc, "BINA: link DWG for reading");
            tx.Start();
            if (!doc.Link(path, options, view, out var id) || id == null || id == ElementId.InvalidElementId)
            {
                tx.RollBack();
                return null;
            }
            tx.Commit();
            return id;
        }

        private static ViewPlan? FirstPlanView(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate);
    }
}
