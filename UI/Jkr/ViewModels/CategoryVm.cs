using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    public class CategoryVm : INotifyPropertyChanged
    {
        public string Label { get; set; } = "";
        public string Icon { get; set; } = "diamond";
        public bool IsAll { get; set; }

        private int _openCount;
        public int OpenCount { get => _openCount; set { _openCount = value; Raise(); Raise(nameof(IsDone)); Raise(nameof(CountDisplay)); Raise(nameof(IsVisible)); } }

        private int _totalCount;
        public int TotalCount { get => _totalCount; set { _totalCount = value; Raise(); Raise(nameof(IsDone)); Raise(nameof(IsVisible)); } }

        private bool _isActive;
        public bool IsActive { get => _isActive; set { _isActive = value; Raise(); } }

        public bool IsDone => !IsAll && TotalCount > 0 && OpenCount == 0;
        public int CountDisplay => IsAll ? OpenCount : OpenCount;
        public bool IsVisible => IsAll || TotalCount > 0;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public static readonly string[] Order = {
            "Project Naming",
            "Project Information",
            "Project Base Point",
            "Grids",
            "Levels",
            "Component Naming",
            "Component Parameter",
            "LOD 400/500 parameter",
        };

        public static readonly Dictionary<string, string> IconMap = new Dictionary<string, string>
        {
            ["Project Naming"] = "file",
            ["Project Information"] = "clipboard",
            ["Project Base Point"] = "pin",
            ["Grids"] = "grid",
            ["Levels"] = "levels",
            ["Component Naming"] = "tag",
            ["Component Parameter"] = "gear",
            ["LOD 400/500 parameter"] = "box",
        };
    }
}
