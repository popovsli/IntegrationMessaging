using IntegrationMessaging.Entities;
using IntegrationMessaging.Models;

namespace IntegrationMessaging.Services.Clients;

public interface IIntegrationClient
{
    Task<IntegrationResponse> SendAsync(
       IntegrationRequest request,
       IntegrationSystem system,
       CancellationToken ct = default);
}
