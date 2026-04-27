using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace BinaConnector
{
    /// <summary>Translates network/HTTP failures into messages suitable for end users.</summary>
    internal static class NetworkErrors
    {
        public static string Friendly(Exception ex)
        {
            switch (ex)
            {
                case TaskCanceledException _:
                    return "The request to BINA Cloud timed out. Please check your internet connection and try again.";
                case HttpRequestException hre when hre.InnerException is WebException:
                case HttpRequestException _:
                    return "Could not reach BINA Cloud. Please check your internet connection and try again.";
                default:
                    return $"An unexpected error occurred while contacting BINA Cloud: {ex.Message}";
            }
        }

        public static string FriendlyForStatus(HttpStatusCode status)
        {
            int code = (int)status;
            if (code == 401) return "Your session has expired. Please sign in again.";
            if (code == 403) return "You don't have permission to perform this action on the current BINA project.";
            if (code == 404) return "The requested item was not found on BINA Cloud.";
            if (code >= 500) return "BINA Cloud is temporarily unavailable. Please try again in a few minutes.";
            return $"BINA Cloud returned an unexpected response (HTTP {code}).";
        }
    }
}
