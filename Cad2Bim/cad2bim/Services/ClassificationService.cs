namespace Cad2Bim.Services {
    // Sole bridge between ViewModels and the Model layer.
    public class ClassificationService {
        private List<GeometryElement> _geometry = new();
        private List<Segment> _segments = new();
        private List<Arc> _arcs = new();
        private List<TextElement> _texts = new();

        public IReadOnlyList<GeometryElement> Geometry => _geometry;
        public IReadOnlyList<Arc> Arcs => _arcs;
        public IReadOnlyList<TextElement> Texts => _texts;
        public int SegmentCount => _segments.Count;
        public bool HasData => _geometry.Count > 0;

        /// <summary>Millimetres per drawing unit in the file that was loaded.</summary>
        public double Scale { get; private set; } = 1.0;

        public void Load(string filePath) => Load(CadRenderSource.Read(filePath));

        public void Load(ACadSharp.CadDocument document) {
            var (rawGeometry, rawText) = CadLoader.LoadCadEntities(document);

            // The drawing is restated in millimetres here rather than each threshold being
            // scaled at its point of use, so one set of settings holds for every file.
            Scale = Units.Resolve(document, rawGeometry.OfType<Segment>().ToList());
            var (geometry, texts) = Units.Normalize(rawGeometry, rawText, Scale);

            _geometry = geometry;
            _texts = texts;
            _segments = geometry.OfType<Segment>().ToList();
            _arcs = geometry.OfType<Arc>().ToList();
        }

        public List<Wall> Classify(double sMin, double sMax) {
            Wall.SMin = sMin;
            Wall.SMax = sMax;
            return CadClassifier.ClassifyWalls(_segments);
        }

        /// <summary>Everything after walls: junctions, then openings, then rooms.</summary>
        public (WallGraph Graph, List<Opening> Openings, List<Space> Spaces) Elaborate(List<Wall> walls) {
            WallGraph graph = CadClassifier.CreateTopologicalPoints(walls);
            List<Opening> openings = CadClassifier.ClassifyOpenings(walls, _segments, _arcs);
            List<Space> spaces = CadClassifier.ClassifySpaces(graph, _texts);
            CadClassifier.SplitWalls(walls, spaces);

            return (graph, openings, spaces);
        }
    }
}
