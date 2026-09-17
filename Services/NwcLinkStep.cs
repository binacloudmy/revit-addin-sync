using System;
using System.IO;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Uploads an already-exported .nwc and links it to the version that was just
    /// committed (ClickUp 86d49v9ak): presign → PUT → `sync/link`.
    ///
    /// Its whole contract is that it cannot fail a sync. The rvt version is
    /// already on the server by the time this runs, and an NWC that did not make
    /// it is a missing companion file, not a lost model — so every failure comes
    /// back as <see cref="Outcome.Linked"/> false plus something a drafter can
    /// read, and nothing here throws. Touches no Revit API.
    /// </summary>
    public static class NwcLinkStep
    {
        public sealed class Outcome
        {
            public bool Linked { get; set; }

            /// <summary>
            /// One line for the sync dialog. Set on success as well as failure —
            /// the outcome view reports the NWC separately from the version, so
            /// there is always something to say about it.
            /// </summary>
            public string Message { get; set; }

            /// <summary>Server-side link id, when one was created.</summary>
            public int? LinkId { get; set; }
        }

        public static async Task<Outcome> RunAsync(
            SyncApiClient api,
            int projectId,
            int designId,
            string disciplineType,
            string nwcPath,
            string notes = null)
        {
            if (api == null) return Failed("The NWC could not be linked: no connection to BINA.");

            if (string.IsNullOrEmpty(nwcPath) || !File.Exists(nwcPath))
                return Failed("The NWC was not found on disk, so nothing was linked.");

            try
            {
                long size = new FileInfo(nwcPath).Length;
                string fileName = Path.GetFileName(nwcPath);

                var init = await api.InitLinkAsync(new SyncInitLinkRequest
                {
                    ProjectId = projectId,
                    DisciplineType = disciplineType,
                    FileName = fileName,
                    FileSize = size
                }).ConfigureAwait(false);

                if (init == null || string.IsNullOrEmpty(init.UploadUrl) || string.IsNullOrEmpty(init.FileKey))
                {
                    // A server that predates the route answers 404, which throws
                    // below. Reaching here means it answered without the fields —
                    // treat that as "this server cannot take the NWC" rather than
                    // uploading to nowhere.
                    return Failed("This BINA server does not accept NWC links yet, so the model synced without one.");
                }

                bool uploaded = await api.UploadAsync(init.UploadUrl, nwcPath).ConfigureAwait(false);
                if (!uploaded)
                    return Failed("The NWC could not be uploaded to BINA storage, so it was not linked.");

                var link = await api.LinkNwcAsync(new SyncLinkRequest
                {
                    ProjectId = projectId,
                    DesignId = designId,
                    FileKey = init.FileKey,
                    FileName = fileName,
                    FileSize = size,
                    FileType = "nwc",
                    Notes = notes
                }).ConfigureAwait(false);

                return new Outcome
                {
                    Linked = true,
                    LinkId = link?.LinkId,
                    Message = $"{fileName} linked to this version."
                };
            }
            catch (Exception ex)
            {
                // Includes the 404 from a server without the route, and any 4xx
                // the guards raise (a foreign file key, a folder design id). The
                // version stands either way.
                var inner = (ex as AggregateException)?.InnerException ?? ex;
                return Failed($"The NWC was not linked: {inner.Message}");
            }
        }

        private static Outcome Failed(string message) =>
            new Outcome { Linked = false, Message = message };
    }
}
