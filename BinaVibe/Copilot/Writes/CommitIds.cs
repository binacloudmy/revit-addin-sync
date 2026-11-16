#nullable enable
// The add-in's own full id lists of what one write touched, kept in memory beside its
// envelope and never sent anywhere. The envelope's touched lists stop at 50 rows
// (EnvelopeLimits.MaxRows, the contract's cap, unchanged); the marks the pane draws in
// the model and the key that names them need every id, so 58 door tags are 58 marks,
// not 50 (result marks design, rule 4). Keyed by the envelope object, so the list
// lives exactly as long as the turn's evidence holds that envelope. Revit-free.

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed class CommitIds
    {
        private static readonly ConditionalWeakTable<Envelope, CommitIds> Table = new ConditionalWeakTable<Envelope, CommitIds>();
        private static readonly object Sync = new object();

        public IReadOnlyList<long> Added { get; private set; } = new List<long>();
        public IReadOnlyList<long> Modified { get; private set; } = new List<long>();
        public IReadOnlyList<long> Deleted { get; private set; } = new List<long>();
        /// <summary>The recorder's category name per added or modified id ("Door Tags", "class:SketchPlane"); may miss ids.</summary>
        public IReadOnlyDictionary<long, string> CategoryOf { get; private set; } = new Dictionary<long, string>();

        /// <summary>Keep the full touched lists of <paramref name="env"/> before they are cut to the envelope's cap.</summary>
        public static void Remember(Envelope env, ChangesDto? touched, IReadOnlyDictionary<long, string>? categoryOf)
        {
            if (env == null || touched == null) return;
            var added = (touched.Added ?? new List<ElementRef>()).Where(r => r != null && r.Link == null).Select(r => r.Id).Distinct().ToList();
            var modified = (touched.Modified ?? new List<FieldChangeDto>()).Where(r => r != null).Select(r => r.Id).Distinct().ToList();
            var deleted = (touched.Deleted ?? new List<ElementRef>()).Where(r => r != null && r.Link == null).Select(r => r.Id).Distinct().ToList();
            var cats = new Dictionary<long, string>();
            if (categoryOf != null)
                foreach (var id in added.Concat(modified))
                    if (categoryOf.TryGetValue(id, out var c) && !string.IsNullOrWhiteSpace(c)) cats[id] = c;
            var ids = new CommitIds { Added = added, Modified = modified, Deleted = deleted, CategoryOf = cats };
            lock (Sync)
            {
                Table.Remove(env);
                Table.Add(env, ids);
            }
        }

        /// <summary>The full lists kept for this envelope; null when none were kept (an envelope from history, a read).</summary>
        public static CommitIds? Of(Envelope? env)
        {
            if (env == null) return null;
            lock (Sync) return Table.TryGetValue(env, out var ids) ? ids : null;
        }
    }
}
