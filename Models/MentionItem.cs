using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Types of mentionable items in Revit
    /// </summary>
    public enum MentionType
    {
        Category,       // @Doors, @Walls, @Windows
        Level,          // @Level 1, @Ground Floor
        View,           // @Floor Plan Level 1
        Family,         // @Single-Flush
        Phase,          // @New Construction
        Workset,        // @AR_Walls
        Parameter       // @Fire Rating
    }

    /// <summary>
    /// Represents a mentionable item for autocomplete
    /// </summary>
    public class MentionItem
    {
        /// <summary>
        /// Display name shown in autocomplete (e.g., "Doors", "Level 1")
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Type of mention (Category, Level, View, etc.)
        /// </summary>
        public MentionType Type { get; set; }

        /// <summary>
        /// Icon/prefix for display (e.g., category icon)
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// Element ID if applicable (for levels, views)
        /// </summary>
        public long? ElementId { get; set; }

        /// <summary>
        /// Additional info for display (e.g., "47 elements", "Elevation: 0.0m")
        /// </summary>
        public string Info { get; set; }

        /// <summary>
        /// Category name for grouping in autocomplete
        /// </summary>
        public string Group => Type.ToString();

        /// <summary>
        /// Full display text for autocomplete item
        /// </summary>
        public string DisplayText => string.IsNullOrEmpty(Info) ? Name : $"{Name} ({Info})";
    }

    /// <summary>
    /// Resolved mention with full context for AI
    /// </summary>
    public class ResolvedMention
    {
        /// <summary>
        /// Original mention text (e.g., "@Doors")
        /// </summary>
        [JsonProperty("mention")]
        public string Mention { get; set; }

        /// <summary>
        /// Type of the mention
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>
        /// Resolved name (e.g., "Doors")
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// BuiltInCategory enum name if category (e.g., "OST_Doors")
        /// </summary>
        [JsonProperty("builtInCategory")]
        public string BuiltInCategory { get; set; }

        /// <summary>
        /// Element ID if applicable
        /// </summary>
        [JsonProperty("elementId")]
        public long? ElementId { get; set; }

        /// <summary>
        /// Count of elements (for categories)
        /// </summary>
        [JsonProperty("count")]
        public int? Count { get; set; }

        /// <summary>
        /// Elevation in meters (for levels)
        /// </summary>
        [JsonProperty("elevation")]
        public double? Elevation { get; set; }

        /// <summary>
        /// Additional properties (varies by type)
        /// </summary>
        [JsonProperty("properties")]
        public Dictionary<string, string> Properties { get; set; }

        /// <summary>
        /// Human-readable context string for the AI
        /// </summary>
        public string ToContextString()
        {
            switch (Type)
            {
                case "Category":
                    return $"{Name} (BuiltInCategory.{BuiltInCategory}, Count: {Count ?? 0})";

                case "Level":
                    return $"Level \"{Name}\" (ElementId: {ElementId}, Elevation: {Elevation:F2}m)";

                case "View":
                    var viewType = Properties?.GetValueOrDefault("ViewType", "Unknown");
                    return $"View \"{Name}\" (ElementId: {ElementId}, Type: {viewType})";

                case "Family":
                    var category = Properties?.GetValueOrDefault("Category", "");
                    return $"Family \"{Name}\" (Category: {category})";

                case "Phase":
                    return $"Phase \"{Name}\" (ElementId: {ElementId})";

                case "Workset":
                    return $"Workset \"{Name}\"";

                case "Parameter":
                    return $"Parameter \"{Name}\"";

                default:
                    return Name;
            }
        }
    }

    /// <summary>
    /// Collection of resolved mentions for a prompt
    /// </summary>
    public class MentionContext
    {
        /// <summary>
        /// List of resolved mentions
        /// </summary>
        [JsonProperty("mentions")]
        public List<ResolvedMention> Mentions { get; set; } = new List<ResolvedMention>();

        /// <summary>
        /// Original prompt text
        /// </summary>
        [JsonProperty("originalPrompt")]
        public string OriginalPrompt { get; set; }

        /// <summary>
        /// Prompt with mentions expanded to context
        /// </summary>
        [JsonProperty("expandedPrompt")]
        public string ExpandedPrompt { get; set; }

        /// <summary>
        /// Build context string for AI
        /// </summary>
        public string ToContextString()
        {
            if (Mentions == null || Mentions.Count == 0)
                return "";

            var lines = new List<string> { "Referenced Elements:" };
            foreach (var mention in Mentions)
            {
                lines.Add($"  - {mention.ToContextString()}");
            }
            return string.Join("\n", lines);
        }
    }
}
