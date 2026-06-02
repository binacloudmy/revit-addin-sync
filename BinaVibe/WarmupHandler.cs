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
// This handler runs doc.Regenerate() in a throwaway transaction right after
// the document opens, so the first-regen cost is paid invisibly here instead
// of freezing the user's first mutation. It then invokes AfterWarm (the bulk
// index) only once warming is done, so the index walk no longer contends for
// idle while the user is mid-build.

using System;
using System.Collections.Concurrent;
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
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using (var tx = new Transaction(doc, "BinaVibe: warm-up regen"))
                    {
                        tx.Start();
                        doc.Regenerate();
                        tx.Commit();
                    }
                    sw.Stop();
                    System.Diagnostics.Debug.WriteLine(
                        $"[BinaVibe][timing] warm-up regen={sw.ElapsedMilliseconds}ms " +
                        "(paid at load, not on first build)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BinaVibe] warm-up failed: {ex.Message}");
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
}
