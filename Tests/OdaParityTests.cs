// AI-647 laptop harness: ODA vs ACadSharp on real drawings. Compiled only with
// -p:OdaSdkDir=... (HAS_ODA) and run only when these are set, so every other machine skips it:
//   BINA_ODA_BIN      folder with the ODA runtime (native + OdSwig_*_Net5)
//   BINA_ODA_KEY      the licence file (read by OdaKeyFile; its values are never written anywhere)
//   BINA_ASWARA_DWG   ASWARA floor plan
//   BINA_KIMIA_DWG    KIMIA (the large sheet)
//   BINA_ODA_REPORT   where the findings go (JSON lines), outside the repo
//   BINA_DWG_SCAN_DIR optional: folder searched for a drawing CadSourceVerdict refuses today
// Run alone: dotnet test --filter FullyQualifiedName~OdaParityTests (memory figures are per process).

#if HAS_ODA
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.Services.CadToBim.Dwg;
using RevitWebAppSync.UI.CadToBim;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public sealed class OdaFactAttribute : FactAttribute
    {
        public OdaFactAttribute()
        {
            foreach (string v in new[] { "BINA_ODA_BIN", "BINA_ODA_KEY", "BINA_ASWARA_DWG", "BINA_ODA_REPORT" })
                if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(v))) { Skip = v + " not set (laptop-only ODA harness)"; return; }
        }
    }

    [Collection("OdaHarness")]
    public class OdaParityTests
    {
        private static string Env(string name) => System.Environment.GetEnvironmentVariable(name);
        private static OdaDwgReader Oda => new OdaDwgReader(Env("BINA_ODA_BIN"), Env("BINA_ODA_KEY"));
        private static readonly AcadSharpDwgReader Acad = new AcadSharpDwgReader();
        private static readonly CadToBimSettings Defaults = new CadToBimSettings();

        private static void Report(string kind, object row)
        {
            string line = JsonSerializer.Serialize(new { at = System.DateTime.Now.ToString("s"), kind, row });
            lock (typeof(OdaParityTests)) File.AppendAllText(Env("BINA_ODA_REPORT"), line + "\n");
        }

        // ── condition 1 / FRIDAY 5: init on one thread, reads from two others ──

        [OdaFact]
        public void Threading_InitOnOneThread_ReadFromTwoOthers()
        {
            string dwg = Env("BINA_ASWARA_DWG");
            int initThread = 0;
            var first = Task.Factory.StartNew(() => { initThread = Thread.CurrentThread.ManagedThreadId; return Oda.Read(dwg); },
                                              TaskCreationOptions.LongRunning).Result;
            int odaThread = OdaRuntime.ThreadId;

            var reads = new[] { 0, 1 }.Select(_ => Task.Factory.StartNew(() => (Thread.CurrentThread.ManagedThreadId, Oda.Read(dwg)),
                                                                          TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(reads);

            Assert.Equal(odaThread, OdaRuntime.ThreadId);
            foreach (var r in reads)
            {
                Assert.NotEqual(initThread, r.Result.Item1);
                Assert.Equal(first.Document.Entities.Count(), r.Result.Item2.Document.Entities.Count());
            }
            Report("threading", new { initThread, odaThread, readThreads = reads.Select(r => r.Result.Item1).ToArray(), entities = first.Document.Entities.Count() });
        }

        // ── parity ──

        private static object Census(DwgDrawing d) => new
        {
            modelSpace = d.Document.Entities.GroupBy(e => e.GetType().Name).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            blocks = d.Document.BlockRecords.Count(),
            layers = d.Document.Layers.Count(),
            insUnits = d.Document.Header.InsUnits.ToString(),
        };

        private static object VerdictInputs(DwgDrawing d) => d.Classes
            .Where(c => c.IsAnEntity && c.InstanceCount > 0 && (c.DxfName.StartsWith("AEC", System.StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.DxfName).Select(c => c.DxfName + "=" + c.InstanceCount).ToArray();

        private static (string Verdict, string Json) PlanAnswer(DwgDrawing d)
        {
            try
            {
                DwgPlanSheet sheet = DwgPlanReader.Read(d, Defaults);
                var parts = new[] { new int[0] }.Concat(Enumerable.Range(1, System.Math.Min(3, sheet.Plans.Count)).Select(n => new[] { n }))
                    .Select(p => DwgPlanReader.Build(sheet, p).Data!.Value.GetRawText());
                return ("read", string.Join("\n", parts));
            }
            catch (System.NotSupportedException) { return ("refused", ""); }
        }

        private static object Detection(DwgDrawing d, string path)
        {
            try
            {
                DetectResult r = CadToBimViewModel.Detect(d, path, Stopwatch.StartNew(), Defaults, Defaults.SMinMm, Defaults.SMaxMm,
                    new Dictionary<string, LayerRole>(), null, CancellationToken.None);
                return new
                {
                    walls = r.Session.Walls.Count,
                    doors = r.Session.Openings.Count(o => o.IsDoor),
                    windows = r.Session.Openings.Count(o => !o.IsDoor),
                    rooms = r.Session.Spaces.Count,
                    floors = "n/a until AI-649",
                    plans = r.PlanCount,
                    census = r.Census,
                    scale = r.Session.Scale,
                };
            }
            catch (System.NotSupportedException) { return "refused"; }
        }

        private static void Compare(string label, string path)
        {
            DwgDrawing a = Acad.Read(path);
            DwgDrawing o = Oda.Read(path);
            var (va, ja) = PlanAnswer(a);
            var (vo, jo) = PlanAnswer(o);
            Report("parity", new
            {
                file = label,
                census = new { acadsharp = Census(a), oda = Census(o) },
                verdictInputs = new { acadsharp = VerdictInputs(a), oda = VerdictInputs(o) },
                verdict = new { acadsharp = va, oda = vo },
                detection = new { acadsharp = Detection(a, path), oda = Detection(o, path) },
                readDwgPlanIdentical = ja == jo,
                readDwgPlan = ja == jo ? null : new { acadsharp = ja, oda = jo },
                warnings = new { acadsharp = a.Warnings.Select(w => w.ToString()), oda = o.Warnings.Select(w => w.ToString()) },
            });
            Assert.Equal(va, vo);   // the AEC refusal must not depend on the engine
        }

        [OdaFact]
        public void Parity_Aswara() => Compare("ASWARA", Env("BINA_ASWARA_DWG"));

        [OdaFact]
        public void Parity_Kimia()
        {
            if (string.IsNullOrEmpty(Env("BINA_KIMIA_DWG"))) return;
            Compare("KIMIA", Env("BINA_KIMIA_DWG"));
        }

        /// <summary>Finds a drawing CadSourceVerdict refuses with ACadSharp today, then checks ODA refuses it too.</summary>
        [OdaFact]
        public void Parity_AecRefusal()
        {
            string dir = Env("BINA_DWG_SCAN_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            var scanned = new List<object>();
            foreach (string f in Directory.EnumerateFiles(dir, "*.dwg", SearchOption.AllDirectories)
                                          .Where(f => new FileInfo(f).Length < 15_000_000).Take(40))
            {
                string verdict;
                try { verdict = PlanAnswer(Acad.Read(f)).Verdict; } catch (System.Exception ex) { verdict = "error " + ex.GetType().Name; }
                scanned.Add(new { file = Path.GetFileName(f), verdict });
                if (verdict != "refused") continue;
                Report("aec-scan", scanned);
                Compare("AEC:" + Path.GetFileName(f), f);
                return;
            }
            Report("aec-scan", new { found = false, scanned });
        }

        // ── RAM + time (condition 2, FRIDAY 3) ──

        private static (double Seconds, long BeforeFree, long MinFree, long BeforePrivate, long PeakPrivate) Measure(System.Action read)
        {
            System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect();
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            long beforeFree = DwgReaders.FreeBytes(), minFree = beforeFree;
            long beforePrivate = proc.PrivateMemorySize64, peakPrivate = beforePrivate;
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    proc.Refresh();
                    minFree = System.Math.Min(minFree, DwgReaders.FreeBytes());
                    peakPrivate = System.Math.Max(peakPrivate, proc.PrivateMemorySize64);
                    Thread.Sleep(100);
                }
            });
            var clock = Stopwatch.StartNew();
            read();
            clock.Stop();
            stop.Cancel();
            sampler.Wait();
            return (clock.Elapsed.TotalSeconds, beforeFree, minFree, beforePrivate, peakPrivate);
        }

        [OdaFact]
        public void Memory_And_Time_BothEngines()
        {
            var files = new[] { ("ASWARA", Env("BINA_ASWARA_DWG")), ("KIMIA", Env("BINA_KIMIA_DWG")) }.Where(f => !string.IsNullOrEmpty(f.Item2));
            foreach (var (label, path) in files)
            {
                foreach (var (engine, reader) in new (string, IDwgReader)[] { ("acadsharp", Acad), ("oda", Oda) })
                {
                    // Peak during an ODA read = ODA db + the ACadSharp document it is being translated into, both alive.
                    var runs = Enumerable.Range(0, 3).Select(_ => Measure(() => reader.Read(path))).ToList();
                    System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect();
                    var proc = Process.GetCurrentProcess(); proc.Refresh();
                    // Retained = what the session holds afterwards (the document only; the ODA db is already freed).
                    long baseline = proc.PrivateMemorySize64;
                    DwgDrawing held = reader.Read(path);
                    System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect();
                    proc.Refresh();
                    long retainedMb = (proc.PrivateMemorySize64 - baseline) >> 20;
                    System.GC.KeepAlive(held);
                    Report("memory", new
                    {
                        file = label,
                        engine,
                        secondsMedian = runs.Select(r => r.Seconds).OrderBy(s => s).ElementAt(1),
                        seconds = runs.Select(r => System.Math.Round(r.Seconds, 2)),
                        privateCostMb = runs.Select(r => (r.PeakPrivate - r.BeforePrivate) >> 20),
                        retainedDocumentMb = retainedMb,
                        freeDropMb = runs.Select(r => (r.BeforeFree - r.MinFree) >> 20),
                        beforeFreeMb = runs.Select(r => r.BeforeFree >> 20),
                        privateAfterGcMb = proc.PrivateMemorySize64 >> 20,
                    });
                }
            }
        }
    }
}
#endif
