using IntegrationMessaging.Entities;
using IntegrationMessaging.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace IntegrationMessaging.Services.TokenProviders;

public sealed class JwtTokenProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<JwtTokenProvider> logger) : ITokenProvider
{
    private sealed record TokenResponse(string AccessToken, int ExpiresIn);

    public async Task<string> GetTokenAsync(IntegrationSystem system, CancellationToken ct = default)
    {
        var cacheKey = $"token:{system.IntegrationSystemCode}";

        if (cache.TryGetValue(cacheKey, out string? cachedToken) && cachedToken is not null)
            return cachedToken;

        logger.LogDebug("Fetching new token for system {SystemCode}.", system.IntegrationSystemCode);

        var client  = httpFactory.CreateClient("IntegrationMessaging.Auth");
        var authUrl = $"{system.BaseAddress.TrimEnd('/')}/{system.AuthEndpointPath.TrimStart('/')}";

        var body = JsonSerializer.Serialize(new
        {
            username = system.UserName,
            password = system.PasswordSecret
        });

        using var response = await client.PostAsync(
            authUrl,
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new IntegrationMessagingException(
                $"Token request failed for '{system.IntegrationSystemCode}': " +
                $"HTTP {(int)response.StatusCode} — {content[..Math.Min(500, content.Length)]}");

        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
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
