using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Cad2Bim.Services;
using Cad2Bim.ViewModels.Shapes;

namespace Cad2Bim.ViewModels {
    public class MainViewModel : ViewModelBase {
        private readonly ClassificationService _service;
        private readonly Func<string?> _pickFile;

        private readonly LayerViewModel _rawLayer = new("CAD Drawing (raw)");
        private readonly LayerViewModel _wallsLayer = new("Classified Walls", highlightIndex: 0);

        public ObservableCollection<LayerViewModel> Layers { get; }
        public SettingsViewModel Settings { get; }
        public ICommand OpenFileCommand { get; }

        private Rect _bounds = Rect.Empty;
        public Rect Bounds { get => _bounds; private set => SetField(ref _bounds, value); }

        private string _statusText = "Open a CAD file to begin.";
        public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

        public MainViewModel(ClassificationService service, Func<string?> pickFile) {
            _service = service;
            _pickFile = pickFile;

            Settings = new SettingsViewModel(Wall.SMin, Wall.SMax);
            Settings.Changed += Reclassify;

            Layers = new ObservableCollection<LayerViewModel> { _rawLayer, _wallsLayer };

            OpenFileCommand = new RelayCommand(() => {
                var path = _pickFile();
                if (path is not null) LoadFile(path);
            });
        }

        public void LoadFile(string path) {
            ACadSharp.CadDocument document;
            try {
                document = CadRenderSource.Read(path);
            }
            catch (Exception ex) {
                StatusText = $"Failed to load '{path}': {ex.Message}";
                return;
            }

            // The base layer is the CAD file itself — every drawable entity, blocks exploded,
            // curves tessellated. Geometry.cs/CadLoader feeds classification only, so what the
            // classifier keeps stays comparable against the ground truth drawn underneath it.
            var raw = CadRenderSource.Flatten(document);
            _rawLayer.Items = raw;
            Bounds = ComputeBounds(raw);

            try {
                _service.Load(document);
            }
            catch (Exception ex) {
                StatusText = $"Failed to classify '{path}': {ex.Message}";
                return;
            }

            Reclassify();
        }

        private void Reclassify() {
            if (!_service.HasData) return;

            List<Wall> walls;
            try {
                walls = _service.Classify(Settings.SMin, Settings.SMax);
            }
            catch (Exception ex) {
                StatusText = $"Classification failed: {ex.Message}";
                return;
            }

            _wallsLayer.Items = walls.Select(w => {
                var edges = w.Geometry.OfType<Segment>().ToList();
                return (object)new WallShape(ToShape(edges[0]), ToShape(edges[1]));
            }).ToList();

            StatusText = $"{_rawLayer.Items.Count} drawn, {_service.SegmentCount} segments, {walls.Count} walls  (SMin={Settings.SMin:0.###}, SMax={Settings.SMax:0.###})";
        }

        private static SegmentShape ToShape(Segment s) =>
            new(s.P1.x, s.P1.y, s.P2.x, s.P2.y);

        // Extents of the drawn geometry, so a fit-to-extents shows exactly what is rendered.
        private static Rect ComputeBounds(IReadOnlyList<object> shapes) {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var shape in shapes.OfType<PolylineShape>()) {
                foreach (var (x, y) in shape.Points) {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            return minX > maxX ? Rect.Empty : new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
