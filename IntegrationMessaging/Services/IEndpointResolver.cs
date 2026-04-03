using IntegrationMessaging.Models;

namespace IntegrationMessaging.Services;

public interface IEndpointResolver
{
    Task<EndpointResolution> ResolveAsync(
        string integrationSystemCode,
        string messageTypeName,
        int    entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Evict the cached entry for (systemCode, messageTypeName) immediately.
    /// Call this after updating an IntegrationEndpoint row in the database
    /// so the next resolve picks up the change without waiting for the TTL.
    /// </summary>
    void Invalidate(string systemCode, string messageTypeName);
}
