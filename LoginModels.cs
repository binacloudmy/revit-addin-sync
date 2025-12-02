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
        public string Number { get; set; }
        public string Address { get; set; }
        public string ClientName { get; set; }

        /// <summary>
        /// Gets a display-friendly name for the project
        /// </summary>
        public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : "Unknown Project";
    }

    public class UserProjectsResponse
    {
        public List<ProjectInfo> Projects { get; set; }
    }
}
