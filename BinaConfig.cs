using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace BinaConnector
{
    /// <summary>
    /// Persisted user session. Refresh token is stored DPAPI-encrypted (CurrentUser scope).
    /// Access token and password are NEVER persisted; access tokens live in-memory only.
    /// </summary>
    public class BinaConfig
    {
        public int UserId { get; set; }
        public string UserName { get; set; }
        public int ProjectId { get; set; }
        public string ProjectName { get; set; }

        /// <summary>DPAPI-encrypted refresh token (base64). Null if user has not signed in.</summary>
        public string EncryptedRefreshToken { get; set; }

        // In-memory only — not serialized.
        [JsonIgnore] public string AccessToken { get; set; }
        [JsonIgnore] public DateTime TokenExpiry { get; set; }

        public static BinaConfig Load()
        {
            try
            {
                if (File.Exists(Paths.ConfigFile))
                {
                    string json = File.ReadAllText(Paths.ConfigFile);
                    return JsonConvert.DeserializeObject<BinaConfig>(json) ?? new BinaConfig();
                }
            }
            catch
            {
                // Corrupt or unreadable config — start fresh rather than crashing the addin.
            }
            return new BinaConfig();
        }

        public void Save()
        {
            try
            {
                Paths.EnsureDirectories();
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(Paths.ConfigFile, json);
            }
            catch
            {
                // Persistence failures are non-fatal (in-memory session continues to work).
            }
        }

        public bool IsLoggedIn() =>
            !string.IsNullOrEmpty(AccessToken)
            && !string.IsNullOrEmpty(UserName)
            && ProjectId > 0;

        public bool HasRefreshableSession() => !string.IsNullOrEmpty(EncryptedRefreshToken);

        public void ClearSession()
        {
            UserName = null;
            UserId = 0;
            ProjectId = 0;
            ProjectName = null;
            AccessToken = null;
            TokenExpiry = DateTime.MinValue;
            EncryptedRefreshToken = null;
        }

        /// <summary>Set the refresh token, encrypting it with DPAPI for at-rest storage.</summary>
        public void SetRefreshToken(string plainRefreshToken)
        {
            EncryptedRefreshToken = string.IsNullOrEmpty(plainRefreshToken)
                ? null
                : ProtectString(plainRefreshToken);
        }

        /// <summary>Decrypt the stored refresh token. Returns null if absent or unreadable.</summary>
        public string GetRefreshToken()
        {
            if (string.IsNullOrEmpty(EncryptedRefreshToken)) return null;
            try { return UnprotectString(EncryptedRefreshToken); }
            catch { return null; }
        }

        // DPAPI helpers. CurrentUser scope: only the same Windows user account can decrypt.
        private static string ProtectString(string plain)
        {
            byte[] data = Encoding.UTF8.GetBytes(plain);
            byte[] cipher = ProtectedData.Protect(data, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }

        private static string UnprotectString(string base64Cipher)
        {
            byte[] cipher = Convert.FromBase64String(base64Cipher);
            byte[] data = ProtectedData.Unprotect(cipher, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
    }
}
