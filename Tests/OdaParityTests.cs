// AI-647 laptop harness: ODA vs ACadSharp on real drawings. Compiled only with
// -p:OdaSdkDir=... (HAS_ODA) and run only when these are set, so every other machine skips it:
//   BINA_ODA_BIN      folder with the ODA runtime (native + OdSwig_*_Net5)
//   BINA_ODA_KEY      the licence file (read by OdaKeyFile; its values are never written anywhere)
//   BINA_ASWARA_DWG   ASWARA floor plan
//   BINA_KIMIA_DWG    KIMIA (the large sheet)
//   BINA_ODA_REPORT   where the findings go (JSON lines), outside the repo
//   BINA_DWG_SCAN_DIR optional: folder searched for a drawing CadSourceVerdict refuses today
//   BINA_MEM_FILE / BINA_MEM_ENGINE optional: limit memory / leak / parity export to one file (ASWARA/KIMIA) / engine (acadsharp/oda)
//   BINA_PARITY_LABEL / BINA_PARITY_PATH optional: which drawing Parity_Export / Parity_Compare use
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
            int first = Task.Factory.StartNew(() => { initThread = Thread.CurrentThread.ManagedThreadId; return Oda.Read(dwg).Document.Entities.Count(); },
                                              TaskCreationOptions.LongRunning).Result;   // counts only: never hold more than one document
            int odaThread = OdaRuntime.ThreadId;

            var reads = new[] { 0, 1 }.Select(_ => Task.Factory.StartNew(() => (Thread.CurrentThread.ManagedThreadId, Oda.Read(dwg).Document.Entities.Count()),
                                                                          TaskCreationOptions.LongRunning)).ToArray();
            Task.WaitAll(reads);

            Assert.Equal(odaThread, OdaRuntime.ThreadId);
            foreach (var r in reads)
            {
                Assert.NotEqual(initThread, r.Result.Item1);
                Assert.Equal(first, r.Result.Item2);
            }
            Report("threading", new { initThread, odaThread, readThreads = reads.Select(r => r.Result.Item1).ToArray(), entities = first });
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

        // Parity runs one engine per process (both documents plus the ODA runtime in one process
        // was what the watchdog killed): Parity_Export writes this engine's result, Parity_Compare
        // reads both. BINA_PARITY_LABEL names the file (ASWARA / KIMIA / AEC), BINA_PARITY_PATH
        // overrides its path, BINA_MEM_ENGINE picks the engine.
        private static string ParityPath(string label, string engine) =>
            Path.Combine(Path.GetDirectoryName(Env("BINA_ODA_REPORT"))!,
                         "AI-647-parity-" + string.Concat(label.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + "-" + engine + ".json");

        private static string LabelPath(string label) =>
            !string.IsNullOrEmpty(Env("BINA_PARITY_PATH")) ? Env("BINA_PARITY_PATH")
            : label == "KIMIA" ? Env("BINA_KIMIA_DWG") : Env("BINA_ASWARA_DWG");

        [OdaFact]
        public void Parity_Export()
        {
            string label = Env("BINA_PARITY_LABEL") ?? "ASWARA";
            string engine = Env("BINA_MEM_ENGINE") ?? "acadsharp";
            string path = LabelPath(label);
            DwgDrawing d = engine == "oda" ? Oda.Read(path) : Acad.Read(path);
            var (verdict, json) = PlanAnswer(d);
            var result = new Dictionary<string, object>
            {
                ["census"] = Census(d),
                ["verdictInputs"] = VerdictInputs(d),
                ["verdict"] = verdict,
                ["detection"] = Detection(d, path),
                ["readDwgPlan"] = json,
                ["warnings"] = d.Warnings.Select(w => w.ToString()).ToArray(),
                ["timings"] = d.Timings,
            };
            File.WriteAllText(ParityPath(label, engine), JsonSerializer.Serialize(result));
        }

        [OdaFact]
        public void Parity_Compare()
        {
            string label = Env("BINA_PARITY_LABEL") ?? "ASWARA";
            JsonElement a = JsonDocument.Parse(File.ReadAllText(ParityPath(label, "acadsharp"))).RootElement;
            JsonElement o = JsonDocument.Parse(File.ReadAllText(ParityPath(label, "oda"))).RootElement;
            string J(JsonElement e, string k) => e.GetProperty(k).GetRawText();
            bool planSame = a.GetProperty("readDwgPlan").GetString() == o.GetProperty("readDwgPlan").GetString();
            Report("parity", new
            {
                file = label,
                census = new { acadsharp = J(a, "census"), oda = J(o, "census") },
                verdictInputs = new { acadsharp = J(a, "verdictInputs"), oda = J(o, "verdictInputs") },
                verdict = new { acadsharp = J(a, "verdict"), oda = J(o, "verdict") },
                detection = new { acadsharp = J(a, "detection"), oda = J(o, "detection") },
                readDwgPlanIdentical = planSame,
                readDwgPlan = planSame ? null : new { acadsharp = a.GetProperty("readDwgPlan").GetString(), oda = o.GetProperty("readDwgPlan").GetString() },
                warnings = new { acadsharp = J(a, "warnings"), oda = J(o, "warnings") },
            });
            Assert.Equal(J(a, "verdict"), J(o, "verdict"));   // the AEC refusal must not depend on the engine
        }

        /// <summary>Finds a drawing CadSourceVerdict refuses with ACadSharp today (export/compare it next with BINA_PARITY_PATH).</summary>
        [OdaFact]
        public void Parity_AecScan()
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
                System.GC.Collect();
                if (verdict == "refused") { Report("aec-scan", new { found = f, scanned }); return; }
            }
            Report("aec-scan", new { found = (string)null, scanned });
        }

        // ── RAM + time (condition 2, FRIDAY 3) ──

        private static (double Seconds, long BeforeFree, long MinFree, long BeforePrivate, long PeakPrivate, Dictionary<string, double> Timings)
            Measure(System.Func<DwgDrawing> read)
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
            var timings = read().Timings;
            clock.Stop();
            stop.Cancel();
            sampler.Wait();
            return (clock.Elapsed.TotalSeconds, beforeFree, minFree, beforePrivate, peakPrivate, timings);
        }

        private static IEnumerable<(string Label, string Path)> Files() =>
            new[] { ("ASWARA", Env("BINA_ASWARA_DWG")), ("KIMIA", Env("BINA_KIMIA_DWG")) }
                .Where(f => !string.IsNullOrEmpty(f.Item2) && (string.IsNullOrEmpty(Env("BINA_MEM_FILE")) || Env("BINA_MEM_FILE") == f.Item1));

        private static IEnumerable<(string Engine, IDwgReader Reader)> Engines() =>
            new (string, IDwgReader)[] { ("acadsharp", Acad), ("oda", Oda) }
                .Where(e => string.IsNullOrEmpty(Env("BINA_MEM_ENGINE")) || Env("BINA_MEM_ENGINE") == e.Item1);

        /// <summary>ODA files this process loaded from the runtime folder: the installer allowlist candidate.</summary>
        private static object OdaModules()
        {
            string bin = Path.GetFullPath(Env("BINA_ODA_BIN")).TrimEnd('\\');
            bool InBin(string file) => string.Equals(Path.GetDirectoryName(file), bin, System.StringComparison.OrdinalIgnoreCase);
            var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName).Where(InBin);
            var managed = System.AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && InBin(a.Location)).Select(a => a.Location);
            var all = native.Concat(managed).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
            return new { count = all.Count, mb = System.Math.Round(all.Sum(f => new FileInfo(f).Length) / 1048576.0, 1), files = all.Select(Path.GetFileName).OrderBy(n => n) };
        }

        [OdaFact]
        public void Memory_And_Time_BothEngines()
        {
            foreach (var (label, path) in Files())
            {
                foreach (var (engine, reader) in Engines())
                {
                    // Peak during an ODA read = ODA db + the ACadSharp document it is being translated into, both alive.
                    var runs = Enumerable.Range(0, 3).Select(_ => Measure(() => reader.Read(path))).ToList();
                    // Managed size of one held document (GC heap only): what a session keeps per drawing.
                    long heapBefore = System.GC.GetTotalMemory(true);
                    DwgDrawing held = reader.Read(path);
                    long documentMb = (System.GC.GetTotalMemory(true) - heapBefore) >> 20;
                    System.GC.KeepAlive(held);
                    held = null;
                    System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect();
                    var proc = Process.GetCurrentProcess(); proc.Refresh();
                    Report("memory", new
                    {
                        file = label,
                        engine,
                        secondsMedian = runs.Select(r => r.Seconds).OrderBy(s => s).ElementAt(1),
                        seconds = runs.Select(r => System.Math.Round(r.Seconds, 2)),
                        phases = runs.Select(r => r.Timings.ToDictionary(kv => kv.Key, kv => System.Math.Round(kv.Value, 2))),
                        privateCostMb = runs.Select(r => (r.PeakPrivate - r.BeforePrivate) >> 20),
                        freeDropMb = runs.Select(r => (r.BeforeFree - r.MinFree) >> 20),
                        beforeFreeMb = runs.Select(r => r.BeforeFree >> 20),
                        documentHeapMb = documentMb,
                        privateAfterGcMb = proc.PrivateMemorySize64 >> 20,
                        odaModules = engine == "oda" ? OdaModules() : null,
                    });
                }
            }
        }

        /// <summary>Ten reads, private bytes after each (GC + 1 s settle): flat = no leak, rising = leak.</summary>
        [OdaFact]
        public void Leak_TenReads()
        {
            foreach (var (label, path) in Files())
            {
                foreach (var (engine, reader) in Engines())
                {
                    var proc = Process.GetCurrentProcess();
                    var after = new List<long>();
                    for (int i = 0; i < 10; i++)
                    {
                        reader.Read(path);
                        System.GC.Collect(); System.GC.WaitForPendingFinalizers(); System.GC.Collect();
                        Thread.Sleep(1000);
                        proc.Refresh();
                        after.Add(proc.PrivateMemorySize64 >> 20);
                    }
                    Report("leak", new { file = label, engine, privateAfterEachReadMb = after, growthReads2To10Mb = after[9] - after[1] });
                }
            }
        }
    }
}
#endif
