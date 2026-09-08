using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim.ViewModels;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>Editable copy of CadToBimSettings for the settings window. Nothing reaches the
    /// real settings until Validate() returns null and WriteTo() runs (OK), so Cancel is free.</summary>
    public sealed class CadToBimSettingsDraft : ViewModelBase
    {
        private double _sMin, _sMax, _height, _doorMin, _doorMax, _sill;
        private string _wallHints = "", _openingHints = "", _excludeGlobs = "", _templatePath = "";

        public double SMin { get => _sMin; set => SetField(ref _sMin, value); }
        public double SMax { get => _sMax; set => SetField(ref _sMax, value); }
        public double Height { get => _height; set => SetField(ref _height, value); }
        public double DoorMin { get => _doorMin; set => SetField(ref _doorMin, value); }
        public double DoorMax { get => _doorMax; set => SetField(ref _doorMax, value); }
        public double Sill { get => _sill; set => SetField(ref _sill, value); }
        public string WallHints { get => _wallHints; set => SetField(ref _wallHints, value ?? ""); }
        public string OpeningHints { get => _openingHints; set => SetField(ref _openingHints, value ?? ""); }
        public string ExcludeGlobs { get => _excludeGlobs; set => SetField(ref _excludeGlobs, value ?? ""); }
        public string TemplatePath { get => _templatePath; set => SetField(ref _templatePath, value ?? ""); }

        public static CadToBimSettingsDraft From(CadToBimSettings s) => new CadToBimSettingsDraft
        {
            SMin = s.SMinMm,
            SMax = s.SMaxMm,
            Height = s.WallHeightMm,
            DoorMin = s.DoorMinRadiusMm,
            DoorMax = s.DoorMaxRadiusMm,
            Sill = s.WindowSillMm,
            WallHints = string.Join(", ", s.WallLayerHints ?? new List<string>()),
            OpeningHints = string.Join(", ", s.OpeningLayerHints ?? new List<string>()),
            ExcludeGlobs = string.Join(", ", s.ExcludeGlobs ?? new List<string>()),
            TemplatePath = s.TemplatePath ?? "",
        };

        /// <summary>Null when everything is usable; otherwise the message to show.</summary>
        public string Validate()
        {
            if (_sMin <= 0) return "Minimum thickness must be above 0 mm.";
            if (_sMin >= _sMax) return "Minimum thickness must be smaller than the maximum.";
            if (_height <= 0) return "Wall height must be above 0 mm.";
            if (_doorMin <= 0) return "Door swing minimum radius must be above 0 mm.";
            if (_doorMin >= _doorMax) return "Door swing minimum radius must be smaller than the maximum.";
            if (_sill < 0) return "Window sill cannot be negative.";
            if (SplitList(_wallHints).Count == 0) return "Give at least one wall layer hint (e.g. wall, dinding).";
            string template = _templatePath.Trim();
            if (template.Length > 0 && !System.IO.File.Exists(template)) return "Template file not found: " + template;
            return null;
        }

        public void WriteTo(CadToBimSettings s)
        {
            s.SMinMm = _sMin;
            s.SMaxMm = _sMax;
            s.WallHeightMm = _height;
            s.DoorMinRadiusMm = _doorMin;
            s.DoorMaxRadiusMm = _doorMax;
            s.WindowSillMm = _sill;
            s.WallLayerHints = SplitList(_wallHints);
            s.OpeningLayerHints = SplitList(_openingHints);
            s.ExcludeGlobs = SplitList(_excludeGlobs);
            string template = _templatePath.Trim();
            s.TemplatePath = template.Length == 0 ? null : template;
        }

        public static List<string> SplitList(string csv) =>
            (csv ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(p => p.Trim())
                       .Where(p => p.Length > 0)
                       .ToList();
    }
}
