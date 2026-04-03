// Services/EndpointResolver.cs
// FIX #4: null DB miss is no longer cached.
//         GetOrCreateAsync is replaced with explicit cache-aside logic
//         so a newly inserted endpoint row becomes visible without
//         waiting for the TTL to expire.

using IntegrationMessaging.Configuration;
using IntegrationMessaging.Data;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace IntegrationMessaging.Services;

public sealed class EndpointResolver(
    IntegrationDbContext db,
    IMemoryCache cache,
    IOptions<IntegrationMessagingOptions> options) : IEndpointResolver
{
    public async Task<EndpointResolution> ResolveAsync(
        string systemCode,
        string messageTypeName,
        int entityId,
        CancellationToken ct = default)
    {
        var cacheKey = $"endpoint:{systemCode}:{messageTypeName}";
        var ttl = TimeSpan.FromMinutes(options.Value.EndpointCacheMinutes);

        // FIX #4: check cache first; only write to cache on a successful DB hit
        if (cache.TryGetValue(cacheKey, out EndpointResolution? cached) && cached is not null)
            return cached;

        var endpoint = await db.IntegrationEndpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.IntegrationSystemCode == systemCode
                  && e.MessageTypeName == messageTypeName, ct);

        if (endpoint is null)
            throw new IntegrationMessagingException(
                $"No endpoint configured for system='{systemCode}', " +
                $"type='{messageTypeName}'. " +
                "Add a row to the IntegrationEndpoint table.");

        var resolvedPath = endpoint.EndpointPath
            .Replace("{EntityId}", entityId.ToString(),
                StringComparison.OrdinalIgnoreCase);

        var resolution = new EndpointResolution(
            resolvedPath, endpoint.HttpMethod, endpoint.SoapAction);

        // FIX #4: only cache on success — a null miss never enters the cache
        cache.Set(cacheKey, resolution, ttl);

        return resolution;
    }
}
