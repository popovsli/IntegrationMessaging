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

        var endpoint = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            return await db.IntegrationEndpoints
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.IntegrationSystemCode == systemCode
                      && e.MessageTypeName == messageTypeName,
                    ct);
        });

        if (endpoint is null)
            throw new IntegrationMessagingException(
                $"No endpoint configured for system='{systemCode}', type='{messageTypeName}'. " +
                "Add a row to the IntegrationEndpoint table.");

        var resolvedPath = endpoint.EndpointPath
            .Replace("{EntityId}", entityId.ToString(), StringComparison.OrdinalIgnoreCase);

        return new EndpointResolution(resolvedPath, endpoint.HttpMethod, endpoint.SoapAction);
    }
}
