using System;

namespace BinaConnector
{
    /// <summary>
    /// Backend endpoint configuration. Override at runtime via the BINA_API_BASE_URL env var.
    /// </summary>
    public static class BinaApiConfig
    {
        // PLACEHOLDER — replace with production BINA API URL before App Store submission.
        public const string DEFAULT_BASE_URL = "https://api.bina.cloud";

        public const string DEFAULT_WEB_APP_URL = "https://app.bina.cloud";

        public static string BaseUrl =>
            Environment.GetEnvironmentVariable("BINA_API_BASE_URL")?.TrimEnd('/')
            ?? DEFAULT_BASE_URL;

        public static string WebAppUrl =>
            Environment.GetEnvironmentVariable("BINA_WEB_APP_URL")?.TrimEnd('/')
            ?? DEFAULT_WEB_APP_URL;

        public const string UserAgent = "BinaConnector/1.0";

        // Standard control-API timeout. Upload operations use a longer timeout on the
        // HttpClient that performs them.
        public static readonly TimeSpan ControlApiTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(10);
    }
}
