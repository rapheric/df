using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NCBA.DCL.Data;
using NCBA.DCL.Models;

namespace NCBA.DCL.Services;

/// <summary>
/// Service for handling Single Sign-On (SSO) operations
/// Supports OAuth2 and OpenID Connect providers
/// </summary>
public interface ISSOService
{
    Task<SSOProvider> CreateOrUpdateProviderAsync(SSOProvider provider);
    Task<List<SSOProvider>> GetEnabledProvidersAsync();
    Task<string> GenerateAuthorizationUrlAsync(Guid providerId, string redirectUri, string? state = null);
    Task<(bool Success, User? User, bool IsNewUser, string Message)> HandleCallbackAsync(Guid providerId, string code, string? state = null);
    Task<SSOConnection> LinkAccountAsync(Guid userId, Guid providerId, string providerUserId, string? providerEmail, string? providerName, string? profileData);
    Task<bool> UnlinkAccountAsync(Guid userId, Guid providerId);
    Task<List<SSOConnection>> GetUserConnectionsAsync(Guid userId);
    Task<SSOConnection?> GetConnectionByProviderUserIdAsync(Guid providerId, string providerUserId);
    Task<SSOLog> LogSSOAttemptAsync(Guid? userId, Guid providerId, string operation, bool isSuccess, string? errorMessage = null, string? ipAddress = null);
    Task<bool> ValidateAndRefreshTokenAsync(Guid connectionId);
}

public class SSOService : ISSOService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<SSOService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public SSOService(ApplicationDbContext context, ILogger<SSOService> logger, IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Creates or updates an SSO provider configuration
    /// </summary>
    public async Task<SSOProvider> CreateOrUpdateProviderAsync(SSOProvider provider)
    {
        try
        {
            var existingProvider = await _context.SSOProviders
                .FirstOrDefaultAsync(p => p.ProviderName == provider.ProviderName);

            if (existingProvider != null)
            {
                existingProvider.DisplayName = provider.DisplayName ?? existingProvider.DisplayName;
                existingProvider.ProviderType = provider.ProviderType;
                existingProvider.ClientId = provider.ClientId;
                existingProvider.ClientSecret = EncryptSecret(provider.ClientSecret);
                existingProvider.Authority = provider.Authority;
                existingProvider.TokenEndpoint = provider.TokenEndpoint;
                existingProvider.AuthorizationEndpoint = provider.AuthorizationEndpoint;
                existingProvider.UserInfoEndpoint = provider.UserInfoEndpoint;
                existingProvider.RedirectUri = provider.RedirectUri;
                existingProvider.Scopes = provider.Scopes;
                existingProvider.IsEnabled = provider.IsEnabled;
                existingProvider.IconUrl = provider.IconUrl;
                existingProvider.DisplayOrder = provider.DisplayOrder;
                existingProvider.UpdatedAt = DateTime.UtcNow;

                _context.SSOProviders.Update(existingProvider);
            }
            else
            {
                provider.ClientSecret = EncryptSecret(provider.ClientSecret);
                _context.SSOProviders.Add(provider);
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("SSO Provider {ProviderName} created/updated", provider.ProviderName);
            return provider;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating/updating SSO provider");
            throw;
        }
    }

    /// <summary>
    /// Gets all enabled SSO providers
    /// </summary>
    public async Task<List<SSOProvider>> GetEnabledProvidersAsync()
    {
        return await _context.SSOProviders
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.DisplayOrder)
            .ToListAsync();
    }

    /// <summary>
    /// Generates OAuth2/OIDC authorization URL
    /// </summary>
    public async Task<string> GenerateAuthorizationUrlAsync(Guid providerId, string redirectUri, string? state = null)
    {
        try
        {
            var provider = await _context.SSOProviders.FindAsync(providerId);
            if (provider == null)
                throw new InvalidOperationException("Provider not found");

            state ??= GenerateRandomState();

            var parameters = new Dictionary<string, string>
            {
                { "client_id", provider.ClientId },
                { "redirect_uri", redirectUri },
                { "response_type", "code" },
                { "scope", provider.Scopes ?? "openid profile email" },
                { "state", state }
            };

            // For PKCE support (recommended for native apps)
            var codeChallenge = GenerateCodeChallenge();
            parameters["code_challenge"] = codeChallenge;
            parameters["code_challenge_method"] = "S256";

            var authorizationUrl = provider.AuthorizationEndpoint ?? provider.Authority + "/authorize";
            var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

            return $"{authorizationUrl}?{query}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating authorization URL for provider {ProviderId}", providerId);
            throw;
        }
    }

    /// <summary>
    /// Handles OAuth2/OIDC callback
    /// </summary>
    public async Task<(bool Success, User? User, bool IsNewUser, string Message)> HandleCallbackAsync(Guid providerId, string code, string? state = null)
    {
        try
        {
            var provider = await _context.SSOProviders.FindAsync(providerId);
            if (provider == null)
                return (false, null, false, "Provider not found");

            // Exchange code for token
            var tokenEndpoint = provider.TokenEndpoint ?? provider.Authority + "/token";
            using (var client = _httpClientFactory.CreateClient())
            {
                var tokenRequest = new Dictionary<string, string>
                {
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "client_id", provider.ClientId },
                    { "client_secret", DecryptSecret(provider.ClientSecret) },
                    { "redirect_uri", provider.RedirectUri ?? "" }
                };

                var response = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(tokenRequest));
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to exchange code for token from provider {ProviderId}", providerId);
                    return (false, null, false, "Failed to obtain token from provider");
                }

                var tokenContent = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(tokenContent))
                {
                    var root = doc.RootElement;
                    var accessToken = root.GetProperty("access_token").GetString();

                        if (string.IsNullOrWhiteSpace(accessToken))
                        {
                            _logger.LogWarning("Provider {ProviderId} returned an empty access token", providerId);
                            return (false, null, false, "Provider did not return a valid access token");
                        }

                    // Get user info
                    var userInfoEndpoint = provider.UserInfoEndpoint ?? provider.Authority + "/userinfo";
                    var userInfoRequest = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
                    userInfoRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                    var userInfoResponse = await client.SendAsync(userInfoRequest);
                    if (!userInfoResponse.IsSuccessStatusCode)
                    {
                        return (false, null, false, "Failed to obtain user information from provider");
                    }

                    var userInfoContent = await userInfoResponse.Content.ReadAsStringAsync();
                    using (var userDoc = JsonDocument.Parse(userInfoContent))
                    {
                        var userRoot = userDoc.RootElement;
                        var providerUserId = userRoot.GetProperty("sub").GetString() ?? userRoot.GetProperty("id").GetString();
                        var email = userRoot.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
                        var name = userRoot.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

                        // Check if connection exists
                        var connection = await GetConnectionByProviderUserIdAsync(providerId, providerUserId ?? "");
                        if (connection != null)
                        {
                            // Update tokens
                            connection.AccessToken = EncryptSecret(accessToken);
                            connection.UpdatedAt = DateTime.UtcNow;
                            _context.SSOConnections.Update(connection);
                            await _context.SaveChangesAsync();

                            // Log successful attempt
                            await LogSSOAttemptAsync(connection.UserId, providerId, "Login", true);

                            return (true, connection.User, false, "Login successful");
                        }

                        // New user via SSO
                        if (email == null)
                            return (false, null, false, "Email not provided by SSO provider");

                        // Check if user exists by email
                        var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
                        if (existingUser != null)
                        {
                            // Link SSO account to existing user
                            return (true, existingUser, false, "User already exists, SSO account linked");
                        }

                        // Create new user
                        var newUser = new User
                        {
                            Id = Guid.NewGuid(),
                            Email = email,
                            Name = name ?? email.Split('@')[0],
                            Password = HashPassword(Guid.NewGuid().ToString()), // Random password
                            Role = UserRole.Admin,
                            Active = true,
                            CreatedAt = DateTime.UtcNow
                        };

                        _context.Users.Add(newUser);

                        // Create SSO connection
                        var newConnection = new SSOConnection
                        {
                            Id = Guid.NewGuid(),
                            UserId = newUser.Id,
                            SSOProviderId = providerId,
                            ProviderUserId = providerUserId ?? "",
                            ProviderEmail = email,
                            ProviderName = name,
                            ProviderProfileData = userInfoContent,
                            AccessToken = EncryptSecret(accessToken),
                            CreatedAt = DateTime.UtcNow
                        };

                        _context.SSOConnections.Add(newConnection);
                        await _context.SaveChangesAsync();

                        // Log successful attempt
                        await LogSSOAttemptAsync(newUser.Id, providerId, "NewUserCreate", true);

                        return (true, newUser, true, "New user created via SSO");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SSO callback for provider {ProviderId}", providerId);
            return (false, null, false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Links an SSO account to an existing user
    /// </summary>
    public async Task<SSOConnection> LinkAccountAsync(Guid userId, Guid providerId, string providerUserId, string? providerEmail, string? providerName, string? profileData)
    {
        try
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
                throw new InvalidOperationException("User not found");

            // Check if connection already exists
            var existingConnection = await _context.SSOConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.SSOProviderId == providerId);

            if (existingConnection != null)
            {
                existingConnection.ProviderUserId = providerUserId;
                existingConnection.ProviderEmail = providerEmail;
                existingConnection.ProviderName = providerName;
                existingConnection.ProviderProfileData = profileData;
                existingConnection.UpdatedAt = DateTime.UtcNow;

                _context.SSOConnections.Update(existingConnection);
                await _context.SaveChangesAsync();

                await LogSSOAttemptAsync(userId, providerId, "LinkAccount", true);
                return existingConnection;
            }

            var connection = new SSOConnection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SSOProviderId = providerId,
                ProviderUserId = providerUserId,
                ProviderEmail = providerEmail,
                ProviderName = providerName,
                ProviderProfileData = profileData,
                CreatedAt = DateTime.UtcNow
            };

            _context.SSOConnections.Add(connection);
            await _context.SaveChangesAsync();

            await LogSSOAttemptAsync(userId, providerId, "LinkAccount", true);
            _logger.LogInformation("SSO account linked for user {UserId}", userId);

            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error linking SSO account for user {UserId}", userId);
            await LogSSOAttemptAsync(userId, providerId, "LinkAccount", false, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Unlinks an SSO account from a user
    /// </summary>
    public async Task<bool> UnlinkAccountAsync(Guid userId, Guid providerId)
    {
        try
        {
            var connection = await _context.SSOConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.SSOProviderId == providerId);

            if (connection == null)
                return false;

            connection.IsActive = false;
            connection.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await LogSSOAttemptAsync(userId, providerId, "UnlinkAccount", true);

            _logger.LogInformation("SSO account unlinked for user {UserId}", userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unlinking SSO account for user {UserId}", userId);
            await LogSSOAttemptAsync(userId, providerId, "UnlinkAccount", false, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Gets all SSO connections for a user
    /// </summary>
    public async Task<List<SSOConnection>> GetUserConnectionsAsync(Guid userId)
    {
        return await _context.SSOConnections
            .Where(c => c.UserId == userId && c.IsActive)
            .Include(c => c.Provider)
            .ToListAsync();
    }

    /// <summary>
    /// Gets a connection by provider user ID
    /// </summary>
    public async Task<SSOConnection?> GetConnectionByProviderUserIdAsync(Guid providerId, string providerUserId)
    {
        return await _context.SSOConnections
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.SSOProviderId == providerId && c.ProviderUserId == providerUserId && c.IsActive);
    }

    /// <summary>
    /// Logs SSO authentication attempts
    /// </summary>
    public async Task<SSOLog> LogSSOAttemptAsync(Guid? userId, Guid providerId, string operation, bool isSuccess, string? errorMessage = null, string? ipAddress = null)
    {
        try
        {
            var log = new SSOLog
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SSOProviderId = providerId,
                Operation = operation,
                IsSuccess = isSuccess,
                ErrorMessage = errorMessage,
                IpAddress = ipAddress,
                CreatedAt = DateTime.UtcNow
            };

            _context.SSOLogs.Add(log);
            await _context.SaveChangesAsync();

            return log;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging SSO attempt");
            throw;
        }
    }

    /// <summary>
    /// Validates and refreshes SSO tokens if needed
    /// </summary>
    public async Task<bool> ValidateAndRefreshTokenAsync(Guid connectionId)
    {
        try
        {
            var connection = await _context.SSOConnections.FindAsync(connectionId);
            if (connection == null)
                return false;

            // Check if token is expired
            if (connection.TokenExpiresAt == null || DateTime.UtcNow < connection.TokenExpiresAt)
                return true;

            // Refresh token if available
            if (string.IsNullOrWhiteSpace(connection.RefreshToken))
                return false;

            var provider = await _context.SSOProviders.FindAsync(connection.SSOProviderId);
            if (provider == null)
                return false;

            // Call provider's token refresh endpoint
            var tokenEndpoint = provider.TokenEndpoint ?? provider.Authority + "/token";
            using (var client = _httpClientFactory.CreateClient())
            {
                var refreshRequest = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", DecryptSecret(connection.RefreshToken) },
                    { "client_id", provider.ClientId },
                    { "client_secret", DecryptSecret(provider.ClientSecret) }
                };

                var response = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(refreshRequest));
                if (!response.IsSuccessStatusCode)
                    return false;

                var content = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(content))
                {
                    var root = doc.RootElement;
                    var accessToken = root.GetProperty("access_token").GetString();
                    var expiresIn = root.TryGetProperty("expires_in", out var expiresProp) ? expiresProp.GetInt32() : 3600;

                        if (string.IsNullOrWhiteSpace(accessToken))
                        {
                            _logger.LogWarning("Provider refresh for connection {ConnectionId} returned an empty access token", connectionId);
                            return false;
                        }

                    connection.AccessToken = EncryptSecret(accessToken);
                    connection.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                    connection.UpdatedAt = DateTime.UtcNow;

                    _context.SSOConnections.Update(connection);
                    await _context.SaveChangesAsync();

                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating/refreshing SSO token for connection {ConnectionId}", connectionId);
            return false;
        }
    }

    // ============ Helper Methods ============

    private string GenerateRandomState(int length = 32)
    {
        var random = new byte[length];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(random);
        }
        return Convert.ToBase64String(random);
    }

    private string GenerateCodeChallenge(int length = 32)
    {
        var random = new byte[length];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(random);
        }
        using (var sha256 = SHA256.Create())
        {
            var hashedBytes = sha256.ComputeHash(random);
            return Convert.ToBase64String(hashedBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    private string EncryptSecret(string secret)
    {
        // In production, use proper encryption (AES-256)
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
    }

    private string DecryptSecret(string encrypted)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encrypted));
        }
        catch
        {
            return encrypted;
        }
    }

    private string HashPassword(string password)
    {
        using (var sha256 = SHA256.Create())
        {
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hashedBytes);
        }
    }
}
