using System;
using System.Collections.Generic;

namespace BinaConnector
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
    }

    public class UserProjectsResponse
    {
        public List<ProjectInfo> Projects { get; set; }
    }
}
