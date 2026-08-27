namespace Cad2Bim.Services {
    // Sole bridge between ViewModels and the Model layer.
    public class ClassificationService {
        private CadModel _model = new();

        public IReadOnlyList<Segment> Segments => _model.Segments;
        public IReadOnlyList<Arc> Arcs => _model.Arcs;
        public IReadOnlyList<TextElement> Texts => _model.Texts;
        public IReadOnlyDictionary<string, int> LayerCensus => _model.LayerCensus;

        public int SegmentCount => _model.Segments.Count;
        public bool HasData => _model.Segments.Count > 0;

        /// <summary>Millimetres per drawing unit in the file that was loaded.</summary>
        public double Scale => _model.Scale;

        /// <summary>Which layers the classifier may look at. Edit, then load again.</summary>
        public LayerFilter Filter { get; } = new();

        public void Load(string filePath) => Load(CadRenderSource.Read(filePath));

        public void Load(ACadSharp.CadDocument document) {
            // Same traversal the viewport draws from, so the classifier sees the whole drawing:
            // blocks flattened, polylines included, text carried through, everything in
            // millimetres. The previous path read only top-level lines and arcs.
            _model = ModelSource.Read(document, Filter);
        }

        public List<Wall> Classify(double sMin, double sMax) {
            Wall.SMin = sMin;
            Wall.SMax = sMax;
            return CadClassifier.ClassifyWalls(_model.Segments);
        }

        /// <summary>Everything after walls: junctions, then openings, then rooms.</summary>
        public (WallGraph Graph, List<Opening> Openings, List<Space> Spaces) Elaborate(List<Wall> walls) {
            WallGraph graph = CadClassifier.CreateTopologicalPoints(walls);
            List<Opening> openings = CadClassifier.ClassifyOpenings(walls, _model.Segments, _model.Arcs);
            List<Space> spaces = CadClassifier.ClassifySpaces(graph, _model.Texts);
            CadClassifier.SplitWalls(walls, spaces);

            return (graph, openings, spaces);
        }
    }
}
