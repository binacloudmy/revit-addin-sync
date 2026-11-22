using System.Collections.Generic;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>AI-491: the id list behind an issue row (no WPF, so it is unit-tested on the Mac harness).
    /// <see cref="IssueVm.AddElements"/> delegates here.</summary>
    public static class IssueElementIds
    {
        /// <summary>Add positive ids not yet in <paramref name="ids"/>, in order, each with <paramref name="label"/>
        /// (an empty label adds none; a label already set for an id is kept). Returns the new id count.</summary>
        public static int Add(List<long> ids, Dictionary<long, string> labels, IEnumerable<long> add, string label)
        {
            foreach (var id in add ?? new List<long>())
            {
                if (id <= 0 || ids.Contains(id)) continue;
                ids.Add(id);
                if (!string.IsNullOrEmpty(label)) labels[id] = label;
            }
            return ids.Count;
        }
    }
}
