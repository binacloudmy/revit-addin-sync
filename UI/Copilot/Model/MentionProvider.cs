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

        // Facts the flat search shows and ranks on. All optional: a node from
        // an older provider (or the static fallback) leaves them unset and the
        // search falls back to the tree's ancestry for family and category.
        public string Family;     // type: its family ("Pintu Tunggal")
        public string Category;   // type / family: its category ("Doors")
        public string CategoryKey;// category / type / family: BuiltInCategory name ("OST_Doors")
        public string TypeMark;   // type: the Type Mark, when the provider read it
        public int? Count;        // type: instances in the model; null = not counted
        public int? InView;       // type: instances in the active view; null = not counted
        public bool IsActive;     // view: the active view
        public MentionSelection Selection;   // selection: what is selected, by type

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

    /// <summary>A provider that can read the live selection again at send time,
    /// so a "Selection" chip sends what is selected then, not when it was picked.
    /// Separate from IMentionProvider so fakes and the static fallback need not
    /// implement it (net48: no default interface members).</summary>
    public interface IMentionSelectionSource
    {
        /// <summary>The selection now, or null when it cannot be read.</summary>
        MentionSelection ReadSelection();
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
}
