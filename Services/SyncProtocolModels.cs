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

    public class SyncCommitRequest : SyncInitRequest
    {
        public string FileKey { get; set; }
        /// <summary>Stable across retries of one attempt — this is what makes commit idempotent.</summary>
        public string SyncSessionId { get; set; }
        public string Comment { get; set; }
        public string UrnInBase64 { get; set; }
        public object ClientInfo { get; set; }
        public object Metadata { get; set; }
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

    /// <summary>
    /// One Bina parameter to write onto an element (ClickUp 86d3y5jxx).
    ///
    /// `ElementExternalId` is the Revit UniqueId — BINA stores it rather than
    /// the viewer's dbId precisely so that it resolves here, through
    /// `doc.GetElement(uniqueId)`.
    /// </summary>
    public class BinaElementParameter
    {
        public string ElementExternalId { get; set; }
        public string ParameterName { get; set; }
        /// <summary>Text | Number | YesNo | Date — how BINA typed the value.</summary>
        public string ParameterType { get; set; }
        /// <summary>Always a string on the wire; coerced to the Revit storage type on write.</summary>
        public string Value { get; set; }
        /// <summary>Add = a new Bina parameter; Override = a value placed over an existing one.</summary>
        public string Source { get; set; }
        /// <summary>Version the value was entered on — shown in the summary, not used for matching.</summary>
        public int? FromVersion { get; set; }
    }

    /// <summary>The design an open document turned out to be (GET design/resolve).</summary>
    public class ResolvedDesign
    {
        public int DesignId { get; set; }
        public int ProjectId { get; set; }
        /// <summary>BINA folder — the thing the document itself gives no clue about.</summary>
        public int? ParentId { get; set; }
        public string Name { get; set; }
        public int? VersionNumber { get; set; }
        public string DesignStatus { get; set; }
        public string DisciplineType { get; set; }
        public string LineageId { get; set; }
    }

    /// <summary>Response of GET design/:id/element-parameters.</summary>
    public class ElementParametersResponse
    {
        public int DesignId { get; set; }
        public string LineageId { get; set; }
        public int? VersionNumber { get; set; }
        /// <summary>lineage (whole version chain, the default) | design (this version only).</summary>
        public string Scope { get; set; }
        public int Count { get; set; }
        /// <summary>True when the server clipped the set at its ceiling.</summary>
        public bool Truncated { get; set; }
        public List<BinaElementParameter> Parameters { get; set; }
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
