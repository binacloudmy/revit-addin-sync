using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitWebAppSync
{
    public class BinaConfig
    {
        public string Email { get; set; }
        public string Password { get; set; }
        public int ProjectId { get; set; }
        public int UserId { get; set; }
        public int? OrgId { get; set; }   // organisation/team id, when the user belongs to one

        // Session data
        public string UserName { get; set; }
        public string ProjectName { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime TokenExpiry { get; set; }

        // Backend URLs — overridable via config.json so the addin doesn't need
        // a rebuild when ngrok tunnels rotate. Empty/missing values fall back
        // to the DEFAULT_* constants below.
        public string AIBaseUrl { get; set; }
        public string ApiBaseUrl { get; set; }

        public const string DEFAULT_AI_BASE_URL = "https://6400-2001-f40-935-7c0f-a8cf-1833-fa15-59dd.ngrok-free.app";
        public const string DEFAULT_API_BASE_URL = "https://6d9e82978eba.ngrok-free.app";

        [JsonIgnore]
        public string ResolvedAIBaseUrl =>
            !string.IsNullOrWhiteSpace(AIBaseUrl) ? AIBaseUrl : DEFAULT_AI_BASE_URL;

        [JsonIgnore]
        public string ResolvedApiBaseUrl =>
            !string.IsNullOrWhiteSpace(ApiBaseUrl) ? ApiBaseUrl : DEFAULT_API_BASE_URL;

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync",
            "config.json"
        );

        public static BinaConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return JsonConvert.DeserializeObject<BinaConfig>(json);
                }
            }
            catch (Exception ex)
            {
            }

            return new BinaConfig();
        }

        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
            }
        }

        public bool IsValid()
        {
            return !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(Password) && ProjectId > 0 && UserId > 0;
        }

        public bool IsLoggedIn()
        {
            return !string.IsNullOrEmpty(AccessToken)
                && !string.IsNullOrEmpty(UserName)
                && ProjectId > 0;
        }

        public void ClearSession()
        {
            Email = null;
            Password = null;
            UserName = null;
            ProjectName = null;
            AccessToken = null;
            RefreshToken = null;
            TokenExpiry = DateTime.MinValue;
            ProjectId = 0;
            UserId = 0;
        }
    }
}