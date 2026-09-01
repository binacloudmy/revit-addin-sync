using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UiHarness
{
    /// <summary>
    /// Canned bina-be answers for everything SyncOptionsWindow asks for:
    /// projects, WIP folders, the models inside a folder, and the head for a
    /// filename. Lets the lineage picker be built and reviewed without a
    /// backend or a Revit boot.
    ///
    /// Like WipBrowseStubHandler this is a stub of the CONTRACT, and it keeps
    /// the awkward rows on purpose:
    ///   * folder 1 holds a model whose name matches the document being synced
    ///     (the collision the server resolves by filename, whatever the radio
    ///     says);
    ///   * folder 1 also holds a web upload with no docGuid — targetable, and
    ///     the reason the add-in must send null rather than its own GUID;
    ///   * folder 2 is empty, so "new version of an existing model" has nothing
    ///     to offer;
    ///   * folder 4 holds enough models to bring out the search box, and reports
    ///     hasMore so the count has to say it is partial.
    /// </summary>
    internal sealed class SyncOptionsStubHandler : HttpMessageHandler
    {
        /// <summary>Filename the head route answers for; anything else is a 404.</summary>
        public const string ExistingFileName = "ARC-Tower-A-Model.rvt";

        /// <summary>
        /// Provenance GUID that model carries. A document stamped with this one
        /// is that chain's own, so re-syncing it is the ordinary next version
        /// rather than a name collision.
        /// </summary>
        public const string ExistingDocGuid = "7d1b4e2a-0000-4a11-9f01-aa1122334455";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri.AbsolutePath;
            string query = request.RequestUri.Query ?? "";

            // Latency the real thing has and a local stub does not — without it
            // the loading states never render long enough to see.
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);

            if (path.EndsWith("/user/projects", StringComparison.OrdinalIgnoreCase))
                return Json(Projects);

            if (path.EndsWith("/folders", StringComparison.OrdinalIgnoreCase))
            {
                if (query.IndexOf("disciplineType=Structure", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Json(StructureFolders);
                if (query.IndexOf("disciplineType=Architecture", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Json(ArchitectureFolders);
                // A discipline with nothing set up yet: the dialog has to explain
                // itself rather than offering an empty combo.
                return Json("[]");
            }

            if (path.EndsWith("/designs", StringComparison.OrdinalIgnoreCase))
            {
                switch (FolderIdFrom(path))
                {
                    case 1: return Json(ArchitectureDesigns);
                    case 2: return Json(EmptyDesigns);
                    case 3: return Status(HttpStatusCode.Forbidden, "{\"message\":\"forbidden\"}");
                    case 4: return Json(ManyDesigns);
                    default: return Status(HttpStatusCode.NotFound, "{\"message\":\"no such folder\"}");
                }
            }

            if (path.EndsWith("/sync/head", StringComparison.OrdinalIgnoreCase))
            {
                // The head route is keyed on the same filename the server matches
                // a lineage on, so a hit here IS the collision the dialog warns
                // about. Only folder 1 has one.
                bool folder1 = query.Contains("parentId=1");
                bool sameName = query.IndexOf(Uri.EscapeDataString(ExistingFileName), StringComparison.OrdinalIgnoreCase) >= 0;
                if (folder1 && sameName) return Json(Head);
                return Status(HttpStatusCode.NotFound, "{\"message\":\"no head\"}");
            }

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

        private static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body);

        private static HttpResponseMessage Status(HttpStatusCode code, string body) =>
            new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

        // ------------------------------------------------------------ payloads

        private const string Projects = @"[
          { ""id"": 77, ""name"": ""PROJEK PEMBINAAN MAKTAB RENDAH SAINS MARA (MRSM) DUNGUN, TERENGGANU (REKA DAN BINA)"" },
          { ""id"": 92, ""name"": ""Hospital Sultanah Aminah - Blok Rawatan"" }
        ]";

        private const string ArchitectureFolders = @"[
          { ""id"": 1, ""name"": ""BIM Models"", ""disciplineType"": ""Architecture"", ""area"": ""wip"" },
          { ""id"": 2, ""name"": ""WIP - Sandbox"", ""disciplineType"": ""Architecture"", ""area"": ""wip"" },
          { ""id"": 4, ""name"": ""BIM Models - Package 2"", ""disciplineType"": ""Architecture"", ""area"": ""wip"" }
        ]";

        private const string StructureFolders = @"[
          { ""id"": 3, ""name"": ""STR - Substructure"", ""disciplineType"": ""Structure"", ""area"": ""wip"" }
        ]";

        private const string EmptyDesigns = @"{
          ""designs"": [], ""area"": ""wip"", ""nextCursor"": null, ""hasMore"": false, ""limit"": 200
        }";

        private const string ArchitectureDesigns = @"{
          ""designs"": [
            {
              ""docGuid"": ""7d1b4e2a-0000-4a11-9f01-aa1122334455"",
              ""designId"": 4211, ""lineageId"": ""11111111-2222-3333-4444-555555555555"",
              ""name"": ""ARC-Tower-A-Model.rvt"", ""area"": ""wip"",
              ""versionNumber"": 7, ""versionCount"": 7,
              ""uploadedAt"": ""2026-08-19T04:12:33.000Z"", ""uploadedBy"": 132, ""uploaderName"": ""Wafiy"",
              ""fileSize"": 184922112, ""fileHash"": ""9f2c1ab4de77"",
              ""disciplineType"": ""Architecture"", ""designStatus"": ""ACTIVE"", ""syncSource"": ""revit-addin""
            },
            {
              ""docGuid"": ""9a2c5f31-0000-4b22-8e02-bb2233445566"",
              ""designId"": 4290, ""lineageId"": ""22222222-3333-4444-5555-666666666666"",
              ""name"": ""ARC-Podium-Interiors.rvt"", ""area"": ""wip"",
              ""versionNumber"": 3, ""versionCount"": 3,
              ""uploadedAt"": ""2026-08-17T09:41:02.000Z"", ""uploadedBy"": 141, ""uploaderName"": ""Adham"",
              ""fileSize"": 61341696, ""disciplineType"": ""Architecture"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""revit-addin""
            },
            {
              ""docGuid"": null,
              ""designId"": 4402, ""lineageId"": ""33333333-4444-5555-6666-777777777777"",
              ""name"": ""ARC-Facade-Study (web upload).rvt"", ""area"": ""wip"",
              ""versionNumber"": 2, ""versionCount"": 2,
              ""uploadedAt"": ""2026-08-11T02:05:00.000Z"", ""uploaderName"": ""Nurul"",
              ""fileSize"": 24117248, ""disciplineType"": ""Architecture"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""web-upload""
            },
            {
              ""docGuid"": ""c4d8a9b0-0000-4c33-9d04-cc3344556677"",
              ""designId"": 4455, ""lineageId"": ""44444444-5555-6666-7777-888888888888"",
              ""name"": ""ARC-Coordination-Locked.rvt"", ""area"": ""wip"",
              ""versionNumber"": 12, ""versionCount"": 12,
              ""uploadedAt"": ""2026-08-20T23:58:10.000Z"", ""uploaderName"": ""BIM Manager"",
              ""fileSize"": 402653184, ""disciplineType"": ""Architecture"",
              ""designStatus"": ""ACTIVE"", ""syncSource"": ""revit-addin""
            }
          ],
          ""area"": ""wip"", ""nextCursor"": null, ""hasMore"": false, ""limit"": 200
        }";

        /// <summary>Enough rows for the search box, and a partial page on top.</summary>
        private const string ManyDesigns = @"{
          ""designs"": [
            { ""designId"": 5001, ""lineageId"": ""a1"", ""name"": ""ARC-P2-Block-A.rvt"", ""versionNumber"": 4, ""versionCount"": 4,
              ""uploadedAt"": ""2026-08-20T01:00:00.000Z"", ""uploaderName"": ""Wafiy"", ""fileSize"": 141557760, ""area"": ""wip"" },
            { ""designId"": 5002, ""lineageId"": ""a2"", ""name"": ""ARC-P2-Block-B.rvt"", ""versionNumber"": 2, ""versionCount"": 2,
              ""uploadedAt"": ""2026-08-19T01:00:00.000Z"", ""uploaderName"": ""Wafiy"", ""fileSize"": 128974848, ""area"": ""wip"" },
            { ""designId"": 5003, ""lineageId"": ""a3"", ""name"": ""ARC-P2-Block-C.rvt"", ""versionNumber"": 9, ""versionCount"": 9,
              ""uploadedAt"": ""2026-08-18T01:00:00.000Z"", ""uploaderName"": ""Adham"", ""fileSize"": 165675008, ""area"": ""wip"" },
            { ""designId"": 5004, ""lineageId"": ""a4"", ""name"": ""ARC-P2-Hostel-1.rvt"", ""versionNumber"": 1, ""versionCount"": 1,
              ""uploadedAt"": ""2026-08-17T01:00:00.000Z"", ""uploaderName"": ""Nurul"", ""fileSize"": 52428800, ""area"": ""wip"" },
            { ""designId"": 5005, ""lineageId"": ""a5"", ""name"": ""ARC-P2-Hostel-2.rvt"", ""versionNumber"": 3, ""versionCount"": 3,
              ""uploadedAt"": ""2026-08-16T01:00:00.000Z"", ""uploaderName"": ""Nurul"", ""fileSize"": 54525952, ""area"": ""wip"" },
            { ""designId"": 5006, ""lineageId"": ""a6"", ""name"": ""ARC-P2-Surau.rvt"", ""versionNumber"": 2, ""versionCount"": 2,
              ""uploadedAt"": ""2026-08-15T01:00:00.000Z"", ""uploaderName"": ""Faiz"", ""fileSize"": 20971520, ""area"": ""wip"" },
            { ""designId"": 5007, ""lineageId"": ""a7"", ""name"": ""ARC-P2-Dewan.rvt"", ""versionNumber"": 6, ""versionCount"": 6,
              ""uploadedAt"": ""2026-08-14T01:00:00.000Z"", ""uploaderName"": ""Faiz"", ""fileSize"": 88080384, ""area"": ""wip"" },
            { ""designId"": 5008, ""lineageId"": ""a8"", ""name"": ""ARC-P2-Kantin.rvt"", ""versionNumber"": 2, ""versionCount"": 2,
              ""uploadedAt"": ""2026-08-13T01:00:00.000Z"", ""uploaderName"": ""Adham"", ""fileSize"": 31457280, ""area"": ""wip"" },
            { ""designId"": 5009, ""lineageId"": ""a9"", ""name"": ""ARC-P2-Pagar-Landskap.rvt"", ""versionNumber"": 1, ""versionCount"": 1,
              ""uploadedAt"": ""2026-08-12T01:00:00.000Z"", ""uploaderName"": ""Wafiy"", ""fileSize"": 12582912, ""area"": ""wip"" },
            { ""designId"": 5010, ""lineageId"": ""a10"", ""name"": ""ARC-P2-Rumah-Warden.rvt"", ""versionNumber"": 5, ""versionCount"": 5,
              ""uploadedAt"": ""2026-08-11T01:00:00.000Z"", ""uploaderName"": ""Nurul"", ""fileSize"": 41943040, ""area"": ""wip"" }
          ],
          ""area"": ""wip"", ""nextCursor"": ""eyJpZCI6NTAxMH0"", ""hasMore"": true, ""limit"": 10
        }";

        private const string Head = @"{
          ""head"": {
            ""designId"": 4211, ""version"": 7, ""name"": ""ARC-Tower-A-Model.rvt"",
            ""uploadedBy"": 132, ""uploadedAt"": ""2026-08-19T04:12:33.000Z"",
            ""fileHash"": ""9f2c1ab4de77""
          }
        }";
    }
}
