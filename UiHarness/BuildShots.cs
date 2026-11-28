using System;
using System.IO;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim.Check;
using RevitWebAppSync.UI.Copilot;

namespace UiHarness
{
    /// <summary>`UiHarness --shot-build &lt;dir&gt;` (AI-642 step 1): screens 8, 9, 10 and 12 at 520×906 from the real ASWARA
    /// snapshot, no engine fixture. The build result is cad2bim/aswara-run.real.json (captured from a real Revit build
    /// of ASWARA's main plan) when present; until then the planned counts of the main plan with no Revit warnings,
    /// and the PNG names say "planned". 4 screens light @125% + W10 dark @100/125/150%.</summary>
    internal static class BuildShots
    {
        private sealed class Sink : IBuildRequestSink
        {
            public BuildRequest Request { get; set; }
            public event Action<BuildReport> Completed;
            public void Fire(BuildReport r) { Request = null; Completed?.Invoke(r); }
        }

        private sealed class Raiser : IBuildEventRaiser { public bool Raise() => true; }

        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            string data = Path.Combine(AppContext.BaseDirectory, "cad2bim");
            string real = Path.Combine(data, "aswara-run.real.json");
            string tag = File.Exists(real) ? "real" : "planned";
            Shot(dir, "w08-build-" + tag + "-light-125.png", CheckScreen.Building, false, 1.25);
            Shot(dir, "w09-issues-" + tag + "-light-125.png", CheckScreen.Built, false, 1.25);
            Shot(dir, "w10-review-" + tag + "-light-125.png", CheckScreen.Review, false, 1.25);
            foreach (double s in new[] { 1.0, 1.25, 1.5 }) Shot(dir, "w10-review-" + tag + "-dark-" + (int)(s * 100) + ".png", CheckScreen.Review, true, s);
            Shot(dir, "w12-accept-" + tag + "-light-125.png", CheckScreen.Accept, false, 1.25);
        }

        private static void Shot(string dir, string file, CheckScreen screen, bool dark, double scale)
        {
            CopilotTheme.SetDark(dark);
            string data = Path.Combine(AppContext.BaseDirectory, "cad2bim");
            SheetSnapshot sheet = SheetSnapshot.FromJson(File.ReadAllText(Path.Combine(data, "aswara-sheet.json")));
            // the request needs a reader sheet behind the snapshot; the harness never runs it, so the main plan's walls stay empty
            sheet.Source = new DwgPlanSheet();
            foreach (PlanSnap p in sheet.Plans) sheet.Source.Plans.Add(new DwgPlan { N = p.N, Cluster = new Cad2Bim.PlanCluster() });

            var vm = new CheckViewModel(_ => null);
            vm.Show(sheet, new TargetInfo { DocTitle = "ASWARA_ARCH", Levels = { new LevelInfo { Name = "Level 1" } } });
            for (int i = 0; i < 4; i++) vm.Next();     // 0 questions: pre-flight → consultant → gate 1 → gate 2 → rows
            var sink = new Sink();
            vm.UseBuilder(sink, new Raiser());
            vm.Build();
            if (screen != CheckScreen.Building)
            {
                sink.Fire(Report(Path.Combine(data, "aswara-run.real.json"), vm.State.MainPlan.Plan));
                if (screen >= CheckScreen.Review) vm.Next();
                if (screen == CheckScreen.Accept) vm.Next();
            }
            CheckShots.Render(new CheckPane(vm), Path.Combine(dir, file), scale);
        }

        /// <summary>The captured real run, or the main plan's planned counts with no warnings.</summary>
        private static BuildReport Report(string realPath, PlanSnap plan)
        {
            if (!File.Exists(realPath))
                return new BuildReport { Walls = plan.Walls, Doors = plan.Doors, Windows = plan.Windows, Rooms = plan.Rooms };
            JObject j = JObject.Parse(File.ReadAllText(realPath));
            var r = new BuildReport
            {
                Walls = (int)j["Walls"], Doors = (int)j["Doors"], Windows = (int)j["Windows"], Rooms = (int)j["Rooms"],
                SkippedWalls = (int?)j["SkippedWalls"] ?? 0, SkippedOpenings = (int?)j["SkippedOpenings"] ?? 0, SkippedRooms = (int?)j["SkippedRooms"] ?? 0,
            };
            foreach (JObject g in (JArray)j["Failures"] ?? new JArray())
                r.Failures.Groups.Add(g.ToObject<FailureGroup>());
            return r;
        }
    }
}
