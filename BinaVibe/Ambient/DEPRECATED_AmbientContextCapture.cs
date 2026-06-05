// BinaVibe.Ambient — capture ~1000-token live model snapshot per turn.
//
// Per PRD §7.1 + FR-AMB-01..03. Shipped on every message; the backend
// trusts this as ground truth. The shape mirrors the Python
// ``AmbientContext`` Pydantic model.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;

namespace BinaVibe.Ambient
{
    public sealed class SelectedElement
    {
        public long Id { get; init; }
        public string? Category { get; init; }
        public string? TypeName { get; init; }
    }

    public sealed class AmbientContext
    {
        public string? ProjectName { get; init; }
        public string? RevitVersion { get; init; }
        public string? ActiveViewName { get; init; }
        public string? ActiveViewType { get; init; }
        public int? ActiveViewScale { get; init; }
        public string? ActiveLevelName { get; init; }
        public string? Units { get; init; }
        public List<string> OpenViewNames { get; init; } = new();
        public string? ActiveWorkset { get; init; }
        public string? UserRole { get; init; }
        public List<SelectedElement> Selection { get; init; } = new();
        public Dictionary<string, string> Extras { get; init; } = new();
    }

    public static class AmbientContextCapture
    {
        public static AmbientContext Capture(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;

            var selection = new List<SelectedElement>();
            if (uidoc != null && doc != null)
            {
                foreach (var id in uidoc.Selection.GetElementIds())
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;
                    selection.Add(new SelectedElement
                    {
                        Id = id.Value,
                        Category = el.Category?.Name,
                        TypeName = doc.GetElement(el.GetTypeId())?.Name,
                    });
                }
            }

            return new AmbientContext
            {
                ProjectName = doc?.Title,
                RevitVersion = uiApp.Application?.VersionNumber,
                ActiveViewName = view?.Name,
                ActiveViewType = view?.ViewType.ToString(),
                ActiveViewScale = view?.Scale,
                Units = doc?.GetUnits()?.GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId,
                Selection = selection,
                OpenViewNames = uidoc?.GetOpenUIViews()
                    ?.Select(v => doc?.GetElement(v.ViewId)?.Name ?? "")
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList() ?? new(),
            };
        }
    }
}
