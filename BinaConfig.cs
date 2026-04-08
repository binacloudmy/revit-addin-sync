using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace RevitWebAppSync
{
    public class BinaConfig
    {
        public string Email { get; set; }
        public string Password { get; set; }
        public int ProjectId { get; set; }
        public int UserId { get; set; }

        // Session data
        public string UserName { get; set; }
        public string ProjectName { get; set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime TokenExpiry { get; set; }

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync",
            "config.json"
        );

        // Prefix to identify DPAPI-encrypted values
        private const string EncryptedPrefix = "ENC:";

        public static BinaConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonConvert.DeserializeObject<BinaConfig>(json);
                    if (config != null)
                    {
                        // Decrypt sensitive fields
                        config.Password = Unprotect(config.Password);
                        config.AccessToken = Unprotect(config.AccessToken);
                        config.RefreshToken = Unprotect(config.RefreshToken);
                    }
                    return config;
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

                // Create a copy with encrypted sensitive fields
                var toSave = new BinaConfig
                {
                    Email = Email,
                    Password = Protect(Password),
                    ProjectId = ProjectId,
                    UserId = UserId,
                    UserName = UserName,
                    ProjectName = ProjectName,
                    AccessToken = Protect(AccessToken),
                    RefreshToken = Protect(RefreshToken),
                    TokenExpiry = TokenExpiry,
                };

                string json = JsonConvert.SerializeObject(toSave, Formatting.Indented);
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

        /// <summary>
        /// Encrypt a string using Windows DPAPI (current user scope).
        /// Returns null if input is null/empty.
        /// </summary>
        private static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plainText);
                byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return EncryptedPrefix + Convert.ToBase64String(encrypted);
            }
            catch
            {
                // If encryption fails, store as-is (shouldn't happen on Windows)
                return plainText;
            }
        }

        /// <summary>
        /// Decrypt a DPAPI-encrypted string. If the value is not encrypted
        /// (no ENC: prefix), returns it as-is for backwards compatibility.
        /// </summary>
        private static string Unprotect(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText)) return encryptedText;

            // Backwards compatible: if not encrypted, return as-is
            if (!encryptedText.StartsWith(EncryptedPrefix)) return encryptedText;

            try
            {
                string base64 = encryptedText.Substring(EncryptedPrefix.Length);
                byte[] encrypted = Convert.FromBase64String(base64);
                byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                // If decryption fails (e.g. different user), return empty
                return null;
            }
        }
    }
}
