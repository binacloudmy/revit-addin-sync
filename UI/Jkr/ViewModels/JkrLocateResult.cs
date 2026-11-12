namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>What "Locate in 3D" managed to do. Revit-free so the messages
    /// are unit-tested; the Revit side is the panel's LocateInRevitAsync.</summary>
    public enum JkrLocateKind
    {
        /// <summary>Shown and selected.</summary>
        Shown,
        /// <summary>The issue points at a type: its placed instances were shown.</summary>
        ShownTypeInstances,
        /// <summary>The issue points at a type with no placed instances.</summary>
        TypeNotPlaced,
        /// <summary>Selected, but hidden in the view Revit chose (hidden element or category).</summary>
        HiddenInView,
        /// <summary>Revit could not show it (no view shows it); it is still selected.</summary>
        NotShown,
        /// <summary>No element with that id in the model.</summary>
        Missing,
        NoDocument,
        NoElement,
    }

    public sealed class JkrLocateResult
    {
        public JkrLocateKind Kind { get; }
        /// <summary>Instances shown, for <see cref="JkrLocateKind.ShownTypeInstances"/>.</summary>
        public int Count { get; }
        /// <summary>Type name, view name or Revit's message, depending on <see cref="Kind"/>.</summary>
        public string Detail { get; }

        public JkrLocateResult(JkrLocateKind kind, int count = 0, string detail = "")
        {
            Kind = kind;
            Count = count;
            Detail = detail ?? "";
        }

        /// <summary>Toast text; null when nothing needs saying (plain success).</summary>
        public string Message
        {
            get
            {
                switch (Kind)
                {
                    case JkrLocateKind.Shown:
                        return null;
                    case JkrLocateKind.ShownTypeInstances:
                        return $"This issue is on a type. Selected its {Count} placed instance{(Count == 1 ? "" : "s")}.";
                    case JkrLocateKind.TypeNotPlaced:
                        return $"This issue is on the type '{Detail}', which has no placed instances to show.";
                    case JkrLocateKind.HiddenInView:
                        return $"Selected, but it is hidden in the view '{Detail}' (hidden element or category).";
                    case JkrLocateKind.NotShown:
                        return string.IsNullOrEmpty(Detail)
                            ? "Selected, but Revit could not find a view that shows it."
                            : $"Selected, but Revit could not show it: {Detail}";
                    case JkrLocateKind.Missing:
                        return "Element no longer exists in the model — try re-scanning.";
                    case JkrLocateKind.NoDocument:
                        return "No active Revit document — open a model and try again.";
                    default:
                        return "This issue has no linked element to locate.";
                }
            }
        }
    }
}
