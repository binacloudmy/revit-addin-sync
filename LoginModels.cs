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
        // Org / team id. The auth backend doesn't return this today, but the
        // addin stores it on BinaConfig.OrgId for team-scoped Copilot commands.
        // Captured here so the wiring is in place when the backend starts
        // returning it — also covers an alternative "OrganizationId" field name.
        public int? OrgId { get; set; }
        public int? OrganizationId { get; set; }
    }

    public class ProjectInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }

    public class UserProjectsResponse
    {
        public List<ProjectInfo> Projects { get; set; }
    }
}
