#nullable enable
// The live document's identity (spec §14; Review Focus 4): the Revit half of
// DocIdentity. kind / title / path_hash are computed exactly as the snapshot
// contract defines them; the session nonce comes from W2's SnapshotService
// when its current snapshot describes this same document.

using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;

namespace BinaVibe.Copilot.Writes
{
    public static class RevitDocIdentity
    {
        public static DocIdentity Of(Document doc)
        {
            var kind = doc.IsFamilyDocument ? SnapshotDocKinds.Family : SnapshotDocKinds.Project;
            var title = doc.Title ?? "";
            var pathHash = DocIdentity.HashPath(doc.PathName);
            return new DocIdentity(kind, title, pathHash, NonceFor(kind, title, pathHash));
        }

        private static string? NonceFor(string kind, string title, string? pathHash)
        {
            try
            {
                var d = SnapshotService.Current()?.Doc;
                if (d == null || string.IsNullOrEmpty(d.SessionNonce)) return null;
                var snap = new DocIdentity(d.Kind, d.Title, d.PathHash);
                return DocIdentity.Same(snap, new DocIdentity(kind, title, pathHash)) ? d.SessionNonce : null;
            }
            catch (Exception)
            {
                // SnapshotService not attached (or still a stub): identity falls back to kind/title/path.
                return null;
            }
        }
    }

    /// <summary>The fingerprint provider the add-in's runner uses.</summary>
    public sealed class RevitDocIdentityProvider : IDocIdentityProvider
    {
        public Task<DocIdentity?> ActiveAsync(CancellationToken ct) =>
            RevitThread.RunAsync<DocIdentity?>(app =>
            {
                var doc = app.ActiveUIDocument?.Document;
                return doc == null ? null : RevitDocIdentity.Of(doc);
            }, ct);
    }
}
