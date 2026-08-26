using ACadSharp;
using ACadSharp.Entities;

using Cad2Bim;
using Cad2Bim.Services;

namespace Cad2Bim.Headless {
    // Console harness for the classification model. The WPF viewer is the tuning surface;
    // this is the measuring surface — it runs the same code path with no UI, so a result is
    // comparable across machines, repeatable in CI, and reviewable in a diff.
    internal static class Program {
        private static int Main(string[] args) {
            if (args.Length == 0) {
                Console.Error.WriteLine("usage: cad2bim-headless <file.dwg|file.dxf> [sMinMm] [sMaxMm]");
                return 2;
            }

            string path = args[0];
            if (!File.Exists(path)) {
                Console.Error.WriteLine($"no such file: {path}");
                return 2;
            }

            CadDocument document = CadRenderSource.Read(path);
            var (rawGeometry, rawText) = CadLoader.LoadCadEntities(document);

            double scale = Units.Resolve(document, rawGeometry.OfType<Segment>().ToList());
            var (geometry, texts) = Units.Normalize(rawGeometry, rawText, scale);

            List<Segment> segments = geometry.OfType<Segment>().ToList();
            List<Cad2Bim.Arc> arcs = geometry.OfType<Cad2Bim.Arc>().ToList();

            Console.WriteLine($"file        {Path.GetFileName(path)}");
            Console.WriteLine($"units       {document.Header.InsUnits} ({scale:0.####} mm per unit)");
            ReportEntityCensus(document);

            Console.WriteLine();
            Console.WriteLine("-- reaching the classifier --");
            Console.WriteLine($"  segments  {segments.Count}");
            Console.WriteLine($"  arcs      {arcs.Count}");
            Console.WriteLine($"  texts     {texts.Count}");
            Console.WriteLine($"  (the renderer resolves {CadRenderSource.Flatten(document).Count} polylines; " +
                              "the difference is geometry inside blocks and polylines the loader drops)");

            if (args.Contains("--texts")) {
                Console.WriteLine();
                Console.WriteLine("-- text in the drawing --");
                foreach (TextElement item in texts.Take(60)) {
                    Console.WriteLine($"  {item.Text}");
                }
            }

            Wall.SMin = args.Length > 1 && double.TryParse(args[1], out double smin) ? smin : Units.DefaultMinWallThicknessMm;
            Wall.SMax = args.Length > 2 && double.TryParse(args[2], out double smax) ? smax : Units.DefaultMaxWallThicknessMm;

            List<Wall> walls = CadClassifier.ClassifyWalls(segments);
            Console.WriteLine();
            Console.WriteLine($"-- walls (SMin={Wall.SMin:0.##} mm, SMax={Wall.SMax:0.##} mm) --");
            Console.WriteLine($"  walls     {walls.Count}");
            Console.WriteLine($"  consumed  {walls.Count * 2} of {segments.Count} segments " +
                              $"({Share(walls.Count * 2, segments.Count)})");
            if (walls.Count > 0) {
                var thicknesses = walls.Select(w => w.Thickness).OrderBy(t => t).ToList();
                Console.WriteLine($"  thickness min={thicknesses[0]:0.#} " +
                                  $"median={thicknesses[thicknesses.Count / 2]:0.#} " +
                                  $"max={thicknesses[^1]:0.#} mm");
            }

            WallGraph graph = CadClassifier.CreateTopologicalPoints(walls);
            Console.WriteLine();
            Console.WriteLine("-- topology --");
            Console.WriteLine($"  nodes     {graph.Nodes.Count}");
            Console.WriteLine($"  edges     {graph.Edges.Count}");
            Console.WriteLine($"  loose     {graph.Nodes.Count(n => n.Degree < 2)} " +
                              $"(ends that meet nothing — every one is a hole in a room boundary)");
            Console.WriteLine($"  junctions {graph.Nodes.Count(n => n.Degree >= 3)}");

            List<Opening> openings = CadClassifier.ClassifyOpenings(walls, segments, arcs);
            Console.WriteLine();
            Console.WriteLine("-- openings --");
            Console.WriteLine($"  openings  {openings.Count}");
            Console.WriteLine($"  doors     {openings.Count(o => o.IsDoor)} (a swing hinged at a jamb)");
            Console.WriteLine($"  other     {openings.Count(o => !o.IsDoor)} (window or plain opening)");
            if (openings.Count > 0) {
                var widths = openings.Select(o => o.Width).OrderBy(w => w).ToList();
                Console.WriteLine($"  width     min={widths[0]:0} median={widths[widths.Count / 2]:0} max={widths[^1]:0} mm");
            }

            List<Space> spaces = CadClassifier.ClassifySpaces(graph, texts);
            CadClassifier.SplitWalls(walls, spaces);

            Console.WriteLine();
            Console.WriteLine("-- spaces --");
            Console.WriteLine($"  rooms     {spaces.Count}");
            Console.WriteLine($"  named     {spaces.Count(s => s.Name is not null)}");
            Console.WriteLine($"  area      {spaces.Sum(s => s.Area) / 1_000_000.0:0.#} m² total");
            Console.WriteLine($"  external  {walls.Count(w => w.IsOutdoor)} of {walls.Count} walls " +
                              "border fewer than two rooms");

            foreach (Space space in spaces.OrderByDescending(s => s.Area).Take(10)) {
                Console.WriteLine($"    {space.Area / 1_000_000.0,8:0.0} m²  {space.Name ?? "(unnamed)"}");
            }

            return 0;
        }

        // Which entity types the drawing actually holds — the honest answer to "why did the
        // classifier only see N segments".
        private static void ReportEntityCensus(CadDocument document) {
            Dictionary<string, int> census = new();
            foreach (Entity entity in document.Entities) {
                string key = entity.GetType().Name;
                census[key] = census.GetValueOrDefault(key) + 1;
            }

            Console.WriteLine();
            Console.WriteLine("-- model space entity census --");
            foreach (var (name, count) in census.OrderByDescending(kv => kv.Value)) {
                Console.WriteLine($"  {name,-16} {count}");
            }
        }

        private static string Share(int part, int whole) =>
            whole == 0 ? "0%" : $"{100.0 * part / whole:0.#}%";
    }
}
