namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Where the Project Browser lists a view, by its Revit ViewType name (kept as text so the rule is unit-tested without the Revit API).
    /// The browser shows plans, 3D views, elevations, sections, details, drafting views, area plans, walkthroughs and renderings under
    /// Views; Legends and Schedules/Quantities as their own branches. It never shows the Project Browser or System Browser pseudo-views,
    /// internal or undefined views, sheets (their own branch) or report views, so the @ picker must not offer them either.
    /// </summary>
    public static class BrowserViews
    {
        public const string ViewsBranch = "Views", LegendsBranch = "Legends", SchedulesBranch = "Schedules/Quantities";

        /// <summary>The branch and the group (null for a branch listed flat) the Project Browser puts a view of this type in, with the
        /// group's order in the browser; null when the browser does not list it.</summary>
        public static (string Branch, string Group, int Rank)? Place(string viewType)
        {
            switch (viewType)
            {
                case "FloorPlan": return (ViewsBranch, "Floor Plans", 1);
                case "CeilingPlan": return (ViewsBranch, "Ceiling Plans", 2);
                case "EngineeringPlan": return (ViewsBranch, "Structural Plans", 3);
                case "AreaPlan": return (ViewsBranch, "Area Plans", 4);
                case "ThreeD": return (ViewsBranch, "3D Views", 5);
                case "Elevation": return (ViewsBranch, "Elevations", 6);
                case "Section": return (ViewsBranch, "Sections", 7);
                case "Detail": return (ViewsBranch, "Detail Views", 8);
                case "DraftingView": return (ViewsBranch, "Drafting Views", 9);
                case "Walkthrough": return (ViewsBranch, "Walkthroughs", 10);
                case "Rendering": return (ViewsBranch, "Renderings", 11);
                case "Legend": return (LegendsBranch, null, 0);
                case "Schedule":
                case "ColumnSchedule":
                case "PanelSchedule": return (SchedulesBranch, null, 0);
                default: return null;   // ProjectBrowser, SystemBrowser, Internal, Undefined, DrawingSheet, Report views
            }
        }
    }
}
