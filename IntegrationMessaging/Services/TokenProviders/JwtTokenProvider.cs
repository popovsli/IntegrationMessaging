// Services/TokenProviders/JwtTokenProvider.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Services.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace IntegrationMessaging.Services.TokenProviders;

public sealed class JwtTokenProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    IPasswordEncryptor encryptor,
    ILogger<JwtTokenProvider> logger) : ITokenProvider
{
    private sealed record TokenResponse(string AccessToken, int ExpiresIn);

    public async Task<string> GetTokenAsync(
        IntegrationSystem system, CancellationToken ct = default)
    {
        // Guard: auth URL must be configured for JWT systems
        if (string.IsNullOrWhiteSpace(system.AuthUrl))
            throw new IntegrationMessagingException(
                $"JwtTokenProvider requires AuthUrl to be set for " +
                $"system '{system.IntegrationSystemCode}'. " +
                $"Set IntegrationSystem.AuthUrl to the full token endpoint URL.");

        var cacheKey = $"token:{system.IntegrationSystemCode}";

        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        logger.LogDebug("Fetching new token for system {SystemCode}.", system.IntegrationSystemCode);

        // AuthUrl is absolute — used directly, never combined with BaseAddress
        var authUrl = system.AuthUrl;

        // Decrypt password at call time — never stored in memory long-term
        var password = string.IsNullOrWhiteSpace(system.PasswordEncrypted)
            ? string.Empty
            : encryptor.Decrypt(system.PasswordEncrypted);

        var body = JsonSerializer.Serialize(new
        {
            username = system.UserName,
            password
        });

        var client = httpFactory.CreateClient("IntegrationMessaging.Auth");

        using var response = await client.PostAsync(
            authUrl,
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new IntegrationMessagingException(
                $"Token request failed for '{system.IntegrationSystemCode}': " +
                $"HTTP {(int)response.StatusCode} — {content[..Math.Min(500, content.Length)]}");

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(
            content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IntegrationMessagingException(
                $"Invalid token response for '{system.IntegrationSystemCode}'.");

        var expiry = TimeSpan.FromSeconds(
            Math.Max(0, tokenResponse.ExpiresIn - system.TokenSkewSeconds));

        cache.Set(cacheKey, tokenResponse.AccessToken,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiry });

        logger.LogDebug("Token cached for {SystemCode} for {Seconds}s.",
            system.IntegrationSystemCode, expiry.TotalSeconds);

        return tokenResponse.AccessToken;
    }
}