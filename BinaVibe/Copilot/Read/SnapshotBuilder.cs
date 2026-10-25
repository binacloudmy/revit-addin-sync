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
