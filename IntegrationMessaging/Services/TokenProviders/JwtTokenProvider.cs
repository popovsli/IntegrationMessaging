// Services/TokenProviders/JwtTokenProvider.cs
// FIX NEW-D: Zero-TTL guard — if ExpiresIn <= TokenSkewSeconds, the computed
//            expiry would be zero which means no caching; now logs a warning
//            and uses a 30-second floor so the app does not hammer the auth
//            endpoint on every message.
// FIX NEW-E: Auth HTTP client registered with AddStandardResilienceHandler
//            so transient failures on the token endpoint are retried.

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
    // 30-second floor prevents hammering the token endpoint when the server
    // returns an ExpiresIn that is smaller than TokenSkewSeconds.
    private static readonly TimeSpan MinimumCacheTtl = TimeSpan.FromSeconds(30);

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")]
        string AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        int ExpiresIn);

    public async Task<string> GetTokenAsync(
        IntegrationSystem system,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(system.AuthUrl))
            throw new IntegrationMessagingException(
                $"JwtTokenProvider requires AuthUrl for " +
                $"system '{system.IntegrationSystemCode}'.");

        var cacheKey = $"token:{system.IntegrationSystemCode}";

        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        logger.LogDebug(
            "Fetching new token for {SystemCode}.",
            system.IntegrationSystemCode);

        var password = string.IsNullOrWhiteSpace(system.PasswordEncrypted)
            ? string.Empty
            : encryptor.Decrypt(system.PasswordEncrypted);

        var body = JsonSerializer.Serialize(
            new { username = system.UserName, password });

        // "IntegrationMessaging.Auth" is registered with AddStandardResilienceHandler
        var client = httpFactory.CreateClient("IntegrationMessaging.Auth");

        using var response = await client.PostAsync(
            system.AuthUrl,
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new IntegrationMessagingException(
                $"Token request failed for '{system.IntegrationSystemCode}': " +
                $"HTTP {(int)response.StatusCode} — " +
                $"{content[..Math.Min(500, content.Length)]}");

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IntegrationMessagingException(
                $"Invalid token response for '{system.IntegrationSystemCode}'.");

        var rawExpiry = TimeSpan.FromSeconds(
            Math.Max(0, tokenResponse.ExpiresIn - system.TokenSkewSeconds));

        // FIX NEW-D: floor prevents caching with TTL=0
        if (rawExpiry < MinimumCacheTtl)
        {
            logger.LogWarning(
                "Token expiry for {SystemCode} is {Raw}s after skew " +
                "(ExpiresIn={ExpiresIn}, Skew={Skew}). " +
                "Using floor of {Floor}s. " +
                "Consider reducing TokenSkewSeconds in the database.",
                system.IntegrationSystemCode,
                rawExpiry.TotalSeconds,
                tokenResponse.ExpiresIn,
                system.TokenSkewSeconds,
                MinimumCacheTtl.TotalSeconds);
        }

        var ttl = rawExpiry < MinimumCacheTtl ? MinimumCacheTtl : rawExpiry;

        cache.Set(cacheKey, tokenResponse.AccessToken,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            });

        logger.LogDebug(
            "Token cached for {SystemCode} | TTL={Seconds}s.",
            system.IntegrationSystemCode,
            ttl.TotalSeconds);

        return tokenResponse.AccessToken;
    }
}
