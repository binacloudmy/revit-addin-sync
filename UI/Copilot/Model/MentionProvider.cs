using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public class MentionGroup
    {
        public string Id;       // level | category | view | selection
        public string Label;    // "Levels", "Categories", …
        public List<string> Items = new List<string>();
        public MentionGroup(string id, string label, IEnumerable<string> items)
        { Id = id; Label = label; if (items != null) Items.AddRange(items); }
    }

    /// <summary>Supplies @-mention picker groups. Revit impl reads the live Document;
    /// a static fallback covers the no-document case.</summary>
    public interface IMentionProvider
    {
        List<MentionGroup> GetGroups();
    }

    /// <summary>
    /// Fallback when no live document is available. Only "Categories" is a fixed enum (real,
    /// not mock); levels/views/selection come from the model, so they're omitted here rather
    /// than faked. RevitMentionProvider supplies the real data when a document is open.
    /// </summary>
    public class StaticMentionProvider : IMentionProvider
    {
        public List<MentionGroup> GetGroups() => new List<MentionGroup>
        {
            new MentionGroup("category", "Categories", new[] { "Walls", "Doors", "Windows", "Floors", "Rooms", "Furniture", "Casework" }),
        };
    }

    // Pill colors per mention kind (chat.jsx MENTION_PILL_STYLE).
    public static class MentionStyle
    {
        public static (string bg, string fg) For(string kind)
        {
            switch (kind)
            {
                case "level": return ("#fef3c7", "#92400e");
                case "category": return ("#dbeafe", "#1e40af");
                case "view": return ("#dcfce7", "#15803d");
                case "room": return ("#cffafe", "#0e7490");
                case "selection": return ("#ede9fe", "#6d28d9");
                default: return ("#eef0f3", "#374151");
            }
        }
    }
}
