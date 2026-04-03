using IntegrationMessaging.Models;

namespace IntegrationMessaging.Services;

public interface IMessageDispatcher
{
    Task<IntegrationResponse> DispatchAsync(SendContext context, CancellationToken ct = default);
}
