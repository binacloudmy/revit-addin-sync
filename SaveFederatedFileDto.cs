using System.Collections.Generic;

namespace RevitWebAppSync
{
    public class SaveFederatedFileDto
    {
        public int ProjectId { get; set; }
        public string Name { get; set; }
        public string FileUrl { get; set; }
        public string FileKey { get; set; }
        public long FileSize { get; set; }
        public string FileType { get; set; }
        public int UploadedBy { get; set; }
        public FederatedFileMetadata Metadata { get; set; }
    }

    public class FederatedFileMetadata
    {
        public List<string> FederatedFrom { get; set; }
        public string FederationDate { get; set; }
        public List<string> SourceFiles { get; set; }
    }

    public class SaveFederatedFileResponseDto
    {
        public bool Success { get; set; }
        public SaveFederatedFileData Data { get; set; }
        public string Message { get; set; }
    }

    public class SaveFederatedFileData
    {
        public int Id { get; set; }
        public string Version { get; set; }
        public string Message { get; set; }
    }
}