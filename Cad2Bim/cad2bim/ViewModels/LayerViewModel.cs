namespace Cad2Bim.ViewModels {
    // One toggleable viewport layer. Adding Openings/Spaces later = one more instance
    // (with the next highlight index) + a Draw case in CadViewport for its shape type.
    public class LayerViewModel : ViewModelBase {
        public string Name { get; }

        // null = neutral base geometry; otherwise an index into the viewport's highlight palette.
        public int? HighlightIndex { get; }

        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }

        private IReadOnlyList<object> _items = Array.Empty<object>();
        public IReadOnlyList<object> Items { get => _items; set => SetField(ref _items, value); }

        public LayerViewModel(string name, int? highlightIndex = null) {
            Name = name;
            HighlightIndex = highlightIndex;
        }
    }
}
