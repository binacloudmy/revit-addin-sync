using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Builds the picker's nested folders from the Project Browser's own organization: each view or sheet comes with the folder names Revit
    /// gives it (outermost first). Folders come first, in the browser's order (view-type folders by rank, then by name, "???" last); items without folders sit at the level they belong to, sorted by
    /// name, as the browser shows them. Pure, so it is unit-tested without Revit.
    /// </summary>
    public static class BrowserTree
    {
        private static readonly StringComparer ByName = StringComparer.CurrentCultureIgnoreCase;

        // View-type folders (Floor Plans, Ceiling Plans, 3D Views ...) come first in the browser's own order, then folders by name,
        // and Revit's "???" folder (a view with no value for the grouping parameter) comes last.
        private static int Band(string folder) => folder.Trim() == "???" ? 2 : BrowserViews.GroupRank(folder) > 0 ? 0 : 1;

        public static List<MentionNode> Nest(IEnumerable<(IList<string> Path, MentionNode Leaf)> rows)
        {
            var all = (rows ?? Enumerable.Empty<(IList<string>, MentionNode)>()).ToList();
            var tree = new List<MentionNode>();
            foreach (var g in all.Where(r => r.Path != null && r.Path.Count > 0).GroupBy(r => r.Path[0] ?? "").OrderBy(g => Band(g.Key)).ThenBy(g => BrowserViews.GroupRank(g.Key)).ThenBy(g => g.Key, ByName))
                tree.Add(MentionNode.Group(g.Key, Nest(g.Select(r => ((IList<string>)r.Path.Skip(1).ToList(), r.Leaf)))));
            tree.AddRange(all.Where(r => r.Path == null || r.Path.Count == 0).Select(r => r.Leaf).OrderBy(n => n.Name, ByName));
            return tree;
        }
    }
}
