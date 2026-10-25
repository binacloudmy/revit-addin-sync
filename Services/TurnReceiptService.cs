// TurnReceiptService — "tunjuk hasil": harness-assembled evidence after every
// mutate batch (spec: bina-ai docs/superpowers/specs/2026-08-18-turn-receipt-
// design.md). The model NEVER authors this receipt — it is computed from
// DocumentChanged transaction ground truth (the model-sight self-verification
// dead end, 2026-07-08, enforced structurally).
//
// Flow: ToolLoopRunner calls BeginBatch() before executing a mutate batch and
// enqueues a "__turn_receipt" job after it; that job (on the Revit UI thread)
// builds the receipt, flash-selects + zooms to the changed elements, drops
// checkmark badges, and returns the receipt dict — which the runner attaches
// to the pane outcome AND folds into the last mutate tool result so the
// model's narration can never contradict what the drafter sees.
//
// Mechanism ranking is research-pinned (The Building Coder 0429/1633/1849):
//   record  = DocumentChanged id sets (free, exact, read-only handler)
//   flash   = Selection.SetElementIds + UIView.ZoomAndCenterRectangle
//             (no transaction, no cleanup debt; ShowElements only as fallback
//             — it can pop a "No good view found" modal)
//   badges  = TemporaryGraphicsManager (2022+; not undoable, never saved —
//             the best cleanup story of every overlay mechanism)
//   tint    = none. Highlighting is selection + badges only: a tint needs
//             its own transaction, which would sit on top of the batch in
//             Revit's undo list, count as an external change for the write
//             engine's preview->commit check, and break the one-Undo-per-batch
//             chain (lean harness spec §7.6)
//   capture = ExportImage before/after, CONFIRM-GATED only (1-5s + hard GPU
//             dependency — never on the hot path)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using BinaVibe.Copilot.Undo;
using BinaVibe.Mcp.Tools;

namespace RevitWebAppSync.Services
{
    public static class TurnReceiptService
    {
        private static readonly object _lock = new object();
        private static bool _subscribed;
        private static bool _recording;
        private static readonly HashSet<ElementId> _added = new HashSet<ElementId>();
        private static readonly HashSet<ElementId> _modified = new HashSet<ElementId>();
        private static int _deleted;
        private static readonly List<string> _txNames = new List<string>();
        // Every recorded commit of the batch, in order (one Revit undo entry each),
        // for the [Undo] button; _txNames above is the distinct list for receipts.
        private static readonly List<string> _txSequence = new List<string>();
        private static List<string> _lastTxSequence = new List<string>();
        // Why the [Undo] button may no longer undo the batch's newest entry: something
        // changed Revit's undo stack during or after the batch (null = still safe).
        private static string _undoBlocked;
        // Which receipt the [Undo] state belongs to; a new batch retires the old one.
        private static string _lastReceiptId = "";

        /// <summary>The real outcome of a receipt [Undo], once Revit has reported it (the
        /// button's own reply only says it is pending). Raised off the UI thread.</summary>
        public static event Action<BinaVibe.Copilot.Contracts.Envelope> LegacyUndoFinished;

        // Last receipt state for the card buttons ([Tunjuk semula], [Undo]).
        private static List<ElementId> _lastShownIds = new List<ElementId>();
        private static readonly List<int> _badgeIds = new List<int>();
        private static string _pendingPreCapturePath;

        // Operation identity for the batch being recorded (spec §8.3/§8.5).
        private static string _operationId = "";
        private static string _jobId = "";
        private static long _preRevision;
        private static string _documentFingerprint = "";

        /// <summary>Idempotent DocumentChanged subscription. Called at the top
        /// of every job drain so the handler exists BEFORE the first mutate
        /// transaction of the first batch commits.</summary>
        public static void EnsureSubscribed(UIApplication app)
        {
            lock (_lock)
            {
                if (_subscribed || app?.Application == null) return;
                app.Application.DocumentChanged += OnDocumentChanged;
                _subscribed = true;
            }
        }

        // Confirm-gated PRE-capture handshake: the pane requests it on a real
        // Ya click (never on Auto mode's programmatic accept); the runner
        // consumes it when the batch executes.
        private static bool _preCaptureRequested;
        public static void RequestPreCapture() { _preCaptureRequested = true; }
        public static bool ConsumePreCaptureRequest()
        {
            var v = _preCaptureRequested; _preCaptureRequested = false; return v;
        }

        /// <summary>Arm recording for a mutate batch. Runner thread; touches no
        /// Revit API.</summary>
        public static void BeginBatch() => BeginBatch("", "");

        /// <summary>Arm recording for ONE operation (the approved mutation pack
        /// of a resume leg). The receipt built by Epilogue belongs to exactly
        /// this operation / job.</summary>
        public static void BeginBatch(string operationId, string jobId)
        {
            lock (_lock)
            {
                _added.Clear(); _modified.Clear(); _deleted = 0; _txNames.Clear(); _txSequence.Clear();
                // A new batch retires the previous receipt's [Undo]: pressed now, it would undo
                // this batch's newest transaction instead of its own.
                _lastTxSequence = new List<string>();
                _lastReceiptId = "";
                _undoBlocked = null;
                _operationId = operationId ?? "";
                _jobId = jobId ?? "";
                _preRevision = 0;
                _documentFingerprint = "";
                _recording = true;
            }
        }

        /// <summary>Revit-thread half of BeginBatch: snapshot the document
        /// identity + revision BEFORE the first mutate transaction. Called by
        /// the drainer via the "__receipt_begin" job.</summary>
        public static Dictionary<string, object> BeginOnRevitThread(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc != null)
                {
                    var ledger = BinaVibe.DocState.DocumentRevisionTracker.LedgerFor(doc);
                    lock (_lock) { _documentFingerprint = ledger.Fingerprint; _preRevision = ledger.Revision; }
                }
            }
            catch { /* identity is best-effort on legacy paths */ }
            return new Dictionary<string, object> { ["ok"] = true };
        }

        // READ-ONLY handler (starting a transaction here throws) — pure recorder.
        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                var names = e.GetTransactionNames();
                var ours = names != null && names.Any(BinaTransactionNames.IsOurs);
                if (!_recording || !ours)
                {
                    // Anything else that moves Revit's undo stack (the drafter, another add-in,
                    // a later BINA commit) makes the receipt's newest-entry Undo unsafe.
                    if (ChangesUndoStack(e)) lock (_lock) _undoBlocked ??= Describe(e, names);
                    return;
                }
                // Only our own tool transactions — a drafter editing mid-batch
                // must not be claimed on our receipt.
                lock (_lock)
                {
                    foreach (var id in e.GetAddedElementIds()) _added.Add(id);
                    foreach (var id in e.GetModifiedElementIds()) { if (!_added.Contains(id)) _modified.Add(id); }
                    _deleted += e.GetDeletedElementIds().Count;
                    foreach (var n in names) if (!_txNames.Contains(n)) _txNames.Add(n);
                    if (e.Operation == UndoOperation.TransactionCommitted)
                        _txSequence.Add(names.First(BinaTransactionNames.IsOurs));
                }
            }
            catch { /* recorder must never break a commit */ }
        }

        private static bool ChangesUndoStack(DocumentChangedEventArgs e)
        {
            if (e.Operation != UndoOperation.TransactionCommitted
                && e.Operation != UndoOperation.TransactionUndone
                && e.Operation != UndoOperation.TransactionRedone)
                return false;
            // Our own preview / read / look callbacks are always rolled back.
            var scope = BinaVibe.Copilot.Guard.CallbackScope.Current(e.GetDocument());
            return scope == null || scope == BinaVibe.Copilot.Guard.CallbackKind.Commit;
        }

        private static string Describe(DocumentChangedEventArgs e, IList<string> names)
        {
            var what = names != null && names.Count > 0 ? "'" + names[0] + "'" : "satu perubahan";
            return e.Operation == UndoOperation.TransactionCommitted ? what + " berlaku selepas perubahan BINA"
                : e.Operation == UndoOperation.TransactionUndone ? "Undo Revit digunakan selepas perubahan BINA"
                : "Redo Revit digunakan selepas perubahan BINA";
        }

        /// <summary>Confirm-gated PRE capture (P2): screenshot the active view
        /// BEFORE an interactively-approved batch executes. Degrades silently
        /// on GPU-less rigs.</summary>
        public static Dictionary<string, object> PreCapture(UIApplication app)
        {
            _pendingPreCapturePath = null;
            var path = TryExportActiveView(app, "before");
            _pendingPreCapturePath = path;
            return new Dictionary<string, object> { ["ok"] = true, ["path"] = path ?? "" };
        }

        /// <summary>Build the receipt + run the visual epilogue. Runs as its own
        /// McpJob on the Revit UI thread AFTER the batch executed.</summary>
        public static Dictionary<string, object> Epilogue(UIApplication app)
        {
            List<ElementId> added, modified;
            int deleted;
            List<string> txNames;
            lock (_lock)
            {
                _recording = false;
                added = _added.ToList();
                modified = _modified.ToList();
                deleted = _deleted;
                txNames = _txNames.ToList();
            }

            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            string opId, jobId, fp; long pre;
            string receiptId = Guid.NewGuid().ToString("N");
            lock (_lock) { opId = _operationId; jobId = _jobId; fp = _documentFingerprint; pre = _preRevision; _lastTxSequence = _txSequence.ToList(); _lastReceiptId = receiptId; }
            long post = pre;
            try
            {
                if (doc != null)
                {
                    var ledger = BinaVibe.DocState.DocumentRevisionTracker.LedgerFor(doc);
                    if (string.IsNullOrEmpty(fp)) fp = ledger.Fingerprint;
                    post = ledger.Revision;
                }
            }
            catch { /* best-effort */ }
            var receipt = ReceiptShape.Build(
                opId, jobId, fp, pre, post,
                added.Select(IdValue).ToList(), modified.Select(IdValue).ToList(), deleted,
                txNames, status: "completed");
            if (doc == null) return receipt;

            // Headline elements = added + directly-relevant modified. The
            // modified set includes indirect dependents (joined walls, hosted
            // elements) — they stay in the count as "berkaitan" but the flash
            // covers everything so nothing changed is invisible.
            var show = new List<ElementId>();
            show.AddRange(added);
            show.AddRange(modified);
            show = show.Where(id => doc.GetElement(id) != null).ToList();

            // Category breakdown (counts only — receipt must stay tiny; the
            // compressor-bomb lesson applies to receipts too).
            var byCat = new Dictionary<string, int>();
            foreach (var id in show)
            {
                var cat = doc.GetElement(id)?.Category?.Name ?? "(lain)";
                byCat[cat] = byCat.TryGetValue(cat, out var c) ? c + 1 : 1;
            }
            receipt["by_category"] = byCat;
            receipt["receipt_id"] = receiptId;   // Undo(app, receiptId) refuses a stale receipt

            ClearVisuals(app, doc);          // previous turn's badges die first
            _lastShownIds = show;

            if (show.Count > 0)
            {
                FlashAndZoom(uidoc, doc, show);
                TryAddBadges(doc, show);
                receipt["highlighted"] = show.Count;
            }

            // P2 — POST capture only when a PRE exists (confirm-gated batches).
            if (!string.IsNullOrEmpty(_pendingPreCapturePath))
            {
                var after = TryExportActiveView(app, "after");
                if (after != null)
                {
                    receipt["before_image"] = _pendingPreCapturePath;
                    receipt["after_image"] = after;
                }
                _pendingPreCapturePath = null;
            }
            return receipt;
        }

        /// <summary>[Tunjuk semula] — re-run the WHOLE visual epilogue, not just
        /// selection + zoom. The original only re-selected; each new turn clears
        /// the badges/tint, and a bare selection is invisible on elements whose
        /// category doesn't render (Rooms without interior fill — UAT
        /// 2026-09-01: "diserlah dan dizum" while the canvas showed nothing).
        /// Badges ride TemporaryGraphicsManager, so they show regardless of
        /// category visibility. No tint: it would need a transaction (spec §7.6).</summary>
        public static Dictionary<string, object> ReShow(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            var ids = _lastShownIds.Where(id => doc?.GetElement(id) != null).ToList();
            if (doc == null || ids.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "tiada rekod perubahan untuk ditunjuk" };
            // What can this change set honestly show? A 3D-view receipt is a
            // view + cameras + sun path — no canvas geometry; "highlighting"
            // it is an invisible no-op (UAT 2026-09-01). Decide first.
            var rows = ids.Select(id =>
            {
                var e = doc.GetElement(id);
                bool isView = e is View;
                bool hasBox = !isView && e?.get_BoundingBox(null) != null;
                return (isView, hasBox);
            }).ToList();
            var decision = ReceiptShape.DecideShow(rows);
            switch (decision.Action)
            {
                case ReceiptShape.ShowAction.Zoom:
                    var showIds = decision.ShowIndexes.Select(i => ids[i]).ToList();
                    ClearVisuals(app, doc);          // drop the previous badges first
                    FlashAndZoom(uidoc, doc, showIds);
                    TryAddBadges(doc, showIds);
                    return new Dictionary<string, object> { ["ok"] = true, ["shown"] = showIds.Count };
                case ReceiptShape.ShowAction.ActivateView:
                    var view = doc.GetElement(ids[decision.ViewIndex]) as View;
                    try { uidoc.ActiveView = view; }
                    catch (Exception ex)
                    {
                        return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"pandangan '{view?.Name}' tidak dapat dibuka ({ex.Message})" };
                    }
                    return new Dictionary<string, object> { ["ok"] = true, ["view_activated"] = view?.Name,
                        ["note"] = $"Perubahan ialah pandangan baharu — '{view?.Name}' dibuka." };
                default:
                    return new Dictionary<string, object> { ["ok"] = false,
                        ["error"] = "perubahan ini tiada geometri untuk diserlah (pandangan/kamera/tetapan) — tiada apa untuk dizum" };
            }
        }

        /// <summary>[Undo] — ONE Revit Undo of the newest entry, only when nothing has moved
        /// Revit's undo stack since the batch, and accepted only if what Revit undid is all
        /// BINA-owned (else it is redone at once; BinaVibe.Copilot.Undo.UndoService). The
        /// recorder sees inner transactions, not assimilated groups, so it never counts undo
        /// entries and never undoes more than one. The reply is "pending" (ok:false) until
        /// Revit reports the outcome, which LegacyUndoFinished then carries.</summary>
        public static Dictionary<string, object> Undo(UIApplication app) => Undo(app, null);

        /// <summary>[Undo] for one receipt: with a receipt id (from the receipt's "receipt_id"),
        /// a stale receipt — one a newer batch has replaced — is refused.</summary>
        public static Dictionary<string, object> Undo(UIApplication app, string receiptId)
        {
            var doc = app.ActiveUIDocument?.Document;
            int recorded; string blocked; string current;
            lock (_lock) { recorded = _lastTxSequence.Count; blocked = _undoBlocked; current = _lastReceiptId; }
            if (!string.IsNullOrEmpty(receiptId) && receiptId != current)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "resit ini sudah lama — perubahan baharu telah dibuat selepasnya; guna senarai Undo Revit" };
            if (doc == null || recorded == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "tiada perubahan BINA yang direkodkan untuk dibuat asal — guna senarai Undo Revit" };
            if (blocked != null)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = blocked + " — guna senarai Undo Revit (entri bermula 'BINA:' / 'BinaVibe')" };

            var task = BinaVibe.Copilot.Undo.UndoService.UndoNewestOursAsync(doc, System.Threading.CancellationToken.None);
            // A press that failed at once (not attached, another undo running) undid nothing:
            // keep the button usable so it can be pressed again.
            if (task.IsCompleted && (task.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || !task.Result.Ok))
                return task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                    ? Reply(task.Result)
                    : new Dictionary<string, object> { ["ok"] = false, ["error"] = "Undo tidak dapat dimulakan — cuba sekali lagi" };

            lock (_lock) { _lastTxSequence = new List<string>(); _undoBlocked = "Undo sudah dijalankan untuk perubahan ini"; }
            if (task.IsCompleted) return Reply(task.Result);
            task.ContinueWith(t =>
            {
                try { if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) LegacyUndoFinished?.Invoke(t.Result); }
                catch { /* a listener must never break Revit */ }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["pending"] = true,
                ["error"] = "belum disahkan — Undo sedang dijalankan dalam Revit; keputusannya dipaparkan sebaik sahaja Revit mengesahkannya",
            };
        }

        private static Dictionary<string, object> Reply(BinaVibe.Copilot.Contracts.Envelope env) => env.Ok
            ? new Dictionary<string, object> { ["ok"] = true, ["note"] = env.Summary }
            : new Dictionary<string, object> { ["ok"] = false, ["error"] = env.Error?.Message ?? env.Summary };

        private static long IdValue(ElementId id)
        {
#if REVIT2023_24
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        // ─── visuals ────────────────────────────────────────────────────────

        /// <summary>A highlight needs a canvas. Measured UAT 2026-08-18: the
        /// drafter clicked [Tunjuk semula] while READING the Kontraktor
        /// schedule — selection landed invisibly, zoom no-oped, "renders
        /// nothing". When the active view is non-graphical (schedule/sheet
        /// list/etc), switch to an already-open graphical view first; returns
        /// null when none is open (caller falls back to ShowElements, which
        /// hunts for a good view itself).</summary>
        internal static View EnsureGraphicalView(UIDocument uidoc, Document doc)
        {
            var view = doc.ActiveView;
            if (view is ViewPlan || view is View3D || view is ViewSection) return view;
            var target = uidoc.GetOpenUIViews()
                .Select(v => doc.GetElement(v.ViewId) as View)
                .FirstOrDefault(v => v != null && !v.IsTemplate
                    && (v is ViewPlan || v is View3D || v is ViewSection));
            if (target == null) return null;
            try { uidoc.ActiveView = target; return target; } catch { return null; }
        }

        private static void FlashAndZoom(UIDocument uidoc, Document doc, List<ElementId> ids)
        {
            try { uidoc.Selection.SetElementIds(ids); } catch { }
            try
            {
                var view = EnsureGraphicalView(uidoc, doc);
                if (view == null) { try { uidoc.ShowElements(ids); } catch { } return; }
                BoundingBoxXYZ union = null;
                foreach (var id in ids)
                {
                    var bb = doc.GetElement(id)?.get_BoundingBox(null);
                    if (bb == null) continue;
                    if (union == null) union = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
                    else
                    {
                        union.Min = new XYZ(Math.Min(union.Min.X, bb.Min.X), Math.Min(union.Min.Y, bb.Min.Y), Math.Min(union.Min.Z, bb.Min.Z));
                        union.Max = new XYZ(Math.Max(union.Max.X, bb.Max.X), Math.Max(union.Max.Y, bb.Max.Y), Math.Max(union.Max.Z, bb.Max.Z));
                    }
                }
                if (union == null) return;
                var pad = 5.0; // ft — breathing room around the change
                var uiview = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                if (uiview != null)
                    uiview.ZoomAndCenterRectangle(
                        new XYZ(union.Min.X - pad, union.Min.Y - pad, union.Min.Z),
                        new XYZ(union.Max.X + pad, union.Max.Y + pad, union.Max.Z));
                else
                    uidoc.ShowElements(ids);   // fallback — may pop "No good view found"
            }
            catch { /* zoom is best-effort; the selection highlight already landed */ }
        }

        // Checkmark badges: TemporaryGraphicsManager (2022+). Not undoable,
        // never saved, Clear() next turn — capped so a 352-wall fill doesn't
        // rain 352 sprites.
        private const int BadgeCap = 40;
        private static void TryAddBadges(Document doc, List<ElementId> ids)
        {
            try
            {
                var bmp = EnsureBadgeBmp();
                if (bmp == null) return;
                var mgr = TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc);
                var viewId = doc.ActiveView.Id;
                foreach (var id in ids.Take(BadgeCap))
                {
                    var bb = doc.GetElement(id)?.get_BoundingBox(null);
                    if (bb == null) continue;
                    var top = new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, bb.Max.Z);
                    var data = new InCanvasControlData(bmp, top);
                    _badgeIds.Add(mgr.AddControl(data, viewId));
                }
            }
            catch { /* badges are decoration — never fail the receipt */ }
        }

        private static void ClearVisuals(UIApplication app, Document doc)
        {
            try
            {
                if (_badgeIds.Count > 0)
                {
                    var mgr = TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc);
                    foreach (var b in _badgeIds) { try { mgr.RemoveControl(b); } catch { } }
                    _badgeIds.Clear();
                }
            }
            catch { }
        }

        // ─── capture (P2, confirm-gated) ───────────────────────────────────

        private static string TryExportActiveView(UIApplication app, string tag)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return null;
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "Bina", "RevitSync", "receipts");
                Directory.CreateDirectory(dir);
                var stem = Path.Combine(dir, $"receipt-{DateTime.Now:HHmmss}-{tag}");
                var opts = new ImageExportOptions
                {
                    ExportRange = ExportRange.VisibleRegionOfCurrentView,
                    FilePath = stem,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    PixelSize = 800,
                    ZoomType = ZoomFitType.FitToPage,
                };
                doc.ExportImage(opts);
                // Revit appends view-name suffixes — glob for the real file.
                var made = Directory.GetFiles(dir, Path.GetFileName(stem) + "*").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                return made;
            }
            catch { return null; }   // GPU-less rigs / RDP — degrade to flash-only
        }

        // 16x16 24bpp BMP checkmark written by hand (no System.Drawing dep).
        // Teal RGB(0,128,128) background = Revit's transparency key.
        private static string _badgeBmpPath;
        private static string EnsureBadgeBmp()
        {
            try
            {
                if (_badgeBmpPath != null && File.Exists(_badgeBmpPath)) return _badgeBmpPath;
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "Bina", "RevitSync");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "receipt-badge.bmp");
                const int W = 16, H = 16;
                bool[,] check = new bool[H, W];
                // simple ✓ raster
                int[][] px = {
                    new[]{3,8},new[]{4,9},new[]{5,10},new[]{6,11},new[]{7,10},new[]{8,9},new[]{9,8},new[]{10,7},new[]{11,6},new[]{12,5},
                    new[]{4,8},new[]{5,9},new[]{6,10},new[]{7,11},new[]{8,10},new[]{9,9},new[]{10,8},new[]{11,7},new[]{12,6},
                };
                foreach (var p in px) if (p[1] < H && p[0] < W) check[p[1], p[0]] = true;
                int rowBytes = ((W * 3 + 3) / 4) * 4;
                int dataSize = rowBytes * H;
                using (var fs = new FileStream(path, FileMode.Create))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write((byte)'B'); bw.Write((byte)'M');
                    bw.Write(54 + dataSize); bw.Write(0); bw.Write(54);
                    bw.Write(40); bw.Write(W); bw.Write(H); bw.Write((short)1); bw.Write((short)24);
                    bw.Write(0); bw.Write(dataSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
                    for (int y = H - 1; y >= 0; y--)   // bottom-up rows
                    {
                        for (int x = 0; x < W; x++)
                        {
                            if (check[y, x]) { bw.Write((byte)74); bw.Write((byte)163); bw.Write((byte)22); }   // BGR green
                            else { bw.Write((byte)128); bw.Write((byte)128); bw.Write((byte)0); }               // teal key
                        }
                        for (int p = W * 3; p < rowBytes; p++) bw.Write((byte)0);
                    }
                }
                _badgeBmpPath = path;
                return path;
            }
            catch { return null; }
        }
    }
}
