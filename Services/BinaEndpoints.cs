using System.Configuration;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Centralized endpoint URLs for all BINA services.
    /// Reads from App.config appSettings, falls back to defaults.
    ///
    /// App.config keys:
    ///   - BinaAI_BaseUrl: AI agent backend (cost analysis, compliance, JKR search)
    ///   - BinaWeb_BaseUrl: BINA web app (auth, projects, sync)
    /// </summary>
    public static class BinaEndpoints
    {
        /// <summary>
        /// AI agent backend URL (bina-ai-agent-agno).
        /// Used by: AICostEstimator, AIService, ComplianceService
        /// </summary>
        public static string AIBaseUrl
        {
            get
            {
                var url = ConfigurationManager.AppSettings["BinaAI_BaseUrl"];
                return string.IsNullOrWhiteSpace(url)
                    ? "https://gastrodermal-ace-overvaliantly.ngrok-free.dev"
                    : url.TrimEnd('/');
            }
        }

        /// <summary>
        /// BINA web application URL (auth, cloud docs, sync).
        /// Used by: BinaApiService, AutodeskApiService
        /// </summary>
        public static string WebBaseUrl
        {
            get
            {
                var url = ConfigurationManager.AppSettings["BinaWeb_BaseUrl"];
                return string.IsNullOrWhiteSpace(url)
                    ? "https://6d9e82978eba.ngrok-free.app"
                    : url.TrimEnd('/');
            }
        }
    }
}
