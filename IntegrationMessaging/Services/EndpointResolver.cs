// Services/EndpointResolver.cs
// FIX #4 (carried forward): null miss never cached.
// NEW: Invalidate(systemCode, messageTypeName) exposes manual cache eviction so
//      an operator updating an IntegrationEndpoint row can flush immediately
//      without waiting for the TTL.

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
    private static string CacheKey(string systemCode, string typeName) =>
        $"endpoint:{systemCode}:{typeName}";

    public async Task<EndpointResolution> ResolveAsync(
        string systemCode,
        string messageTypeName,
        int entityId,
        CancellationToken ct = default)
    {
        var key = CacheKey(systemCode, messageTypeName);
        var ttl = TimeSpan.FromMinutes(options.Value.EndpointCacheMinutes);

        if (cache.TryGetValue(key, out EndpointResolution? cached) && cached is not null)
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
            resolvedPath,
            endpoint.HttpMethod,
            endpoint.SoapAction);

        cache.Set(key, resolution, ttl);
        return resolution;
    }

    public void Invalidate(string systemCode, string messageTypeName) =>
        cache.Remove(CacheKey(systemCode, messageTypeName));
}
