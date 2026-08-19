using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Wire types for the bina-be sync protocol
    /// (/api/cloud-docs/bim-discipline/sync/*, ClickUp 86d3x42mz).
    /// </summary>
    public class WipFolder
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string DisciplineType { get; set; }

        public override string ToString() => Name;
    }

    /// <summary>Current server-side state of a lineage; null when never synced.</summary>
    public class SyncHead
    {
        public int DesignId { get; set; }
        public int? Version { get; set; }
        public string Name { get; set; }
        public int? UploadedBy { get; set; }
        public DateTime? UploadedAt { get; set; }
        public string FileHash { get; set; }
    }

    public class SyncHeadResponse
    {
        public SyncHead Head { get; set; }
    }

    public class SyncInitRequest
    {
        public int ProjectId { get; set; }
        public string DisciplineType { get; set; }
        public string FileName { get; set; }
        public int? ParentId { get; set; }
        public long? FileSize { get; set; }
        public string FileHash { get; set; }
        public string DocGuid { get; set; }
        public int? BaseVersion { get; set; }
    }

    public class SyncInitResponse
    {
        /// <summary>True when the bytes already on the server are identical — skip the upload.</summary>
        public bool Unchanged { get; set; }
        public int? DesignId { get; set; }
        public string UploadUrl { get; set; }
        /// <summary>Server-issued object key; the add-in no longer invents one.</summary>
        public string FileKey { get; set; }
        public SyncHead Head { get; set; }
    }

    /// <summary>
    /// One row of the rollback picker: a version that was synced at some point,
    /// current or not (86d3ut47q).
    /// </summary>
    public class DesignVersion
    {
        public int DesignId { get; set; }
        public int? VersionNumber { get; set; }
        public string Name { get; set; }
        public DateTime? UploadedAt { get; set; }
        public int? UploadedBy { get; set; }
        public string UploaderName { get; set; }
        public long? FileSize { get; set; }
        public string SyncComment { get; set; }
        public string SyncSource { get; set; }
        public string DesignStatus { get; set; }
        public string UrnInBase64 { get; set; }
        public string XktConversionStatus { get; set; }

        /// <summary>True on the version the cloud currently considers head.</summary>
        public bool IsActive { get; set; }

        /// <summary>Set when this version was itself published by a rollback.</summary>
        public int? RolledBackFromDesignId { get; set; }
    }

    public class DesignVersionsResponse
    {
        public List<DesignVersion> Versions { get; set; }
    }

    public class SyncCommitRequest : SyncInitRequest
    {
        public string FileKey { get; set; }
        /// <summary>Stable across retries of one attempt — this is what makes commit idempotent.</summary>
        public string SyncSessionId { get; set; }
        public string Comment { get; set; }
        public string UrnInBase64 { get; set; }
        public object ClientInfo { get; set; }
        public object Metadata { get; set; }

        /// <summary>
        /// Design id this model was restored from, sent on the FIRST sync after a
        /// rollback so the server can label the version it creates (86d3ut47q).
        /// Null on an ordinary sync.
        /// </summary>
        public int? RolledBackFromDesignId { get; set; }
    }

    public class SyncCommitResponse
    {
        /// <summary>created | unchanged | replayed</summary>
        public string Status { get; set; }
        public int DesignId { get; set; }
        public int? Version { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Raised when the server rejects a sync because someone else got there
    /// first. Carries the head so the dialog can name them and the version.
    /// </summary>
    public class SyncConflictException : Exception
    {
        public SyncHead Head { get; }

        public SyncConflictException(string message, SyncHead head) : base(message)
        {
            Head = head;
        }
    }

    /// <summary>Revit build + add-in version + worksharing state, stored on the version.</summary>
    public class SyncClientInfo
    {
        public string RevitVersion { get; set; }
        public string RevitBuild { get; set; }
        public string AddinVersion { get; set; }
        public bool IsWorkshared { get; set; }
        public string ActiveWorkset { get; set; }
        public string MachineName { get; set; }
    }
}
