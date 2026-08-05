using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// One row of the @-mention tree, mirroring the Project Browser hierarchy
    /// (Families → Walls → Basic Wall → Concrete 8"). Group headers ("Views",
    /// "Families", …) are non-pickable containers; every other node inserts
    /// "@Name" when picked, including mid-tree ones like a category.
    /// </summary>
    public class MentionNode
    {
        public string Kind;      // level | category | family | type | view | sheet | selection | group
        public string Name;
        public bool Pickable = true;
        public List<MentionNode> Children = new List<MentionNode>();

        public MentionNode(string kind, string name, bool pickable = true)
        { Kind = kind; Name = name; Pickable = pickable; }

        public MentionNode(string kind, string name, IEnumerable<MentionNode> children, bool pickable)
        { Kind = kind; Name = name; Pickable = pickable; if (children != null) Children.AddRange(children); }

        public static MentionNode Leaf(string kind, string name) => new MentionNode(kind, name);
        public static MentionNode Group(string name, IEnumerable<MentionNode> children)
            => new MentionNode("group", name, children, pickable: false);
    }

    /// <summary>Supplies the @-mention picker tree. Revit impl reads the live
    /// Document; a static fallback covers the no-document case. Called once per
    /// picker open (MentionInput caches while the popup is up).</summary>
    public interface IMentionProvider
    {
        List<MentionNode> GetTree();
    }

    /// <summary>
    /// Fallback when no live document is available. Only "Categories" is a fixed enum (real,
    /// not mock); levels/views/sheets/families come from the model, so they're omitted here
    /// rather than faked. RevitMentionProvider supplies the real data when a document is open.
    /// </summary>
    public class StaticMentionProvider : IMentionProvider
    {
        public List<MentionNode> GetTree() => new List<MentionNode>
        {
            MentionNode.Group("Categories",
                new[] { "Walls", "Doors", "Windows", "Floors", "Rooms", "Furniture", "Casework" }
                    .Select(c => MentionNode.Leaf("category", c))),
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
                case "family": return ("#dbeafe", "#1e40af");
                case "type": return ("#e0f2fe", "#0369a1");
                case "view": return ("#dcfce7", "#15803d");
                case "sheet": return ("#fce7f3", "#9d174d");
                case "selection": return ("#ede9fe", "#6d28d9");
                default: return ("#eef0f3", "#374151");
            }
        }
    }
}
