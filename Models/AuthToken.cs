using System;
using System.Text.Json.Serialization;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents an OAuth 2.0 authentication token from Autodesk APS
    /// Contains all information needed for authenticated API calls
    /// and token refresh operations
    /// </summary>
    public class AuthToken
    {
        #region Token Information

        /// <summary>
        /// The access token used for API authentication
        /// This is the actual token sent in Authorization headers
        /// </summary>
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        /// <summary>
        /// Token used to refresh the access token when it expires
        /// Not all OAuth flows provide refresh tokens
        /// </summary>
        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; }

        /// <summary>
        /// Type of token (usually "Bearer")
        /// </summary>
        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = "Bearer";

        /// <summary>
        /// Scope(s) that this token is valid for
        /// Space-separated list of permissions
        /// </summary>
        [JsonPropertyName("scope")]
        public string Scope { get; set; }

        #endregion

        #region Expiration Information

        /// <summary>
        /// When this token expires (UTC time)
        /// Calculated from expires_in when token is received
        /// </summary>
        [JsonPropertyName("expires_at")]
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// Original expires_in value from OAuth response (in seconds)
        /// Used for reference and debugging
        /// </summary>
        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        #endregion

        #region Metadata

        /// <summary>
        /// When this token was created/received
        /// </summary>
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this token was last used for an API call
        /// Useful for tracking token usage
        /// </summary>
        [JsonPropertyName("last_used_at")]
        public DateTime? LastUsedAt { get; set; }

        /// <summary>
        /// Number of times this token has been used
        /// Useful for debugging and monitoring
        /// </summary>
        [JsonPropertyName("use_count")]
        public int UseCount { get; set; } = 0;

        /// <summary>
        /// Client ID associated with this token
        /// Useful for multi-client scenarios
        /// </summary>
        [JsonPropertyName("client_id")]
        public string ClientId { get; set; }

        #endregion

        #region Computed Properties

        /// <summary>
        /// Checks if the token is currently expired
        /// Includes a small buffer to account for network latency
        /// </summary>
        [JsonIgnore]
        public bool IsExpired
        {
            get
            {
                // Add 30-second buffer to prevent expiration during API calls
                var expirationWithBuffer = ExpiresAt.AddSeconds(-30);
                return DateTime.UtcNow >= expirationWithBuffer;
            }
        }

        /// <summary>
        /// Checks if the token will expire soon (within 5 minutes)
        /// Useful for proactive token refresh
        /// </summary>
        [JsonIgnore]
        public bool IsExpiringSoon
        {
            get
            {
                var soonThreshold = ExpiresAt.AddMinutes(-5);
                return DateTime.UtcNow >= soonThreshold;
            }
        }

        /// <summary>
        /// Gets time remaining until expiration
        /// </summary>
        [JsonIgnore]
        public TimeSpan TimeUntilExpiration
        {
            get
            {
                var remaining = ExpiresAt - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Gets formatted string showing time until expiration
        /// </summary>
        [JsonIgnore]
        public string FormattedTimeUntilExpiration
        {
            get
            {
                var remaining = TimeUntilExpiration;
                
                if (remaining == TimeSpan.Zero)
                    return "Expired";

                if (remaining.TotalDays >= 1)
                    return $"{remaining.Days} days, {remaining.Hours} hours";
                else if (remaining.TotalHours >= 1)
                    return $"{remaining.Hours} hours, {remaining.Minutes} minutes";
                else
                    return $"{remaining.Minutes} minutes";
            }
        }

        /// <summary>
        /// Checks if this token has a refresh token available
        /// </summary>
        [JsonIgnore]
        public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);

        /// <summary>
        /// Gets the authorization header value for HTTP requests
        /// </summary>
        [JsonIgnore]
        public string AuthorizationHeaderValue => $"{TokenType} {AccessToken}";

        #endregion

        #region Methods

        /// <summary>
        /// Records that this token was used for an API call
        /// Updates usage statistics
        /// </summary>
        public void RecordUsage()
        {
            LastUsedAt = DateTime.UtcNow;
            UseCount++;
        }

        /// <summary>
        /// Validates that the token has required information
        /// </summary>
        /// <returns>Validation result</returns>
        public TokenValidationResult Validate()
        {
            var result = new TokenValidationResult();

            if (string.IsNullOrEmpty(AccessToken))
                result.Errors.Add("Access token is required");

            if (string.IsNullOrEmpty(TokenType))
                result.Errors.Add("Token type is required");

            if (ExpiresAt == default(DateTime))
                result.Errors.Add("Expiration time must be set");

            if (IsExpired)
                result.Errors.Add("Token is expired");

            if (string.IsNullOrEmpty(Scope))
                result.Warnings.Add("Token scope is not specified");

            if (CreatedAt == default(DateTime))
                result.Warnings.Add("Created date is not set");

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Creates a summary string for logging/debugging
        /// </summary>
        /// <returns>Token summary (without sensitive data)</returns>
        public string GetSummary()
        {
            return $"Token Type: {TokenType}\n" +
                   $"Scope: {Scope ?? "Not specified"}\n" +
                   $"Created: {CreatedAt:yyyy-MM-dd HH:mm:ss} UTC\n" +
                   $"Expires: {ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC\n" +
                   $"Status: {(IsExpired ? "Expired" : IsExpiringSoon ? "Expiring Soon" : "Valid")}\n" +
                   $"Time Remaining: {FormattedTimeUntilExpiration}\n" +
                   $"Use Count: {UseCount}\n" +
                   $"Can Refresh: {CanRefresh}\n" +
                   $"Access Token: {(string.IsNullOrEmpty(AccessToken) ? "None" : "***" + AccessToken.Substring(Math.Max(0, AccessToken.Length - 4)))}";
        }

        /// <summary>
        /// Creates a copy of this token for safe handling
        /// TODO: Consider if cloning is needed for your use case
        /// </summary>
        /// <returns>Copy of the token</returns>
        public AuthToken Clone()
        {
            return new AuthToken
            {
                AccessToken = AccessToken,
                RefreshToken = RefreshToken,
                TokenType = TokenType,
                Scope = Scope,
                ExpiresAt = ExpiresAt,
                ExpiresIn = ExpiresIn,
                CreatedAt = CreatedAt,
                LastUsedAt = LastUsedAt,
                UseCount = UseCount,
                ClientId = ClientId
            };
        }

        /// <summary>
        /// Clears sensitive data from the token
        /// Useful before logging or when disposing
        /// </summary>
        public void ClearSensitiveData()
        {
            AccessToken = null;
            RefreshToken = null;
        }

        #endregion

        #region Static Factory Methods

        /// <summary>
        /// Creates a token from OAuth response values
        /// </summary>
        /// <param name="accessToken">Access token value</param>
        /// <param name="tokenType">Token type (usually "Bearer")</param>
        /// <param name="expiresIn">Expiration time in seconds</param>
        /// <param name="scope">Token scope</param>
        /// <param name="refreshToken">Optional refresh token</param>
        /// <param name="clientId">Client ID that requested the token</param>
        /// <returns>New AuthToken instance</returns>
        public static AuthToken FromOAuthResponse(
            string accessToken,
            string tokenType,
            int expiresIn,
            string scope,
            string refreshToken = null,
            string clientId = null)
        {
            return new AuthToken
            {
                AccessToken = accessToken,
                TokenType = tokenType ?? "Bearer",
                ExpiresIn = expiresIn,
                ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn),
                Scope = scope,
                RefreshToken = refreshToken,
                ClientId = clientId,
                CreatedAt = DateTime.UtcNow
            };
        }

        #endregion
    }

    /// <summary>
    /// Result of token validation
    /// </summary>
    public class TokenValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();

        public bool HasWarnings => Warnings.Count > 0;
        public bool HasErrors => Errors.Count > 0;
    }
}