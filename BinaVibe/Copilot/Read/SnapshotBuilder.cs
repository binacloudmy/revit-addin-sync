#nullable enable
// Revit-free builders for the context snapshot (spec §7.9, plan §2.4) and the
// document identity a turn is bound to. SnapshotService (Revit-bound) reads
// the live model and hands plain values to these, so the shaping rules (caps,
// hashes, mode, staleness) are unit-tested without Revit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public static class SnapshotBuilder
    {
        /// <summary>selection.by_category keeps the biggest categories; the rest add up in "(other)".</summary>
        public const int MaxSelectionCategories = 20;

        public const string NoCategory = "(no category)";

        /// <summary>Category bucket for selected ids beyond the classification cap.</summary>
        public const string NotClassified = "(not classified)";
        public const string OtherCategories = "(other)";

        /// <summary>Lowercase hex SHA-256 of the UTF-8 path; null for an unsaved document.</summary>
        public static string? PathHash(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return Sha256Hex(Encoding.UTF8.GetBytes(path));
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>The path part of doc.project_key (rulings R24, R33) for
        /// SnapshotDoc.ProjectKeyOf: the cloud model GUID or the user-visible central
        /// path for a workshared document (never the local copy's PathName), else
        /// PathName ("" when unsaved).</summary>
        public static string ProjectKeyPath(bool isWorkshared, bool isCloud, string? centralPath, string? cloudModelGuid, string? pathName)
        {
            // Workshared with no readable central path: the local PathName is the best we have.
            if (isWorkshared)
            {
                if (isCloud && !string.IsNullOrEmpty(cloudModelGuid)) return cloudModelGuid!;
                if (!string.IsNullOrEmpty(centralPath)) return centralPath!;
            }
            return pathName ?? "";
        }

        /// <summary>doc.project_key from plain values: family documents hash an empty
        /// UniqueId part (ruling R33); the hash itself is SnapshotDoc.ProjectKeyOf.</summary>
        public static string ProjectKey(bool isFamily, string? projectUniqueId, bool isWorkshared, bool isCloud,
            string? centralPath, string? cloudModelGuid, string? pathName) =>
            SnapshotDoc.ProjectKeyOf(isFamily ? "" : projectUniqueId,
                ProjectKeyPath(isWorkshared, isCloud, centralPath, cloudModelGuid, pathName));

        /// <summary>"n_" + 12 random lowercase hex digits; one per opened document.</summary>
        public static string NewSessionNonce()
        {
            var bytes = new byte[6];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder("n_", 14);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>One of <see cref="SnapshotDocModes"/>. Read-only wins, then detached,
        /// then the worksharing role.</summary>
        public static string Mode(bool isWorkshared, bool isCentral, bool isDetached, bool isReadOnly)
        {
            if (isReadOnly) return SnapshotDocModes.ReadOnly;
            if (isDetached) return SnapshotDocModes.Detached;
            if (!isWorkshared) return SnapshotDocModes.Standalone;
            return isCentral ? SnapshotDocModes.Central : SnapshotDocModes.Local;
        }

        /// <summary>Selection summary: the total, counts per category (capped at
        /// <see cref="MaxSelectionCategories"/> plus "(other)") and the first 50 ids in selection order.</summary>
        public static SnapshotSelection Selection(IEnumerable<(long Id, string? Category)> selected)
        {
            var all = selected?.ToList() ?? new List<(long Id, string? Category)>();
            var counts = all
                .GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? NoCategory : s.Category!)
                .Select(g => (Name: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Name, StringComparer.Ordinal)
                .ToList();

            var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in counts.Take(MaxSelectionCategories)) byCategory[c.Name] = c.Count;
            var rest = counts.Skip(MaxSelectionCategories).Sum(c => c.Count);
            if (rest > 0) byCategory[OtherCategories] = rest;

            return new SnapshotSelection
            {
                Count = all.Count,
                ByCategory = byCategory,
                Ids = all.Select(s => s.Id).Take(SnapshotLimits.MaxSelectionIds).ToList(),
            };
        }

        /// <summary>A selection without categories (pane closed): the count, the first 50 ids and
        /// one "(not classified)" bucket, without touching every id.</summary>
        public static SnapshotSelection UnclassifiedSelection(int count, IEnumerable<long> ids)
        {
            var sel = new SnapshotSelection
            {
                Count = count,
                Ids = (ids ?? Enumerable.Empty<long>()).Take(SnapshotLimits.MaxSelectionIds).ToList(),
            };
            if (count > 0) sel.ByCategory[NotClassified] = count;
            return sel;
        }

        /// <summary>The rooms list, or null when the model has more than
        /// <see cref="SnapshotLimits.MaxRooms"/> rooms (the model asks model_overview then).</summary>
        public static List<SnapshotRoom>? Rooms(IReadOnlyCollection<SnapshotRoom> all)
        {
            if (all == null) return new List<SnapshotRoom>();
            if (all.Count > SnapshotLimits.MaxRooms) return null;
            return all.ToList();
        }

        /// <summary>A snapshot with no document open.</summary>
        public static SnapshotModel Empty() => new SnapshotModel();

        /// <summary>Deep copy (via the wire form, so nothing can be shared by accident).</summary>
        public static SnapshotModel Clone(SnapshotModel s) =>
            CopilotJson.Deserialize<SnapshotModel>(CopilotJson.Serialize(s)) ?? new SnapshotModel();

        /// <summary>The last good snapshot, flagged stale because a refresh failed.</summary>
        public static SnapshotModel Stale(SnapshotModel? lastGood)
        {
            var copy = lastGood == null ? new SnapshotModel() : Clone(lastGood);
            copy.Stale = true;
            return copy;
        }
    }

    /// <summary>What one snapshot refresh rebuilds (ruling R56).</summary>
    public readonly struct SnapshotRefreshPlan
    {
        public SnapshotRefreshPlan(bool identity, bool docParts, bool expensive, bool warnings, bool classify)
        {
            Identity = identity;
            DocParts = docParts;
            Expensive = expensive;
            Warnings = warnings;
            Classify = classify;
        }

        /// <summary>Doc identity (project_key, version_guid, session_nonce, …), view,
        /// selection count and ids: always, whatever the pane state.</summary>
        public bool Identity { get; }

        /// <summary>Document-level cheap parts (links): when the document changed, at most every second.</summary>
        public bool DocParts { get; }

        /// <summary>Rooms and the selection's by_category (GetElement per id): only while the pane is open.</summary>
        public bool Expensive { get; }

        /// <summary>GetWarnings: only while the pane is open, at most every 5 s.</summary>
        public bool Warnings { get; }

        /// <summary>A rebuilt selection reads categories (pane open or forced). Independent of
        /// Identity, so a pane-open refresh never declassifies the selection.</summary>
        public bool Classify { get; }

        /// <summary>The selection is rebuilt only with the identity; a warnings-only refresh keeps
        /// the last one (with its by_category) for the same document.</summary>
        public bool RebuildSelection => Identity;
    }

    /// <summary>The last expensive parts and the document they were read from.</summary>
    public sealed class SnapshotHeavyParts
    {
        public string? DocKey;
        public int WarningsCount;
        public List<SnapshotRoom>? Rooms;
    }

    public static class SnapshotRefresh
    {
        public static readonly TimeSpan DocEvery = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan WarningsEvery = TimeSpan.FromSeconds(5);

        /// <summary>Identity is always kept current so DocIdentity never mismatches because
        /// the pane has not reported open; only the expensive reads wait for the pane.</summary>
        public static SnapshotRefreshPlan Plan(bool paneOpen, bool force, bool docChanged, bool docDirty, bool viewDirty,
            TimeSpan sinceDocRefresh, bool warningsDirty, TimeSpan sinceWarnings)
        {
            var docParts = force || docChanged || (docDirty && sinceDocRefresh >= DocEvery);
            var identity = force || docParts || viewDirty;
            var heavy = paneOpen || force;
            var warnings = heavy && (force || docChanged || (warningsDirty && sinceWarnings >= WarningsEvery));
            return new SnapshotRefreshPlan(identity, docParts, heavy && identity, warnings, classify: heavy);
        }

        /// <summary>The selection for this refresh: the previous one (same document) when the plan
        /// does not rebuild it, else a new one, classified when the plan says so.</summary>
        public static SnapshotSelection SelectionFor(SnapshotRefreshPlan plan, SnapshotModel? previous, bool sameDocument,
            Func<bool, SnapshotSelection> build)
        {
            if (!plan.RebuildSelection && sameDocument && previous != null)
                return SnapshotBuilder.Clone(previous).Selection;
            return build(plan.Classify);
        }

        /// <summary>The snapshot after a refresh threw: the last one, flagged stale. Its identity
        /// is kept only when it is the live document's; after a switch it is dropped (doc and
        /// view null), so the previous document is never presented as current.</summary>
        public static SnapshotModel Failed(SnapshotModel? previous, bool sameDocument)
        {
            var stale = SnapshotBuilder.Stale(previous);
            if (sameDocument) return stale;
            return new SnapshotModel { Stale = true, ChangeSeq = stale.ChangeSeq };
        }

        /// <summary>The snapshot from fresh identity parts and the last expensive parts. Expensive
        /// parts read from another document are dropped (warnings 0, rooms unknown), never reused.</summary>
        public static SnapshotModel Compose(string docKey, SnapshotDoc doc, SnapshotView? view, SnapshotSelection selection,
            string? activeWorkset, IEnumerable<SnapshotLink> links, SnapshotHeavyParts heavy, long changeSeq)
        {
            var same = heavy != null && string.Equals(heavy.DocKey, docKey, StringComparison.Ordinal);
            return new SnapshotModel
            {
                Doc = doc,
                View = view,
                Selection = selection,
                Worksharing = new SnapshotWorksharing { ActiveWorkset = activeWorkset },
                Links = links.Select(l => new SnapshotLink { Name = l.Name, Loaded = l.Loaded }).ToList(),
                WarningsCount = same ? heavy!.WarningsCount : 0,
                Rooms = same ? heavy!.Rooms?.ToList() : null,
                ChangeSeq = changeSeq,
                Stale = false,
            };
        }
    }

    /// <summary>The identity a turn is bound to: the random session nonce of the
    /// opened document. It survives Save As and edits, and changes when the
    /// drafter switches to another document or closes and reopens it.</summary>
    public static class DocIdentity
    {
        public static string Fingerprint(string sessionNonce) =>
            string.IsNullOrEmpty(sessionNonce) ? "" : "doc:" + sessionNonce;

        /// <summary>The fingerprint a snapshot was taken against; "" when it had no document.</summary>
        public static string FromSnapshot(SnapshotModel? snapshot) =>
            snapshot?.Doc == null ? "" : Fingerprint(snapshot.Doc.SessionNonce);

        /// <summary>True when the live document is the one the snapshot was taken against.</summary>
        public static bool Matches(string liveFingerprint, SnapshotModel? snapshot)
        {
            var expected = FromSnapshot(snapshot);
            return expected.Length > 0 && string.Equals(expected, liveFingerprint, StringComparison.Ordinal);
        }
    }

    /// <summary>model_overview cache key (spec §7.9). VersionGUID and the number of saves
    /// only move on save, so the DocumentChanged sequence is part of the key too:
    /// an unsaved new level must not be served from a stale overview.</summary>
    public readonly struct OverviewCacheKey : IEquatable<OverviewCacheKey>
    {
        public string VersionGuid { get; }
        public string SessionNonce { get; }
        public int NumberOfSaves { get; }
        public long ChangeSeq { get; }

        private OverviewCacheKey(string versionGuid, string sessionNonce, int numberOfSaves, long changeSeq)
        {
            VersionGuid = versionGuid ?? "";
            SessionNonce = sessionNonce ?? "";
            NumberOfSaves = numberOfSaves;
            ChangeSeq = changeSeq;
        }

        public static OverviewCacheKey Of(string versionGuid, string sessionNonce, int numberOfSaves, long changeSeq) =>
            new OverviewCacheKey(versionGuid, sessionNonce, numberOfSaves, changeSeq);

        public bool Equals(OverviewCacheKey other) =>
            string.Equals(VersionGuid, other.VersionGuid, StringComparison.Ordinal)
            && string.Equals(SessionNonce, other.SessionNonce, StringComparison.Ordinal)
            && NumberOfSaves == other.NumberOfSaves
            && ChangeSeq == other.ChangeSeq;

        public override bool Equals(object? obj) => obj is OverviewCacheKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                var h = StringComparer.Ordinal.GetHashCode(VersionGuid ?? "");
                h = h * 31 + StringComparer.Ordinal.GetHashCode(SessionNonce ?? "");
                h = h * 31 + NumberOfSaves;
                h = h * 31 + ChangeSeq.GetHashCode();
                return h;
            }
        }

        public override string ToString() => VersionGuid + "|" + SessionNonce + "|" + NumberOfSaves + "|" + ChangeSeq;
    }
}
