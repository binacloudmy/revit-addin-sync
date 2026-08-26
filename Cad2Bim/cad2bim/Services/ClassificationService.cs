namespace Cad2Bim.Services {
    // Sole bridge between ViewModels and the Model layer.
    public class ClassificationService {
        private List<GeometryElement> _geometry = new();
        private List<Segment> _segments = new();

        public IReadOnlyList<GeometryElement> Geometry => _geometry;
        public int SegmentCount => _segments.Count;
        public bool HasData => _geometry.Count > 0;

        public void Load(string filePath) => Load(CadRenderSource.Read(filePath));

        public void Load(ACadSharp.CadDocument document) {
            var (geometry, _) = CadLoader.LoadCadEntities(document);
            _geometry = geometry;
            _segments = geometry.OfType<Segment>().ToList();
        }

        public List<Wall> Classify(double sMin, double sMax) {
            Wall.SMin = sMin;
            Wall.SMax = sMax;
            return CadClassifier.ClassifyWalls(_segments);
        }
    }
}
