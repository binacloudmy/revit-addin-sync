using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UiHarness
{
    /// <summary>
    /// Canned bina-be answers for the WIP browse routes
    /// (docs/wip-browse-backend-spec.md), so ModelBrowserWindow can be developed
    /// and reviewed before the backend ships them.
    ///
    /// This is a stub of the CONTRACT, not of the permission model: it answers
    /// exactly what the spec says a correctly-filtered response looks like, and
    /// includes the awkward rows on purpose — a web-uploaded model with no
    /// docGuid (resolvable only by designId), a browse-only model
    /// (canDownload:false), and one folder that 403s — because those are the
    /// paths that never get exercised by a happy-path fake.
    ///
    /// SyncApiClient takes an HttpClient, so nothing in the add-in has to know
    /// this exists.
    /// </summary>
    internal sealed class WipBrowseStubHandler : HttpMessageHandler
    {
        /// <summary>Fake .rvt payload size; big enough that progress ticks.</summary>
        private const int FakeModelBytes = 12 * 1024 * 1024;

        /// <summary>Slows the fake download so the progress bar is reviewable.</summary>
        public int DownloadDelayMs { get; set; } = 40;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri.AbsolutePath;
            string query = request.RequestUri.Query ?? "";

            // Network latency the real thing has and a local stub does not —
            // without it the loading states never render long enough to see.
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);

            if (path.EndsWith("/folders", StringComparison.OrdinalIgnoreCase))
            {
                if (query.Contains("area=shared")) return Json(SharedFolders);
                if (query.Contains("area=published")) return Json(PublishedFolders);
                return Json(WipFolders);
            }

            if (path.EndsWith("/designs", StringComparison.OrdinalIgnoreCase))
            {
                // Folder ids are unique project-wide, so the folder alone decides
                // the area. ?area= is an assertion: a mismatch is a 404, never a
                // cross-area read.
                int folderId = FolderIdFrom(path);
                string asserted = AreaFrom(query);
                string actual = AreaOfFolder(folderId);

                if (actual == null)
                    return Status(HttpStatusCode.NotFound, "{\"message\":\"no such folder\"}");

                if (asserted != null && asserted != actual)
                    return Status(HttpStatusCode.NotFound, "{\"message\":\"folder is not in area " + asserted + "\"}");

                // Folder 3 is the one this user's role cannot read — the spec says
                // 403, never an empty list, so the UI can say which it is.
                if (folderId == 3)
                    return Status(HttpStatusCode.Forbidden, "{\"message\":\"forbidden\"}");

                if (folderId == 2) return Json(StructuralDesigns);
                if (folderId == 11) return Json(SharedDesigns);
                if (folderId == 21) return Json(PublishedDesigns);
                return Json(ArchitectureDesigns);
            }

            if (path.EndsWith("/sync/versions", StringComparison.OrdinalIgnoreCase))
            {
                // Both keys resolve here. Promoted rows carry no docGuid at all,
                // so Shared and Published always arrive as designId — and the
                // area rides along so a lookup cannot land in the wrong chain.
                if (query.Contains("designId=6120")) return Json(SharedVersions);
                if (query.Contains("designId=6220")) return Json(SharedStructureVersions);
                if (query.Contains("designId=7010")) return Json(PublishedVersions);
                if (query.Contains("designId=4402")) return Json(WebUploadVersions);
                return Json(SyncedVersions);
            }

            if (path.Contains("/download"))
                return await FakeDownload(cancellationToken).ConfigureAwait(false);

            return Status(HttpStatusCode.NotFound, "{\"message\":\"no stub for " + path + "\"}");
        }

        /// <summary>Folder id out of `/project/{p}/folder/{id}/designs`.</summary>
        private static int FolderIdFrom(string path)
        {
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                int id;
                if (string.Equals(parts[i], "folder", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(parts[i + 1], out id))
                    return id;
            }
            return -1;
        }

        private static string AreaFrom(string query)
        {
            if (query.Contains("area=shared")) return "shared";
            if (query.Contains("area=published")) return "published";
            if (query.Contains("area=wip")) return "wip";
            return null;
        }

        /// <summary>Which area owns a folder id, or null when no such folder.</summary>
        private static string AreaOfFolder(int folderId)
        {
            if (folderId >= 1 && folderId <= 3) return "wip";
            if (folderId == 11) return "shared";
            if (folderId == 21) return "published";
            return null;
        }

        private async Task<HttpResponseMessage> FakeDownload(CancellationToken cancellationToken)
        {
            var stream = new SlowStream(FakeModelBytes, DownloadDelayMs, cancellationToken);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = FakeModelBytes;

            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body);

        private static HttpResponseMessage Status(HttpStatusCode code, string body) =>
            new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        // ------------------------------------------------------------ payloads

        private const string WipFolders = @"[
          { ""id"": 1, ""name"": ""ARC - Tower A"", ""disciplineType"": ""ARCHITECTURE"", ""area"": ""wip"" },
          { ""id"": 2, ""name"": ""STR - Substructure"", ""disciplineType"": ""STRUCTURE"", ""area"": ""wip"" },
          { ""id"": 3, ""name"": ""MEP - Wet Services"", ""disciplineType"": ""MECHANICAL"", ""area"": ""wip"" }
        ]";

        // Shared reads wider than WIP: the MEP folder that 403s in WIP is
        // readable here, which is what publishing is for.
        private const string SharedFolders = @"[
          { ""id"": 11, ""name"": ""BIM Models"", ""disciplineType"": ""ARCHITECTURE"", ""area"": ""shared"" }
        ]";

        private const string PublishedFolders = @"[
          { ""id"": 21, ""name"": ""BIM Models"", ""disciplineType"": ""ARCHITECTURE"", ""area"": ""published"" }
        ]";

        // Promotion normally mirrors the version number, so promotedFrom* is
        // present but silent. Row 2 is the case that is NOT mirrored — the number
        // was taken in the target folder — and that is the one the picker names.
        private const string SharedDesigns = @"{
          ""designs"": [
            {
              ""docGuid"": null,
              ""designId"": 6120, ""lineageId"": ""11111111-2222-3333-4444-555555555555"",
              ""name"": ""ARC-Tower-A-Model.rvt"", ""area"": ""shared"",
              ""versionNumber"": 6, ""versionCount"": 6,
              ""uploadedAt"": ""2026-08-18T02:00:00.000Z"", ""uploaderName"": ""Lead Coordinator"",
              ""fileSize"": 181403648, ""disciplineType"": ""ARCHITECTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""web"",
              ""promotedFromDesignId"": 4180, ""promotedFromVersionNumber"": 6,
              ""promotedFromArea"": ""wip"",
              ""urnInBase64"": ""dXJuOmFkc2sz""
            },
            {
              ""docGuid"": null,
              ""designId"": 6220, ""name"": ""STR-Piling-Layout.rvt"", ""area"": ""shared"",
              ""versionNumber"": 5, ""versionCount"": 5,
              ""uploadedAt"": ""2026-08-19T07:30:00.000Z"", ""uploaderName"": ""Lead Coordinator"",
              ""fileSize"": 95420416, ""disciplineType"": ""STRUCTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""web"",
              ""promotedFromDesignId"": 5010, ""promotedFromVersionNumber"": 4,
              ""promotedFromArea"": ""wip""
            }
          ],
          ""area"": ""shared"", ""nextCursor"": null, ""hasMore"": false, ""limit"": 200
        }";

        private const string PublishedDesigns = @"{
          ""designs"": [
            {
              ""docGuid"": null,
              ""designId"": 7010, ""name"": ""ARC-Tower-A-Model.rvt"", ""area"": ""published"",
              ""versionNumber"": 6, ""versionCount"": 2,
              ""uploadedAt"": ""2026-08-20T01:15:00.000Z"", ""uploaderName"": ""BIM Manager"",
              ""fileSize"": 181403648, ""disciplineType"": ""ARCHITECTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""web"",
              ""promotedFromDesignId"": 6120, ""promotedFromVersionNumber"": 6,
              ""promotedFromArea"": ""shared"", ""canDownload"": false
            }
          ],
          ""area"": ""published"", ""nextCursor"": null, ""hasMore"": false, ""limit"": 200
        }";

        private const string SharedVersions = @"{
          ""versions"": [
            { ""designId"": 6120, ""versionNumber"": 6, ""name"": ""ARC-Tower-A-Model.rvt"",
              ""uploadedAt"": ""2026-08-18T02:00:00.000Z"", ""uploaderName"": ""Lead Coordinator"",
              ""fileSize"": 181403648, ""isActive"": true,
              ""promotedFromDesignId"": 4180, ""promotedFromVersionNumber"": 6,
              ""promotedFromArea"": ""wip"", ""urnInBase64"": ""dXJuOmFkc2sz"" },
            { ""designId"": 6050, ""versionNumber"": 4, ""name"": ""ARC-Tower-A-Model.rvt"",
              ""uploadedAt"": ""2026-08-12T03:20:00.000Z"", ""uploaderName"": ""Lead Coordinator"",
              ""fileSize"": 176160768, ""isActive"": false,
              ""promotedFromDesignId"": 4102, ""promotedFromVersionNumber"": 5,
              ""promotedFromArea"": ""wip"" }
          ]
        }";

        // The un-mirrored chain: Shared V5 came from WIP V4 because 5 was already
        // taken in the target folder. Every row here names its source, since none
        // of the numbers line up.
        private const string SharedStructureVersions = @"{
          ""versions"": [
            { ""designId"": 6220, ""versionNumber"": 5, ""name"": ""STR-Piling-Layout.rvt"",
              ""uploadedAt"": ""2026-08-19T07:30:00.000Z"", ""uploaderName"": ""Lead Coordinator"",
              ""fileSize"": 95420416, ""isActive"": true,
              ""promotedFromDesignId"": 5010, ""promotedFromVersionNumber"": 4,
              ""promotedFromArea"": ""wip"" },
            { ""designId"": 6180, ""versionNumber"": 3, ""name"": ""STR-Piling-Layout.rvt"",
              ""uploadedAt"": ""2026-08-14T05:10:00.000Z"", ""uploaderName"": ""Lead Coordinator"",
              ""fileSize"": 91226112, ""isActive"": false,
              ""promotedFromDesignId"": 4980, ""promotedFromVersionNumber"": 2,
              ""promotedFromArea"": ""wip"" }
          ]
        }";

        private const string PublishedVersions = @"{
          ""versions"": [
            { ""designId"": 7010, ""versionNumber"": 6, ""name"": ""ARC-Tower-A-Model.rvt"",
              ""uploadedAt"": ""2026-08-20T01:15:00.000Z"", ""uploaderName"": ""BIM Manager"",
              ""fileSize"": 181403648, ""isActive"": true,
              ""promotedFromDesignId"": 6120, ""promotedFromVersionNumber"": 6,
              ""promotedFromArea"": ""shared"" }
          ]
        }";

        private const string ArchitectureDesigns = @"{
          ""designs"": [
            {
              ""docGuid"": ""7d1b4e2a-0000-4a11-9f01-aa1122334455"",
              ""designId"": 4211, ""lineageId"": ""11111111-2222-3333-4444-555555555555"",
              ""name"": ""ARC-Tower-A-Model.rvt"",
              ""area"": ""wip"", ""versionNumber"": 7, ""versionCount"": 7,
              ""uploadedAt"": ""2026-08-19T04:12:33.000Z"",
              ""uploadedBy"": 132, ""uploaderName"": ""Wafiy"",
              ""fileSize"": 184922112, ""fileHash"": ""9f2c1ab4de77"",
              ""disciplineType"": ""ARCHITECTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""revit-addin"",
              ""urnInBase64"": ""dXJuOmFkc2s="", ""xktConversionStatus"": ""Completed""
            },
            {
              ""docGuid"": ""9a2c5f31-0000-4b22-8e02-bb2233445566"",
              ""designId"": 4290, ""name"": ""ARC-Podium-Interiors.rvt"",
              ""versionNumber"": 3, ""versionCount"": 3,
              ""uploadedAt"": ""2026-08-17T09:41:02.000Z"",
              ""uploadedBy"": 141, ""uploaderName"": ""Adham"",
              ""fileSize"": 61341696, ""disciplineType"": ""ARCHITECTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""revit-addin"",
              ""xktConversionStatus"": ""InProgress""
            },
            {
              ""docGuid"": null,
              ""designId"": 4402, ""name"": ""ARC-Facade-Study (web upload).rvt"",
              ""versionNumber"": 2, ""versionCount"": 2,
              ""uploadedAt"": ""2026-08-11T02:05:00.000Z"",
              ""uploaderName"": ""Nurul"",
              ""fileSize"": 24117248, ""disciplineType"": ""ARCHITECTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""web-upload""
            },
            {
              ""docGuid"": ""c4d8a9b0-0000-4c33-9d04-cc3344556677"",
              ""designId"": 4455, ""name"": ""ARC-Coordination-Locked.rvt"",
              ""versionNumber"": 12, ""versionCount"": 12,
              ""uploadedAt"": ""2026-08-20T23:58:10.000Z"",
              ""uploaderName"": ""BIM Manager"",
              ""fileSize"": 402653184, ""disciplineType"": ""ARCHITECTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""revit-addin"",
              ""canDownload"": false
            }
          ],
          ""area"": ""wip"", ""nextCursor"": null, ""hasMore"": false, ""limit"": 200
        }";

        private const string StructuralDesigns = @"{
          ""designs"": [
            {
              ""docGuid"": ""1f0e7c22-0000-4d44-8a05-dd4455667788"",
              ""designId"": 5010, ""name"": ""STR-Piling-Layout.rvt"",
              ""versionNumber"": 4, ""versionCount"": 4,
              ""uploadedAt"": ""2026-08-18T11:20:44.000Z"",
              ""uploaderName"": ""Faiz"",
              ""fileSize"": 95420416, ""disciplineType"": ""STRUCTURE"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""revit-addin"",
              ""urnInBase64"": ""dXJuOmFkc2sy""
            }
          ],
          ""nextCursor"": null, ""hasMore"": false, ""limit"": 200
        }";

        private const string SyncedVersions = @"{
          ""versions"": [
            { ""designId"": 4211, ""versionNumber"": 7, ""name"": ""ARC-Tower-A-Model.rvt"",
              ""uploadedAt"": ""2026-08-19T04:12:33.000Z"", ""uploaderName"": ""Wafiy"",
              ""fileSize"": 184922112, ""syncComment"": ""Level 12-18 core walls"",
              ""isActive"": true, ""urnInBase64"": ""dXJuOmFkc2s="" },
            { ""designId"": 4180, ""versionNumber"": 6, ""name"": ""ARC-Tower-A-Model.rvt"",
              ""uploadedAt"": ""2026-08-15T08:02:11.000Z"", ""uploaderName"": ""Wafiy"",
              ""fileSize"": 181403648, ""syncComment"": ""Coordination issue #204 closed"",
              ""isActive"": false, ""rolledBackFromDesignId"": 4102,
              ""xktConversionStatus"": ""Completed"" },
            { ""designId"": 4102, ""versionNumber"": 5, ""name"": ""ARC-Tower-A-Model.rvt"",
              ""uploadedAt"": ""2026-08-08T15:33:57.000Z"", ""uploaderName"": ""Adham"",
              ""fileSize"": 176160768, ""isActive"": false }
          ]
        }";

        private const string WebUploadVersions = @"{
          ""versions"": [
            { ""designId"": 4402, ""versionNumber"": 2, ""name"": ""ARC-Facade-Study (web upload).rvt"",
              ""uploadedAt"": ""2026-08-11T02:05:00.000Z"", ""uploaderName"": ""Nurul"",
              ""fileSize"": 24117248, ""isActive"": true },
            { ""designId"": 4390, ""versionNumber"": 1, ""name"": ""ARC-Facade-Study (web upload).rvt"",
              ""uploadedAt"": ""2026-08-04T06:47:19.000Z"", ""uploaderName"": ""Nurul"",
              ""fileSize"": 23068672, ""isActive"": false }
          ]
        }";

        /// <summary>
        /// Emits zeroed bytes slowly, so the progress bar and the Cancel path are
        /// exercisable. Honours the download's CancellationToken the same way a
        /// socket read would.
        /// </summary>
        private sealed class SlowStream : System.IO.Stream
        {
            private readonly int _total;
            private readonly int _delayMs;
            private readonly CancellationToken _token;
            private int _served;

            public SlowStream(int total, int delayMs, CancellationToken token)
            {
                _total = total;
                _delayMs = delayMs;
                _token = token;
            }

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_served >= _total) return 0;

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken))
                    await Task.Delay(_delayMs, linked.Token).ConfigureAwait(false);

                int chunk = Math.Min(count, Math.Min(262144, _total - _served));
                Array.Clear(buffer, offset, chunk);
                _served += chunk;
                return chunk;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _total;
            public override long Position
            {
                get { return _served; }
                set { throw new NotSupportedException(); }
            }
            public override void Flush() { }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
