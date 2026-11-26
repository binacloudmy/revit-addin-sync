using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// One-slot cache for the document part of the @ picker tree. The provider builds the tree on every "@" on Revit's thread; when
    /// the stamp (document session, model event count, active view, open worksets) is the same as last time, nothing the tree shows
    /// can have changed, so a copy of the last build is returned instead of walking the model again.
    ///
    /// A null stamp never hits and never stores (the provider cannot vouch for the model: events not hooked, a temporary view mode).
    /// A build the provider marks not storable, or one that throws, is not kept. Every caller gets its own deep copy, so a caller that
    /// edits its nodes cannot change what the next one sees. Pure, so it is unit-tested without Revit.
    /// </summary>
    public sealed class MentionTreeCache
    {
        private string _stamp;
        private List<MentionNode> _tree;

        /// <summary>The tree for this stamp: a copy of the kept build when the stamp matches, else a new build.</summary>
        public List<MentionNode> Get(string stamp, Func<(List<MentionNode> Tree, bool Storable)> build)
        {
            if (stamp != null && stamp == _stamp && _tree != null) return Clone(_tree);
            var (tree, storable) = build();
            if (stamp != null && storable && tree != null)
            {
                _stamp = stamp;
                _tree = Clone(tree);
            }
            return tree;
        }

        /// <summary>A deep copy: every field, children copied recursively.</summary>
        public static List<MentionNode> Clone(IEnumerable<MentionNode> nodes) =>
            (nodes ?? Enumerable.Empty<MentionNode>()).Select(Clone).ToList();

        private static MentionNode Clone(MentionNode n)
        {
            if (n == null) return null;
            return new MentionNode(n.Kind, n.Name, Clone(n.Children), n.Pickable)
            {
                Family = n.Family, Category = n.Category, CategoryKey = n.CategoryKey, TypeMark = n.TypeMark,
                Count = n.Count, InView = n.InView, IsActive = n.IsActive, Selection = n.Selection,
                ClassName = n.ClassName, ElementId = n.ElementId,
            };
        }
    }
}
