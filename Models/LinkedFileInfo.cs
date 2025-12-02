using System.ComponentModel;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents information about a linked Revit file in the current document
    /// Used for UI binding in the clash detection dialog
    /// </summary>
    public class RevitLinkedFileInfo : INotifyPropertyChanged
    {
        private bool _isSelected;

        /// <summary>
        /// The RevitLinkInstance element
        /// </summary>
        public RevitLinkInstance LinkInstance { get; set; }

        /// <summary>
        /// The linked document (may be null if not loaded)
        /// </summary>
        public Document LinkedDocument { get; set; }

        /// <summary>
        /// Element ID of the link instance
        /// </summary>
        public ElementId LinkInstanceId { get; set; }

        /// <summary>
        /// Name of the linked file
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Full path of the linked file
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Display name for the UI (typically file name without path)
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Indicates if the linked file is currently loaded
        /// </summary>
        public bool IsLoaded { get; set; }

        /// <summary>
        /// Indicates if this link is selected for clash detection
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        /// <summary>
        /// Transform from linked document coordinates to host document coordinates
        /// </summary>
        public Transform LinkTransform { get; set; }

        /// <summary>
        /// Number of elements in the linked document (if loaded)
        /// </summary>
        public int ElementCount { get; set; }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
