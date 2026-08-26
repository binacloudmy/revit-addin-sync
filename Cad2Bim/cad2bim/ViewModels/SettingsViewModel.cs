using System.ComponentModel;

namespace Cad2Bim.ViewModels {
    public class SettingsViewModel : ViewModelBase, IDataErrorInfo {
        private double _sMin;
        private double _sMax;

        // Raised only when the pair is valid; MainViewModel wires this to re-classification.
        public event Action? Changed;

        public SettingsViewModel(double sMin, double sMax) {
            _sMin = sMin;
            _sMax = sMax;
        }

        public double SMin {
            get => _sMin;
            set { if (SetField(ref _sMin, value)) RaiseIfValid(); }
        }

        public double SMax {
            get => _sMax;
            set { if (SetField(ref _sMax, value)) RaiseIfValid(); }
        }

        private bool IsValid => _sMin > 0 && _sMin < _sMax;

        private void RaiseIfValid() {
            if (IsValid) Changed?.Invoke();
        }

        public string Error => string.Empty;

        public string this[string columnName] => columnName switch {
            nameof(SMin) when _sMin <= 0 => "SMin must be > 0.",
            nameof(SMin) when _sMin >= _sMax => "SMin must be < SMax.",
            nameof(SMax) when _sMax <= _sMin => "SMax must be > SMin.",
            _ => string.Empty
        };
    }
}
