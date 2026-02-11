using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service for handling @mentions in prompts
    /// Provides autocomplete suggestions and resolves mentions to Revit context
    /// </summary>
    public class MentionService
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;

        // Cache for mentionable items (refreshed when GetMentionableItems is called)
        private List<MentionItem> _cachedItems;
        private DateTime _cacheTime;
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(30);

        // Mapping of common category names to BuiltInCategory
        private static readonly Dictionary<string, BuiltInCategory> CategoryMap = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            { "Walls", BuiltInCategory.OST_Walls },
            { "Wall", BuiltInCategory.OST_Walls },
            { "Doors", BuiltInCategory.OST_Doors },
            { "Door", BuiltInCategory.OST_Doors },
            { "Windows", BuiltInCategory.OST_Windows },
            { "Window", BuiltInCategory.OST_Windows },
            { "Floors", BuiltInCategory.OST_Floors },
            { "Floor", BuiltInCategory.OST_Floors },
            { "Roofs", BuiltInCategory.OST_Roofs },
            { "Roof", BuiltInCategory.OST_Roofs },
            { "Ceilings", BuiltInCategory.OST_Ceilings },
            { "Ceiling", BuiltInCategory.OST_Ceilings },
            { "Rooms", BuiltInCategory.OST_Rooms },
            { "Room", BuiltInCategory.OST_Rooms },
            { "Furniture", BuiltInCategory.OST_Furniture },
            { "Columns", BuiltInCategory.OST_Columns },
            { "Column", BuiltInCategory.OST_Columns },
            { "Structural Columns", BuiltInCategory.OST_StructuralColumns },
            { "Structural Framing", BuiltInCategory.OST_StructuralFraming },
            { "Beams", BuiltInCategory.OST_StructuralFraming },
            { "Beam", BuiltInCategory.OST_StructuralFraming },
            { "Stairs", BuiltInCategory.OST_Stairs },
            { "Stair", BuiltInCategory.OST_Stairs },
            { "Railings", BuiltInCategory.OST_Railings },
            { "Railing", BuiltInCategory.OST_Railings },
            { "Casework", BuiltInCategory.OST_Casework },
            { "Generic Models", BuiltInCategory.OST_GenericModel },
            { "Generic Model", BuiltInCategory.OST_GenericModel },
            { "Parking", BuiltInCategory.OST_Parking },
            { "Plumbing Fixtures", BuiltInCategory.OST_PlumbingFixtures },
            { "Mechanical Equipment", BuiltInCategory.OST_MechanicalEquipment },
            { "Electrical Equipment", BuiltInCategory.OST_ElectricalEquipment },
            { "Electrical Fixtures", BuiltInCategory.OST_ElectricalFixtures },
            { "Lighting Fixtures", BuiltInCategory.OST_LightingFixtures },
            { "Pipes", BuiltInCategory.OST_PipeCurves },
            { "Ducts", BuiltInCategory.OST_DuctCurves },
            { "Cable Trays", BuiltInCategory.OST_CableTray },
            { "Conduits", BuiltInCategory.OST_Conduit },
            { "Areas", BuiltInCategory.OST_Areas },
            { "Area", BuiltInCategory.OST_Areas },
            { "Curtain Walls", BuiltInCategory.OST_CurtainWallPanels },
            { "Curtain Panels", BuiltInCategory.OST_CurtainWallPanels },
            { "Mullions", BuiltInCategory.OST_CurtainWallMullions },
            { "Grids", BuiltInCategory.OST_Grids },
            { "Grid", BuiltInCategory.OST_Grids },
            { "Levels", BuiltInCategory.OST_Levels },
            { "Tags", BuiltInCategory.OST_Tags },
            { "Door Tags", BuiltInCategory.OST_DoorTags },
            { "Room Tags", BuiltInCategory.OST_RoomTags },
            { "Wall Tags", BuiltInCategory.OST_WallTags },
            { "Window Tags", BuiltInCategory.OST_WindowTags }
        };

        public MentionService(Document doc, UIDocument uidoc)
        {
            _doc = doc;
            _uidoc = uidoc;
        }

        /// <summary>
        /// Get all mentionable items for autocomplete
        /// </summary>
        public List<MentionItem> GetMentionableItems(bool forceRefresh = false)
        {
            // Return cached items if still valid
            if (!forceRefresh && _cachedItems != null && DateTime.Now - _cacheTime < CacheExpiry)
            {
                return _cachedItems;
            }

            var items = new List<MentionItem>();

            // Add categories with element counts
            items.AddRange(GetCategoryItems());

            // Add levels
            items.AddRange(GetLevelItems());

            // Add views
            items.AddRange(GetViewItems());

            // Add families (limited to avoid too many)
            items.AddRange(GetFamilyItems());

            // Add phases
            items.AddRange(GetPhaseItems());

            // Add worksets (if workshared)
            items.AddRange(GetWorksetItems());

            // Cache the items
            _cachedItems = items;
            _cacheTime = DateTime.Now;

            return items;
        }

        /// <summary>
        /// Filter mentionable items based on search text
        /// </summary>
        public List<MentionItem> FilterItems(string searchText)
        {
            var items = GetMentionableItems();

            if (string.IsNullOrWhiteSpace(searchText))
            {
                // Return top items from each category
                return items
                    .GroupBy(i => i.Type)
                    .SelectMany(g => g.Take(5))
                    .ToList();
            }

            // Filter by name containing search text
            return items
                .Where(i => i.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(i => i.Name.StartsWith(searchText, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.Name)
                .Take(10)
                .ToList();
        }

        /// <summary>
        /// Parse prompt for @mentions and resolve them
        /// </summary>
        public MentionContext ResolveMentions(string prompt)
        {
            var context = new MentionContext
            {
                OriginalPrompt = prompt,
                Mentions = new List<ResolvedMention>()
            };

            // Find all @mentions in the prompt
            // Pattern: @ followed by word characters, spaces (for multi-word), or quoted strings
            var pattern = @"@(?:""([^""]+)""|(\w+(?:\s+\w+)*))";
            var matches = Regex.Matches(prompt, pattern);

            var expandedPrompt = prompt;

            foreach (Match match in matches)
            {
                var mentionText = match.Value;
                var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

                var resolved = ResolveByName(name.Trim());
                if (resolved != null)
                {
                    resolved.Mention = mentionText;
                    context.Mentions.Add(resolved);

                    // Replace mention with expanded context in prompt
                    expandedPrompt = expandedPrompt.Replace(mentionText, resolved.ToContextString());
                }
            }

            context.ExpandedPrompt = expandedPrompt;
            return context;
        }

        /// <summary>
        /// Resolve a single mention by name
        /// </summary>
        private ResolvedMention ResolveByName(string name)
        {
            // Try to match as category first
            if (CategoryMap.TryGetValue(name, out var builtInCategory))
            {
                return ResolveCategoryMention(name, builtInCategory);
            }

            // Try to find a level
            var level = FindLevel(name);
            if (level != null)
            {
                return ResolveLevelMention(level);
            }

            // Try to find a view
            var view = FindView(name);
            if (view != null)
            {
                return ResolveViewMention(view);
            }

            // Try to find a family
            var family = FindFamily(name);
            if (family != null)
            {
                return ResolveFamilyMention(family);
            }

            // Try to find a phase
            var phase = FindPhase(name);
            if (phase != null)
            {
                return ResolvePhaseMention(phase);
            }

            // Could not resolve - return generic
            return new ResolvedMention
            {
                Type = "Unknown",
                Name = name,
                Properties = new Dictionary<string, string>()
            };
        }

        #region Category Items

        private List<MentionItem> GetCategoryItems()
        {
            var items = new List<MentionItem>();

            foreach (var kvp in CategoryMap)
            {
                // Skip duplicates (singular/plural)
                if (kvp.Key.EndsWith("s") && CategoryMap.ContainsKey(kvp.Key.TrimEnd('s')))
                    continue;

                try
                {
                    var count = new FilteredElementCollector(_doc)
                        .OfCategory(kvp.Value)
                        .WhereElementIsNotElementType()
                        .GetElementCount();

                    if (count > 0)
                    {
                        items.Add(new MentionItem
                        {
                            Name = kvp.Key,
                            Type = MentionType.Category,
                            Icon = "📦",
                            Info = $"{count} elements"
                        });
                    }
                }
                catch { }
            }

            return items.OrderByDescending(i => int.Parse(i.Info.Split(' ')[0])).ToList();
        }

        private ResolvedMention ResolveCategoryMention(string name, BuiltInCategory category)
        {
            var count = 0;
            try
            {
                count = new FilteredElementCollector(_doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
            }
            catch { }

            return new ResolvedMention
            {
                Type = "Category",
                Name = name,
                BuiltInCategory = category.ToString(),
                Count = count,
                Properties = new Dictionary<string, string>()
            };
        }

        #endregion

        #region Level Items

        private List<MentionItem> GetLevelItems()
        {
            var items = new List<MentionItem>();

            try
            {
                var levels = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation);

                foreach (var level in levels)
                {
                    var elevationM = level.Elevation * 0.3048; // feet to meters
                    items.Add(new MentionItem
                    {
                        Name = level.Name,
                        Type = MentionType.Level,
                        Icon = "📏",
                        ElementId = level.Id.Value,
                        Info = $"Elev: {elevationM:F2}m"
                    });
                }
            }
            catch { }

            return items;
        }

        private Level FindLevel(string name)
        {
            try
            {
                return new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || l.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return null; }
        }

        private ResolvedMention ResolveLevelMention(Level level)
        {
            var elevationM = level.Elevation * 0.3048;
            return new ResolvedMention
            {
                Type = "Level",
                Name = level.Name,
                ElementId = level.Id.Value,
                Elevation = elevationM,
                Properties = new Dictionary<string, string>
                {
                    { "Elevation", $"{elevationM:F2}m" }
                }
            };
        }

        #endregion

        #region View Items

        private List<MentionItem> GetViewItems()
        {
            var items = new List<MentionItem>();

            try
            {
                var views = new FilteredElementCollector(_doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate && v.ViewType != ViewType.DrawingSheet)
                    .Take(30); // Limit to avoid too many

                foreach (var view in views)
                {
                    items.Add(new MentionItem
                    {
                        Name = view.Name,
                        Type = MentionType.View,
                        Icon = GetViewIcon(view.ViewType),
                        ElementId = view.Id.Value,
                        Info = view.ViewType.ToString()
                    });
                }
            }
            catch { }

            return items;
        }

        private string GetViewIcon(ViewType viewType)
        {
            switch (viewType)
            {
                case ViewType.FloorPlan: return "🗺️";
                case ViewType.CeilingPlan: return "⬆️";
                case ViewType.Elevation: return "🏢";
                case ViewType.Section: return "✂️";
                case ViewType.ThreeD: return "🎲";
                case ViewType.Schedule: return "📊";
                default: return "👁️";
            }
        }

        private View FindView(string name)
        {
            try
            {
                return new FilteredElementCollector(_doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || v.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return null; }
        }

        private ResolvedMention ResolveViewMention(View view)
        {
            return new ResolvedMention
            {
                Type = "View",
                Name = view.Name,
                ElementId = view.Id.Value,
                Properties = new Dictionary<string, string>
                {
                    { "ViewType", view.ViewType.ToString() }
                }
            };
        }

        #endregion

        #region Family Items

        private List<MentionItem> GetFamilyItems()
        {
            var items = new List<MentionItem>();

            try
            {
                // Get commonly used families (doors, windows, furniture)
                var categories = new[] {
                    BuiltInCategory.OST_Doors,
                    BuiltInCategory.OST_Windows,
                    BuiltInCategory.OST_Furniture
                };

                foreach (var category in categories)
                {
                    var families = new FilteredElementCollector(_doc)
                        .OfCategory(category)
                        .WhereElementIsElementType()
                        .Cast<FamilySymbol>()
                        .GroupBy(f => f.Family.Name)
                        .Take(10);

                    foreach (var familyGroup in families)
                    {
                        items.Add(new MentionItem
                        {
                            Name = familyGroup.Key,
                            Type = MentionType.Family,
                            Icon = "📁",
                            Info = $"{familyGroup.Count()} types"
                        });
                    }
                }
            }
            catch { }

            return items;
        }

        private FamilySymbol FindFamily(string name)
        {
            try
            {
                return new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(f => f.Family.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private ResolvedMention ResolveFamilyMention(FamilySymbol family)
        {
            return new ResolvedMention
            {
                Type = "Family",
                Name = family.Family.Name,
                ElementId = family.Id.Value,
                Properties = new Dictionary<string, string>
                {
                    { "Category", family.Category?.Name ?? "" },
                    { "TypeName", family.Name }
                }
            };
        }

        #endregion

        #region Phase Items

        private List<MentionItem> GetPhaseItems()
        {
            var items = new List<MentionItem>();

            try
            {
                var phases = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Phase))
                    .Cast<Phase>();

                foreach (var phase in phases)
                {
                    items.Add(new MentionItem
                    {
                        Name = phase.Name,
                        Type = MentionType.Phase,
                        Icon = "🕐",
                        ElementId = phase.Id.Value
                    });
                }
            }
            catch { }

            return items;
        }

        private Phase FindPhase(string name)
        {
            try
            {
                return new FilteredElementCollector(_doc)
                    .OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private ResolvedMention ResolvePhaseMention(Phase phase)
        {
            return new ResolvedMention
            {
                Type = "Phase",
                Name = phase.Name,
                ElementId = phase.Id.Value,
                Properties = new Dictionary<string, string>()
            };
        }

        #endregion

        #region Workset Items

        private List<MentionItem> GetWorksetItems()
        {
            var items = new List<MentionItem>();

            try
            {
                if (!_doc.IsWorkshared) return items;

                var worksets = new FilteredWorksetCollector(_doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets();

                foreach (var workset in worksets)
                {
                    items.Add(new MentionItem
                    {
                        Name = workset.Name,
                        Type = MentionType.Workset,
                        Icon = "📂"
                    });
                }
            }
            catch { }

            return items;
        }

        #endregion
    }
}
