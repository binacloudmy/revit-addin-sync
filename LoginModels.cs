using System;
using System.Collections.Generic;

namespace RevitWebAppSync
{
    public class LoginResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public long AccessTokenExpiry { get; set; }
        public int UserId { get; set; }
    }

    public class ProjectInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string BimRole { get; set; }
        public List<string> DisciplineTypes { get; set; }
    }

    public class UserProjectsResponse
    {
        public List<ProjectInfo> Projects { get; set; }
    }

    public class WipFolderInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string DisciplineType { get; set; }
    }
}
