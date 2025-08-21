using System;
using System.IO;
using Newtonsoft.Json;

namespace RevitWebAppSync
{
    public class BinaConfig
    {
        public string Email { get; set; }
        public string Password { get; set; }

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
            return !string.IsNullOrEmpty(Email) && !string.IsNullOrEmpty(Password);
        }
    }
}