using System.Text.Json;
using System.Text.Json.Serialization;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.App.Services;

/// <summary>
/// Interface for Bangumi authentication service.
/// Supports OAuth flow and API key authentication.
/// </summary>
public interface IBangumiAuthService
{
    /// <summary>
    /// Gets whether the user is currently authenticated.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Gets the current access token (if authenticated).
    /// </summary>
    string? AccessToken { get; }

    /// <summary>
    /// Gets the authenticated user's ID.
    /// </summary>
    string? UserId { get; }

    /// <summary>
    /// Gets the authenticated user's username.
    /// </summary>
    string? Username { get; }

    /// <summary>
    /// Initializes the service by loading stored credentials.
    /// Should be called after DI resolution.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the initialization</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initiates OAuth authentication flow.
    /// Returns the authorization URL to open in browser.
    /// </summary>
    /// <returns>Authorization URL for user to visit</returns>
    string GetOAuthAuthorizationUrl();

    /// <summary>
    /// Completes OAuth authentication by exchanging the authorization code.
    /// </summary>
    /// <param name="authorizationCode">Code received from OAuth callback</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Authentication result</returns>
    Task<BangumiAuthResult> CompleteOAuthAsync(string authorizationCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates using a user-provided API key.
    /// </summary>
    /// <param name="apiKey">The API key/access token</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Authentication result</returns>
    Task<BangumiAuthResult> AuthenticateWithApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the current authentication status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether the authentication is still valid</returns>
    Task<bool> ValidateAuthenticationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the access token (if using OAuth with refresh token).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Refresh result</returns>
    Task<BangumiAuthResult> RefreshTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs out and clears stored authentication data.
    /// </summary>
    Task LogoutAsync();

    /// <summary>
    /// Gets the user's collection/ratings from Bangumi.
    /// Requires authentication.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User's game collection</returns>
    Task<List<BangumiCollectionItem>> GetUserCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user's rating for a specific game.
    /// Requires authentication.
    /// </summary>
    /// <param name="subjectId">Bangumi subject ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>User's collection status and rating</returns>
    Task<BangumiCollectionItem?> GetUserRatingAsync(int subjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when authentication status changes.
    /// </summary>
    event EventHandler<BangumiAuthChangedEventArgs>? AuthChanged;
}

/// <summary>
/// Result of Bangumi authentication attempt.
/// </summary>
public class BangumiAuthResult
{
    /// <summary>
    /// Whether authentication was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Access token for API calls.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Refresh token (for OAuth).
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Token expiration time.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// User ID from Bangumi.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Username from Bangumi.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Error message if authentication failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Authentication method used.
    /// </summary>
    public BangumiAuthMethod AuthMethod { get; set; }
}

/// <summary>
/// Authentication method used.
/// </summary>
public enum BangumiAuthMethod
{
    OAuth,
    ApiKey
}

/// <summary>
/// Event args for authentication status changes.
/// </summary>
public class BangumiAuthChangedEventArgs : EventArgs
{
    public bool IsAuthenticated { get; }
    public string? UserId { get; }
    public string? Username { get; }

    public BangumiAuthChangedEventArgs(bool isAuthenticated, string? userId, string? username)
    {
        IsAuthenticated = isAuthenticated;
        UserId = userId;
        Username = username;
    }
}

/// <summary>
/// User's collection item from Bangumi.
/// </summary>
public class BangumiCollectionItem
{
    /// <summary>
    /// Subject (game) ID.
    /// </summary>
    public int SubjectId { get; set; }

    /// <summary>
    /// Subject name.
    /// </summary>
    public string? SubjectName { get; set; }

    /// <summary>
    /// Collection status (wish, collect, do, on_hold, dropped).
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// User's rating (0-10, or null if unrated).
    /// </summary>
    public double? Rate { get; set; }

    /// <summary>
    /// User's comment/tags.
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// When the collection entry was created.
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// Episode progress for series.
    /// </summary>
    public int? EpStatus { get; set; }
}

/// <summary>
/// Service for Bangumi authentication.
/// Implements OAuth and API key authentication with token storage.
/// </summary>
public class BangumiAuthService : IBangumiAuthService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BangumiAuthService> _logger;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    // OAuth configuration (these would typically come from config)
    private const string OAuthClientId = "bgm_galbox"; // Placeholder - needs real client ID
    private const string OAuthClientSecret = ""; // Placeholder - needs real client secret
    private const string OAuthRedirectUri = "galbox://bangumi/callback"; // Custom URI scheme

    // Bangumi OAuth endpoints
    private const string AuthorizeUrl = "https://bgm.tv/oauth/authorize";
    private const string TokenUrl = "https://bgm.tv/oauth/access_token";
    private const string ApiBaseUrl = "https://api.bgm.tv";

    // Token storage
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime? _expiresAt;
    private string? _userId;
    private string? _username;
    private bool _isAuthenticated;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a BangumiAuthService with injected dependencies.
    /// Note: Use InitializeAsync() after construction to load stored credentials.
    /// </summary>
    public BangumiAuthService(
        IServiceProvider serviceProvider,
        ILogger<BangumiAuthService> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Galbox/1.0");
    }

    /// <summary>
    /// Initializes the service by loading stored credentials.
    /// Should be called after DI resolution.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadStoredCredentialsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool IsAuthenticated => _isAuthenticated;

    /// <inheritdoc />
    public string? AccessToken => _accessToken;

    /// <inheritdoc />
    public string? UserId => _userId;

    /// <inheritdoc />
    public string? Username => _username;

    /// <inheritdoc />
    public event EventHandler<BangumiAuthChangedEventArgs>? AuthChanged;

    /// <inheritdoc />
    public string GetOAuthAuthorizationUrl()
    {
        // Build authorization URL
        // Note: This is a stub - real OAuth requires registered client with Bangumi
        var url = $"{AuthorizeUrl}?client_id={OAuthClientId}&response_type=code&redirect_uri={Uri.EscapeDataString(OAuthRedirectUri)}";

        _logger.LogInformation("Generated OAuth authorization URL");
        return url;
    }

    /// <inheritdoc />
    public async Task<BangumiAuthResult> CompleteOAuthAsync(string authorizationCode, CancellationToken cancellationToken = default)
    {
        var result = new BangumiAuthResult
        {
            AuthMethod = BangumiAuthMethod.OAuth
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Exchange authorization code for access token
            // Note: This is a stub - actual implementation requires valid OAuth client credentials
            _logger.LogWarning("OAuth flow not fully implemented - requires registered Bangumi OAuth client");

            // Stub: Simulate successful auth for development
            result.ErrorMessage = "OAuth flow requires registered client with Bangumi. Please use API Key authentication instead.";
            result.Success = false;

            /*
            // Real implementation would be:
            var tokenRequest = new
            {
                grant_type = "authorization_code",
                code = authorizationCode,
                client_id = OAuthClientId,
                client_secret = OAuthClientSecret,
                redirect_uri = OAuthRedirectUri
            };

            var response = await _httpClient.PostAsJsonAsync(TokenUrl, tokenRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<BangumiTokenResponse>(cancellationToken);

            if (tokenResponse != null)
            {
                result.Success = true;
                result.AccessToken = tokenResponse.AccessToken;
                result.RefreshToken = tokenResponse.RefreshToken;
                result.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

                // Store credentials
                await StoreCredentialsAsync(result, cancellationToken);

                // Get user info
                await FetchUserInfoAsync(result.AccessToken, cancellationToken);
            }
            */
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation cancelled";
        }
        catch (HttpRequestException ex)
        {
            result.ErrorMessage = $"Network error: {ex.Message}";
            _logger.LogError(ex, "OAuth token exchange failed");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Unexpected error: {ex.Message}";
            _logger.LogError(ex, "OAuth authentication failed");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<BangumiAuthResult> AuthenticateWithApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var result = new BangumiAuthResult
        {
            AuthMethod = BangumiAuthMethod.ApiKey
        };

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.ErrorMessage = "API key cannot be empty";
            return result;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Validate the API key by making a test request
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/v0/me");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                result.ErrorMessage = $"API key validation failed: {response.StatusCode}";
                result.Success = false;
                _logger.LogWarning("API key validation failed with status {StatusCode}", response.StatusCode);
                return result;
            }

            // Parse user info from response
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var userInfo = JsonSerializer.Deserialize<BangumiUserInfo>(content, JsonOptions);

            if (userInfo != null)
            {
                result.Success = true;
                result.AccessToken = apiKey;
                result.UserId = userInfo.Id?.ToString();
                result.Username = userInfo.Username;

                // Store credentials
                _accessToken = apiKey;
                _userId = result.UserId;
                _username = result.Username;
                _isAuthenticated = true;
                _expiresAt = null; // API keys typically don't expire

                await StoreCredentialsToDatabaseAsync(result, cancellationToken).ConfigureAwait(false);

                // Raise auth changed event
                AuthChanged?.Invoke(this, new BangumiAuthChangedEventArgs(true, _userId, _username));

                _logger.LogInformation("Successfully authenticated with API key for user {Username}", _username);
            }
            else
            {
                result.ErrorMessage = "Failed to parse user info from API response";
                result.Success = false;
            }
        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = "Operation cancelled";
        }
        catch (HttpRequestException ex)
        {
            result.ErrorMessage = $"Network error: {ex.Message}";
            _logger.LogError(ex, "API key validation failed");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Unexpected error: {ex.Message}";
            _logger.LogError(ex, "API key authentication failed");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        if (!_isAuthenticated || string.IsNullOrWhiteSpace(_accessToken))
        {
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if token has expired (for OAuth tokens)
            if (_expiresAt.HasValue && _expiresAt.Value <= DateTime.UtcNow)
            {
                _logger.LogInformation("Access token has expired");
                _isAuthenticated = false;
                AuthChanged?.Invoke(this, new BangumiAuthChangedEventArgs(false, _userId, _username));
                return false;
            }

            // Validate by making a test API call
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/v0/me");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning("Authentication validation failed with status {StatusCode}", response.StatusCode);
            _isAuthenticated = false;
            AuthChanged?.Invoke(this, new BangumiAuthChangedEventArgs(false, _userId, _username));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating authentication");
            _isAuthenticated = false;
            AuthChanged?.Invoke(this, new BangumiAuthChangedEventArgs(false, _userId, _username));
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<BangumiAuthResult> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        var result = new BangumiAuthResult();

        // API keys don't need refresh
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            result.ErrorMessage = "No refresh token available. API keys do not expire.";
            result.Success = true; // Consider valid if using API key
            return result;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Stub - would need actual OAuth client credentials
            result.ErrorMessage = "Token refresh requires registered OAuth client";
            result.Success = false;

            /*
            // Real implementation:
            var refreshRequest = new
            {
                grant_type = "refresh_token",
                refresh_token = _refreshToken,
                client_id = OAuthClientId,
                client_secret = OAuthClientSecret
            };

            var response = await _httpClient.PostAsJsonAsync(TokenUrl, refreshRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<BangumiTokenResponse>(cancellationToken);

            if (tokenResponse != null)
            {
                result.Success = true;
                result.AccessToken = tokenResponse.AccessToken;
                result.RefreshToken = tokenResponse.RefreshToken;
                result.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

                await StoreCredentialsAsync(result, cancellationToken);
            }
            */
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Token refresh failed: {ex.Message}";
            _logger.LogError(ex, "Token refresh failed");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task LogoutAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = null;
        _userId = null;
        _username = null;
        _isAuthenticated = false;

        // Clear stored credentials from database
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var settings = await dbContext.UserSettings.FirstOrDefaultAsync().ConfigureAwait(false);
            if (settings != null)
            {
                settings.BangumiAccessToken = null;
                settings.BangumiUserId = null;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing stored credentials");
        }

        AuthChanged?.Invoke(this, new BangumiAuthChangedEventArgs(false, null, null));
        _logger.LogInformation("User logged out from Bangumi");
    }

    /// <inheritdoc />
    public async Task<List<BangumiCollectionItem>> GetUserCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_isAuthenticated || string.IsNullOrWhiteSpace(_accessToken))
        {
            _logger.LogWarning("Cannot get collection - user not authenticated");
            return new List<BangumiCollectionItem>();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Get user's collection (type=4 for games)
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/v0/users/{_userId}/collections?type=4");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var collectionResponse = JsonSerializer.Deserialize<BangumiCollectionResponse>(content, JsonOptions);

            if (collectionResponse?.Data != null)
            {
                return collectionResponse.Data.Select(item => new BangumiCollectionItem
                {
                    SubjectId = item.Subject?.Id ?? 0,
                    SubjectName = item.Subject?.Name ?? item.Subject?.NameCn,
                    Type = item.Type,
                    Rate = item.Rate,
                    Comment = item.Comment,
                    CreatedAt = item.CreatedAt,
                    EpStatus = item.EpStatus
                }).ToList();
            }

            return new List<BangumiCollectionItem>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user collection");
            return new List<BangumiCollectionItem>();
        }
    }

    /// <inheritdoc />
    public async Task<BangumiCollectionItem?> GetUserRatingAsync(int subjectId, CancellationToken cancellationToken = default)
    {
        if (!_isAuthenticated || string.IsNullOrWhiteSpace(_accessToken))
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/v0/users/{_userId}/collections/{subjectId}");
            request.Headers.Add("Authorization", $"Bearer {_accessToken}");

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null; // User hasn't collected this subject
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var item = JsonSerializer.Deserialize<BangumiCollectionItemResponse>(content, JsonOptions);

            if (item != null)
            {
                return new BangumiCollectionItem
                {
                    SubjectId = subjectId,
                    SubjectName = item.Subject?.Name ?? item.Subject?.NameCn,
                    Type = item.Type,
                    Rate = item.Rate,
                    Comment = item.Comment,
                    CreatedAt = item.CreatedAt
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user rating for subject {SubjectId}", subjectId);
            return null;
        }
    }

    #region Private Methods

    private async Task LoadStoredCredentialsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var settings = await dbContext.UserSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (settings != null && !string.IsNullOrWhiteSpace(settings.BangumiAccessToken))
            {
                _accessToken = settings.BangumiAccessToken;
                _userId = settings.BangumiUserId;
                _isAuthenticated = true;

                // Validate the stored credentials
                if (await ValidateAuthenticationAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Loaded valid stored Bangumi credentials");
                }
                else
                {
                    _logger.LogWarning("Stored Bangumi credentials are invalid");
                    _isAuthenticated = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading stored Bangumi credentials");
        }
    }

    private async Task StoreCredentialsToDatabaseAsync(BangumiAuthResult result, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GalboxDbContext>();

            var settings = await dbContext.UserSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (settings == null)
            {
                settings = new UserSettings { Id = 1 };
                dbContext.UserSettings.Add(settings);
            }

            settings.BangumiAccessToken = result.AccessToken;
            settings.BangumiUserId = result.UserId;
            settings.LastModified = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Stored Bangumi credentials to database");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing Bangumi credentials");
        }
    }

    #endregion

    #region Response Models

    private class BangumiUserInfo
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("nickname")]
        public string? Nickname { get; set; }
    }

    private class BangumiTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }
    }

    private class BangumiCollectionResponse
    {
        [JsonPropertyName("data")]
        public List<BangumiCollectionItemResponse>? Data { get; set; }

        [JsonPropertyName("total")]
        public int Total { get; set; }
    }

    private class BangumiCollectionItemResponse
    {
        [JsonPropertyName("subject")]
        public BangumiCollectionSubject? Subject { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("rate")]
        public double? Rate { get; set; }

        [JsonPropertyName("comment")]
        public string? Comment { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("ep_status")]
        public int? EpStatus { get; set; }
    }

    private class BangumiCollectionSubject
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("name_cn")]
        public string? NameCn { get; set; }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes resources used by the service.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }

    #endregion
}