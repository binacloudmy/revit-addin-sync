using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text.Json;

namespace RevitWebAppSync.Utils
{
    /// <summary>
    /// Manages configuration settings for the Revit add-in
    /// Supports multiple configuration sources: app.config, JSON files, environment variables
    /// TODO: Customize based on your configuration requirements and security policies
    /// </summary>
    public static class ConfigManager
    {
        #region Private Fields

        private static Dictionary<string, string> _cachedSettings;
        private static readonly object _lockObject = new object();
        private static DateTime _lastConfigLoad = DateTime.MinValue;
        private static readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(5);

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets a configuration setting with optional default value
        /// Searches in order: Environment Variables -> JSON config -> app.config
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <param name="defaultValue">Default value if setting not found</param>
        /// <returns>Configuration value or default</returns>
        public static string GetSetting(string key, string defaultValue = null)
        {
            if (string.IsNullOrEmpty(key))
                return defaultValue;

            try
            {
                EnsureConfigurationLoaded();

                // Priority 1: Environment variables (highest priority)
                var envValue = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue;
                }

                // Priority 2: Cached settings from JSON/config files
                if (_cachedSettings.TryGetValue(key, out string cachedValue))
                {
                    return cachedValue;
                }

                // Priority 3: app.config (lowest priority)
                var configValue = ConfigurationManager.AppSettings[key];
                if (!string.IsNullOrEmpty(configValue))
                {
                    return configValue;
                }

                return defaultValue;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                return defaultValue;
            }
        }

        /// <summary>
        /// Gets a configuration setting as integer
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <param name="defaultValue">Default value if setting not found or invalid</param>
        /// <returns>Configuration value as integer</returns>
        public static int GetIntSetting(string key, int defaultValue = 0)
        {
            var stringValue = GetSetting(key);
            if (int.TryParse(stringValue, out int result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets a configuration setting as boolean
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <param name="defaultValue">Default value if setting not found or invalid</param>
        /// <returns>Configuration value as boolean</returns>
        public static bool GetBoolSetting(string key, bool defaultValue = false)
        {
            var stringValue = GetSetting(key);
            if (bool.TryParse(stringValue, out bool result))
            {
                return result;
            }

            // Handle common string representations of boolean values
            if (!string.IsNullOrEmpty(stringValue))
            {
                var lowerValue = stringValue.ToLowerInvariant();
                if (lowerValue == "1" || lowerValue == "yes" || lowerValue == "on")
                    return true;
                if (lowerValue == "0" || lowerValue == "no" || lowerValue == "off")
                    return false;
            }

            return defaultValue;
        }

        /// <summary>
        /// Gets a configuration setting as double
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <param name="defaultValue">Default value if setting not found or invalid</param>
        /// <returns>Configuration value as double</returns>
        public static double GetDoubleSetting(string key, double defaultValue = 0.0)
        {
            var stringValue = GetSetting(key);
            if (double.TryParse(stringValue, out double result))
            {
                return result;
            }
            return defaultValue;
        }

        /// <summary>
        /// Gets a configuration setting as TimeSpan
        /// Accepts formats like "00:05:00" (5 minutes) or number of seconds
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <param name="defaultValue">Default value if setting not found or invalid</param>
        /// <returns>Configuration value as TimeSpan</returns>
        public static TimeSpan GetTimeSpanSetting(string key, TimeSpan defaultValue)
        {
            var stringValue = GetSetting(key);
            if (string.IsNullOrEmpty(stringValue))
                return defaultValue;

            // Try parsing as TimeSpan first (e.g., "00:05:00")
            if (TimeSpan.TryParse(stringValue, out TimeSpan result))
            {
                return result;
            }

            // Try parsing as seconds
            if (int.TryParse(stringValue, out int seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return defaultValue;
        }

        /// <summary>
        /// Sets a configuration setting (runtime only - not persisted)
        /// TODO: Implement persistent setting storage if needed
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <param name="value">Configuration value</param>
        public static void SetSetting(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
                return;

            lock (_lockObject)
            {
                EnsureConfigurationLoaded();
                _cachedSettings[key] = value;
            }
        }

        /// <summary>
        /// Loads configuration from all sources
        /// Returns complete configuration dictionary
        /// </summary>
        /// <returns>Dictionary of all configuration settings</returns>
        public static Dictionary<string, string> LoadConfiguration()
        {
            lock (_lockObject)
            {
                EnsureConfigurationLoaded();
                return new Dictionary<string, string>(_cachedSettings);
            }
        }

        /// <summary>
        /// Forces reload of configuration from all sources
        /// Useful after configuration files are updated
        /// </summary>
        public static void RefreshConfiguration()
        {
            lock (_lockObject)
            {
                _cachedSettings = null;
                _lastConfigLoad = DateTime.MinValue;
                EnsureConfigurationLoaded();
            }
        }

        /// <summary>
        /// Gets the path to the JSON configuration file
        /// </summary>
        /// <returns>Path to configuration file</returns>
        public static string GetConfigFilePath()
        {
            // Look for config file in several locations
            var locations = new[]
            {
                Path.Combine(GetAddInDirectory(), "config.json"),
                Path.Combine(GetUserDataDirectory(), "config.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitWebAppSync", "config.json")
            };

            foreach (var location in locations)
            {
                if (File.Exists(location))
                {
                    return location;
                }
            }

            // Return default location (may not exist)
            return locations[0];
        }

        /// <summary>
        /// Creates a sample configuration file with default settings
        /// TODO: Customize with your application's specific settings
        /// </summary>
        /// <param name="filePath">Path where to create the config file</param>
        public static void CreateSampleConfigFile(string filePath = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                filePath = GetConfigFilePath();
            }

            var sampleConfig = new
            {
                // Autodesk APS Settings
                APS_CLIENT_ID = "your-aps-client-id",
                APS_CLIENT_SECRET = "your-aps-client-secret",
                APS_REDIRECT_URI = "http://localhost:8080/callback",
                OSS_BucketKey = "revit-webapp-sync-your-company",
                OSS_Region = "US",

                // Web Application Settings
                WebApp_BaseUrl = "https://your-webapp.com/api",
                WebApp_ApiKey = "your-web-app-api-key",

                // File Processing Settings
                ExportFormat = "RVT",
                MaxFileSizeMB = "100",
                AutoSync = "false",

                // Performance Settings
                EnableFileHash = "true",
                HashMethod = "SHA256",
                CacheTokens = "true",

                // Logging Settings
                LogLevel = "Info",
                LogToFile = "true",
                LogFilePath = "",

                // UI Settings
                ShowProgressDialog = "true",
                AutoCloseProgress = "true",
                ConfirmBeforeSync = "true"
            };

            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(sampleConfig, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create sample config file: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Ensures configuration is loaded and up-to-date
        /// </summary>
        private static void EnsureConfigurationLoaded()
        {
            if (_cachedSettings == null || DateTime.UtcNow - _lastConfigLoad > _cacheTimeout)
            {
                LoadConfigurationFromSources();
                _lastConfigLoad = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Loads configuration from all available sources
        /// </summary>
        private static void LoadConfigurationFromSources()
        {
            _cachedSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Load from JSON configuration file
            LoadFromJsonConfig();

            // Load from app.config
            LoadFromAppConfig();

            // TODO: Add other configuration sources as needed
            // For example: registry, database, web service, etc.
        }

        /// <summary>
        /// Loads settings from JSON configuration file
        /// </summary>
        private static void LoadFromJsonConfig()
        {
            try
            {
                var configPath = GetConfigFilePath();
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var configData = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                    if (configData != null)
                    {
                        foreach (var kvp in configData)
                        {
                            _cachedSettings[kvp.Key] = kvp.Value?.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // TODO: Log JSON config loading error
                // Continue with other configuration sources
            }
        }

        /// <summary>
        /// Loads settings from app.config
        /// </summary>
        private static void LoadFromAppConfig()
        {
            try
            {
                foreach (string key in ConfigurationManager.AppSettings.AllKeys)
                {
                    if (!_cachedSettings.ContainsKey(key))
                    {
                        _cachedSettings[key] = ConfigurationManager.AppSettings[key];
                    }
                }
            }
            catch (Exception ex)
            {
                // TODO: Log app.config loading error
            }
        }

        /// <summary>
        /// Gets the directory where the add-in is installed
        /// </summary>
        /// <returns>Add-in directory path</returns>
        private static string GetAddInDirectory()
        {
            try
            {
                var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(assemblyLocation);
            }
            catch
            {
                return Environment.CurrentDirectory;
            }
        }

        /// <summary>
        /// Gets the user data directory for the add-in
        /// </summary>
        /// <returns>User data directory path</returns>
        private static string GetUserDataDirectory()
        {
            try
            {
                var userDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(userDataPath, "RevitWebAppSync");
            }
            catch
            {
                return Environment.CurrentDirectory;
            }
        }

        #endregion

        #region Configuration Validation

        /// <summary>
        /// Validates that required configuration settings are present
        /// </summary>
        /// <returns>Validation result</returns>
        public static ConfigValidationResult ValidateConfiguration()
        {
            var result = new ConfigValidationResult();

            // Required APS settings
            ValidateRequired("APS_CLIENT_ID", "Autodesk APS Client ID is required", result);
            ValidateRequired("APS_CLIENT_SECRET", "Autodesk APS Client Secret is required", result);
            ValidateRequired("WebApp_BaseUrl", "Web Application Base URL is required", result);

            // Optional but recommended settings
            ValidateOptional("OSS_BucketKey", "OSS Bucket Key should be configured for file storage", result);
            ValidateOptional("WebApp_ApiKey", "Web Application API Key should be configured for authentication", result);

            // Validate URL formats
            ValidateUrl("WebApp_BaseUrl", "Web Application Base URL format is invalid", result);
            ValidateUrl("APS_REDIRECT_URI", "APS Redirect URI format is invalid", result);

            // Validate numeric settings
            ValidateNumeric("MaxFileSizeMB", "Maximum file size must be a valid number", result);

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        private static void ValidateRequired(string key, string errorMessage, ConfigValidationResult result)
        {
            var value = GetSetting(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                result.Errors.Add(errorMessage);
            }
        }

        private static void ValidateOptional(string key, string warningMessage, ConfigValidationResult result)
        {
            var value = GetSetting(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                result.Warnings.Add(warningMessage);
            }
        }

        private static void ValidateUrl(string key, string errorMessage, ConfigValidationResult result)
        {
            var value = GetSetting(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out _))
                {
                    result.Errors.Add(errorMessage);
                }
            }
        }

        private static void ValidateNumeric(string key, string errorMessage, ConfigValidationResult result)
        {
            var value = GetSetting(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (!int.TryParse(value, out _))
                {
                    result.Errors.Add(errorMessage);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Result of configuration validation
    /// </summary>
    public class ConfigValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();

        public bool HasWarnings => Warnings.Count > 0;
        public bool HasErrors => Errors.Count > 0;
    }
}