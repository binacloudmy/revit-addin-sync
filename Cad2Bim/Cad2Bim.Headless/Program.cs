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
                Console.Error.WriteLine(
                    "usage: cad2bim-headless <file.dwg|file.dxf> [sMinMm] [sMaxMm] [options]\n" +
                    "  --include=A-WALL,DINDING*   only these layers reach the classifier\n" +
                    "  --exclude=A-FURN*,*-TEXT    drop these layers (wins over include)\n" +
                    "  --hatch                     keep hatch boundaries\n" +
                    "  --dimensions                keep dimension geometry\n" +
                    "  --layers                    list every layer and its segment count\n" +
                    "  --texts                     list the text found in the drawing\n" +
                    "  --faces                     why room loops were kept or dropped\n" +
                    "  --merge=250                 gap between pieces of one wall (mm)\n" +
                    "  --bridge=2000               widest doorway a room boundary may jump (mm)\n" +
                    "  --ifc=out.ifc               write the result as an IFC model");
                return 2;
            }

            string path = args[0];
            if (!File.Exists(path)) {
                Console.Error.WriteLine($"no such file: {path}");
                return 2;
            }

            var filter = new LayerFilter {
                IncludeHatch = args.Contains("--hatch"),
                IncludeDimensions = args.Contains("--dimensions"),
            };
            filter.Include.AddRange(Patterns(args, "--include="));
            filter.Exclude.AddRange(Patterns(args, "--exclude="));

            CadDocument document = CadRenderSource.Read(path);
            CadModel model = ModelSource.Read(document, filter);

            Console.WriteLine($"file        {Path.GetFileName(path)}");
            Console.WriteLine($"units       {document.Header.InsUnits} ({model.Scale:0.####} mm per unit)");
            ReportEntityCensus(document);

            Console.WriteLine();
            Console.WriteLine("-- reaching the classifier --");
            Console.WriteLine($"  segments  {model.Segments.Count}");
            Console.WriteLine($"  arcs      {model.Arcs.Count}");
            Console.WriteLine($"  texts     {model.Texts.Count}");
            if (model.DroppedByFilter > 0) {
                Console.WriteLine($"  dropped   {model.DroppedByFilter} by the layer filter");
            }

            if (args.Contains("--layers")) {
                Console.WriteLine();
                Console.WriteLine("-- layers by segment count --");
                foreach (var (name, count) in model.LayerCensus.OrderByDescending(kv => kv.Value)) {
                    string kept = filter.Allows(name, CadSource.Geometry) ? " " : "x";
                    Console.WriteLine($"  {kept} {count,7}  {(name.Length == 0 ? "(no layer)" : name)}");
                }
            }

            if (args.Contains("--texts")) {
                Console.WriteLine();
                Console.WriteLine("-- text in the drawing --");
                foreach (TextElement item in model.Texts.Take(80)) {
                    Console.WriteLine($"  [{item.Layer}] {item.Text}");
                }
            }

            if (model.Segments.Count > 0) {
                double minX = model.Segments.Min(s => Math.Min(s.P1.x, s.P2.x));
                double maxX = model.Segments.Max(s => Math.Max(s.P1.x, s.P2.x));
                double minY = model.Segments.Min(s => Math.Min(s.P1.y, s.P2.y));
                double maxY = model.Segments.Max(s => Math.Max(s.P1.y, s.P2.y));
                Console.WriteLine($"  extents   x {minX:0} to {maxX:0} mm, y {minY:0} to {maxY:0} mm");
                Console.WriteLine($"  size      {(maxX - minX) / 1000.0:0.#} m x {(maxY - minY) / 1000.0:0.#} m, " +
                                  $"centre {(minX + maxX) / 2000.0:0.#} m, {(minY + maxY) / 2000.0:0.#} m from origin");
            }

            Wall.SMin = Number(args, 1) ?? Units.DefaultMinWallThicknessMm;
            Wall.SMax = Number(args, 2) ?? Units.DefaultMaxWallThicknessMm;

            List<Wall> walls = CadClassifier.ClassifyWalls(model.Segments);
            Console.WriteLine();
            Console.WriteLine($"-- walls (SMin={Wall.SMin:0.##} mm, SMax={Wall.SMax:0.##} mm) --");
            Console.WriteLine($"  walls     {walls.Count}");
            Console.WriteLine($"  consumed  {walls.Count * 2} of {model.Segments.Count} segments " +
                              $"({Share(walls.Count * 2, model.Segments.Count)})");
            if (walls.Count > 0) {
                var thicknesses = walls.Select(w => w.Thickness).OrderBy(t => t).ToList();
                Console.WriteLine($"  thickness min={thicknesses[0]:0.#} " +
                                  $"median={thicknesses[thicknesses.Count / 2]:0.#} " +
                                  $"max={thicknesses[^1]:0.#} mm");
            }

            double mergeGap = Option(args, "--merge=") ?? CadClassifier.DefaultMergeGapMm;
            double bridgeGap = Option(args, "--bridge=") ?? CadClassifier.DefaultGapBridgeMm;
            WallGraph graph = CadClassifier.CreateTopologicalPoints(
                walls, CadClassifier.DefaultJunctionToleranceMm, bridgeGap, mergeGap);
            Console.WriteLine();
            Console.WriteLine("-- topology --");
            Console.WriteLine($"  nodes     {graph.Nodes.Count}");
            Console.WriteLine($"  edges     {graph.Edges.Count}");
            Console.WriteLine($"  loose     {graph.Nodes.Count(n => n.Degree < 2)} " +
                              "(ends that meet nothing — every one is a hole in a room boundary)");
            Console.WriteLine($"  junctions {graph.Nodes.Count(n => n.Degree >= 3)}");
            Console.WriteLine($"  settings  merge={mergeGap:0} mm, bridge={bridgeGap:0} mm");

            if (args.Contains("--faces")) {
                var lengths = walls.Select(w => w.Centerline.Length).OrderBy(l => l).ToList();
                if (lengths.Count > 0) {
                    Console.WriteLine($"  wall len  min={lengths[0]:0} median={lengths[lengths.Count/2]:0} max={lengths[^1]:0} mm");
                }
                // connected components over the graph
                var parent = Enumerable.Range(0, graph.Nodes.Count).ToArray();
                int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                foreach (var e in graph.Edges) { int a = Find(e.A), b = Find(e.B); if (a != b) parent[a] = b; }
                int comps = Enumerable.Range(0, graph.Nodes.Count).Count(i => Find(i) == i);
                Console.WriteLine($"  components {comps}  (cycles = {graph.Edges.Count - graph.Nodes.Count + comps})");
            }

            List<Opening> openings = CadClassifier.ClassifyOpenings(walls, model.Segments, model.Arcs);
            Console.WriteLine();
            Console.WriteLine("-- openings --");
            Console.WriteLine($"  openings  {openings.Count}");
            Console.WriteLine($"  doors     {openings.Count(o => o.IsDoor)} (a swing hinged at a jamb)");
            Console.WriteLine($"  other     {openings.Count(o => !o.IsDoor)} (window or plain opening)");
            if (openings.Count > 0) {
                var widths = openings.Select(o => o.Width).OrderBy(w => w).ToList();
                Console.WriteLine($"  width     min={widths[0]:0} median={widths[widths.Count / 2]:0} max={widths[^1]:0} mm");
            }

            List<Space> spaces = CadClassifier.ClassifySpaces(graph, model.Texts);
            CadClassifier.SplitWalls(walls, spaces);

            Console.WriteLine();
            Console.WriteLine("-- spaces --");
            Console.WriteLine($"  rooms     {spaces.Count}");
            Console.WriteLine($"  named     {spaces.Count(s => s.Name is not null)}");
            Console.WriteLine($"  area      {spaces.Sum(s => s.Area) / 1_000_000.0:0.#} m² total");
            Console.WriteLine($"  external  {walls.Count(w => w.IsOutdoor)} of {walls.Count} walls " +
                              "border fewer than two rooms");

            if (args.Contains("--faces")) {
            Console.WriteLine($"  [faces] wrong-winding={Diagnostics.Dropped} " +
                              $"kept={Diagnostics.Areas.Count} too-small={Diagnostics.TooSmall}");
                if (Diagnostics.Areas.Count > 0) {
                    var sorted = Diagnostics.Areas.OrderBy(a => a).ToList();
                    Console.WriteLine($"  [faces] area m2: min={sorted[0]/1e6:0.###} " +
                                      $"median={sorted[sorted.Count/2]/1e6:0.###} max={sorted[^1]/1e6:0.#}");
                }
            }

            string? ifcPath = args.FirstOrDefault(a => a.StartsWith("--ifc=", StringComparison.OrdinalIgnoreCase))?[6..];
            if (!string.IsNullOrWhiteSpace(ifcPath)) {
                var exporter = new IfcExporter {
                    ProjectName = Path.GetFileNameWithoutExtension(path),
                    // The file's own timestamp, so the same drawing always exports the same
                    // bytes and two exports can be diffed.
                    Timestamp = File.GetLastWriteTimeUtc(path),
                };
                exporter.Write(ifcPath, walls, spaces);

                var written = new FileInfo(ifcPath);
                Console.WriteLine();
                Console.WriteLine("-- ifc --");
                Console.WriteLine($"  wrote     {written.FullName}");
                Console.WriteLine($"  size      {written.Length / 1024.0:0.#} KB");
                Console.WriteLine($"  contains  {walls.Count} walls, {spaces.Count} spaces, " +
                                  $"wall height {exporter.WallHeightMm:0} mm (assumed - a plan carries no height)");
            }

            foreach (Space space in spaces.OrderByDescending(s => s.Area).Take(15)) {
                Console.WriteLine($"    {space.Area / 1_000_000.0,8:0.0} m²  {space.Name ?? "(unnamed)"}");
            }

            return 0;
        }

        // Which entity types the drawing holds at the top level, before blocks are opened up.
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

        /// <summary>Positional number; flags may sit anywhere after it.</summary>
        private static double? Option(string[] args, string prefix) {
            string? found = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return found is not null && double.TryParse(found[prefix.Length..], out double value) ? value : null;
        }

        private static double? Number(string[] args, int position) =>
            args.Length > position && double.TryParse(args[position], out double value) ? value : null;

        private static IEnumerable<string> Patterns(string[] args, string prefix) =>
            args.Where(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .SelectMany(a => a[prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);

        private static string Share(int part, int whole) =>
            whole == 0 ? "0%" : $"{100.0 * part / whole:0.#}%";
    }
}
