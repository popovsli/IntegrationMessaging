using IntegrationMessaging.Models;

namespace IntegrationMessaging.Services;

public interface IEndpointResolver
{
    Task<EndpointResolution> ResolveAsync(
        string integrationSystemCode,
        string messageTypeName,
        int    entityId,
        CancellationToken ct = default);
}
