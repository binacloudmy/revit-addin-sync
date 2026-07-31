using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Pure tree logic for the @-mention picker, split out of MentionInput so it
    /// compiles in the Tests project (no WPF). Owns the collapse/filter rules:
    /// an empty query shows everything collapsed; a query prunes to matching
    /// branches and reports which paths the UI must expand to make them visible.
    /// </summary>
    public static class MentionTree
    {
        // Path separator for expansion-state keys; never appears in element names.
        private const char PathSep = '\u001f';

        public static string PathOf(string parentPath, MentionNode node) => parentPath + PathSep + node.Name;

        /// <summary>
        /// Prune to nodes whose name matches the query or that contain a match.
        /// A node kept because a DESCENDANT matches keeps only the matching
        /// branches and its path goes into <paramref name="autoExpand"/>; a node
        /// kept because its OWN name matches keeps its full subtree collapsed, so
        /// the user can still drill in. Empty query keeps everything, collapsed.
        /// </summary>
        public static List<MentionNode> Filter(List<MentionNode> nodes, string query, ISet<string> autoExpand, string parentPath = "")
        {
            var result = new List<MentionNode>();
            if (nodes == null) return result;
            if (string.IsNullOrEmpty(query)) { result.AddRange(nodes); return result; }

            foreach (var n in nodes)
            {
                string path = PathOf(parentPath, n);
                var kids = Filter(n.Children, query, autoExpand, path);
                if (kids.Count > 0)
                {
                    result.Add(new MentionNode(n.Kind, n.Name, kids, n.Pickable));
                    autoExpand?.Add(path);
                }
                else if (MentionToken.Matches(n.Name, query))
                {
                    result.Add(n);
                }
            }
            return result;
        }

        /// <summary>All pickable nodes, depth-first.</summary>
        public static IEnumerable<MentionNode> Flatten(IEnumerable<MentionNode> nodes)
        {
            if (nodes == null) yield break;
            foreach (var n in nodes)
            {
                if (n.Pickable) yield return n;
                foreach (var c in Flatten(n.Children)) yield return c;
            }
        }
    }
}
