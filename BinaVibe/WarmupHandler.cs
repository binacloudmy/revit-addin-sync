// WarmupHandler — pays the expensive one-time first-regeneration of a
// freshly opened model at LOAD, on the UI thread, before the user issues
// their first build.
//
// Measured (2026-06-02, large model): the FIRST tx.Commit() after open ran
// 58,620ms (full first regen / lazy load), every edit after = 11-67ms. That
// 58s, plus a ~30s idle-starvation caused by BulkIndex walking the model on
// a background thread at the same moment, is the ~90s "freeze + not
// responding" on the first build of a session.
//
// This handler creates a throwaway WALL in a TransactionGroup right after the
// document opens and rolls it back, so the heavy first-regen (geometry + joins
// + room bounding) is paid invisibly here instead of freezing the user's first
// mutation. doc.Regenerate() and a datum SketchPlane were both too light to
// trigger it (~3ms / ~48ms). It then invokes AfterWarm (the bulk index) only
// once warming is done, so the index walk no longer contends for idle.

using System;
using System.Collections.Concurrent;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe
{
    public sealed class WarmupHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Document> _pending = new();

        /// <summary>Runs after a document is warmed (on the UI thread). Used
        /// to kick the bulk index only once the first regen is paid.</summary>
        public Action<Document>? AfterWarm { get; set; }

        public void Enqueue(Document doc) => _pending.Enqueue(doc);

        public void Execute(UIApplication app)
        {
            while (_pending.TryDequeue(out var doc))
            {
                if (doc == null || !doc.IsValidObject) continue;
                if (doc.IsReadOnly || doc.IsLinked || doc.IsFamilyDocument) continue;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // A REAL (tiny, dependency-free) change forces the expensive
                    // first-transaction regen that a freshly opened large model
                    // pays once (~58-71s measured). doc.Regenerate() alone is a
                    // no-op (nothing dirty → ~3ms), so it never absorbed the
                    // cost. Create a throwaway SketchPlane (no level/view/geometry
                    // dependency — valid in any project doc), commit (pays the
                    // first regen HERE, at load), then roll the whole group back
                    // so it leaves no trace and no undo-stack entry. Warnings are
                    // swallowed so it can never block on a modal dialog.
                    using (var tg = new TransactionGroup(doc, "BinaVibe: warm-up"))
                    {
                        tg.Start();
                        using (var tx = new Transaction(doc, "BinaVibe: warm-up edit"))
                        {
                            tx.Start();
                            var fho = tx.GetFailureHandlingOptions();
                            fho.SetFailuresPreprocessor(new SwallowWarnings());
                            fho.SetClearAfterRollback(true);
                            tx.SetFailureHandlingOptions(fho);
                            // Create a throwaway WALL (not a datum) so the warm-up
                            // exercises the SAME heavy first-regen path as the
                            // user's real build — geometry + joins + room bounding.
                            // A lighter SketchPlane logged ~48ms and did NOT trigger
                            // the cold ~70s regen. Falls back to a SketchPlane only
                            // if the model has no level to host a wall.
                            var level = new FilteredElementCollector(doc)
                                .OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                            if (level != null)
                            {
                                var line = Line.CreateBound(XYZ.Zero, new XYZ(1.0, 0, 0));
                                Wall.Create(doc, line, level.Id, false);
                            }
                            else
                            {
                                SketchPlane.Create(
                                    doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                            }
                            tx.Commit();  // ← pays the first regen at load
                        }
                        tg.RollBack();    // ← undo: no trace, no undo entry
                    }
                    sw.Stop();
                    System.Diagnostics.Debug.WriteLine(
                        $"[BinaVibe][timing] warm-up edit (real, rolled back)={sw.ElapsedMilliseconds}ms " +
                        "(paid at load, not on first build)");
                }
                catch (Exception ex)
                {
                    // Best-effort: on any failure the first real build just pays
                    // the cost itself (same as before). Never trap the user.
                    System.Diagnostics.Debug.WriteLine($"[BinaVibe] warm-up edit failed (best-effort): {ex.Message}");
                }

                try { AfterWarm?.Invoke(doc); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BinaVibe] warm-up after-hook failed: {ex.Message}");
                }
            }
        }

        public string GetName() => "BinaVibe.WarmupHandler";
    }

    /// <summary>Deletes warnings during a transaction so the warm-up edit can
    /// never block on a modal failure dialog (it runs unattended at load).</summary>
    internal sealed class SwallowWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning)
                    a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}
