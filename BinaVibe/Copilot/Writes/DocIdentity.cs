#nullable enable
// Which document a turn is about (spec §14 "Doc switched or closed mid-turn";
// Review Focus 4). Revit-free.
//
// A write runs only against the document the turn's snapshot describes. The
// identity is built from snapshot fields the contract defines exactly
// (kind, title, path_hash = sha256 of Document.PathName) plus the per-open
// session_nonce when both sides know it, so a closed-and-reopened document
// or a different unsaved "Project1" is never mistaken for the turn's one.

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed class DocIdentity
    {
        public DocIdentity(string kind, string title, string? pathHash, string? sessionNonce = null)
        {
            Kind = kind ?? "";
            Title = title ?? "";
            PathHash = string.IsNullOrEmpty(pathHash) ? null : pathHash!.ToLowerInvariant();
            SessionNonce = string.IsNullOrEmpty(sessionNonce) ? null : sessionNonce;
        }

        /// <summary>project | family.</summary>
        public string Kind { get; }
        public string Title { get; }
        /// <summary>Lowercase hex SHA-256 of the path; null for an unsaved document.</summary>
        public string? PathHash { get; }
        /// <summary>Random per opened document; null when unknown.</summary>
        public string? SessionNonce { get; }

        /// <summary>A stable per-document key (path hash, or the title of an unsaved document).</summary>
        public string Key => PathHash != null ? Kind + ":" + PathHash : Kind + ":unsaved:" + Title;

        public static DocIdentity? FromSnapshot(SnapshotModel? snapshot)
        {
            var d = snapshot?.Doc;
            if (d == null) return null;
            return new DocIdentity(d.Kind, d.Title, d.PathHash, d.SessionNonce);
        }

        /// <summary>True when both name the same open document; two nulls ("no document") match.</summary>
        public static bool Same(DocIdentity? expected, DocIdentity? active)
        {
            if (expected == null || active == null) return expected == null && active == null;
            if (!string.Equals(expected.Kind, active.Kind, StringComparison.Ordinal)) return false;
            if (expected.PathHash != null || active.PathHash != null)
            {
                if (!string.Equals(expected.PathHash, active.PathHash, StringComparison.Ordinal)) return false;
            }
            else if (!string.Equals(expected.Title, active.Title, StringComparison.Ordinal))
            {
                return false;
            }
            if (expected.SessionNonce != null && active.SessionNonce != null
                && !string.Equals(expected.SessionNonce, active.SessionNonce, StringComparison.Ordinal))
                return false;
            return true;
        }

        /// <summary>The contract's path_hash: lowercase hex SHA-256 of the UTF-8 path; null when empty.</summary>
        public static string? HashPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(path));
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public override string ToString() => "'" + Title + "' (" + Kind + ")";
    }
}
