// Schedules: name lookup for the copilot's inspect(schedule) read.
using System;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class Schedules
    {
        /// <summary>Exact name first, then partial/case-insensitive, so
        /// "Door Schedule" wins over "Door Schedule - Level 2" when both
        /// exist, while "door" still finds either.</summary>
        internal static ViewSchedule? Resolve(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate).ToList();
            return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(s => s.Name != null &&
                       s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
