using IntegrationMessaging.Entities;

namespace IntegrationMessaging.Services.TokenProviders;

public interface ITokenProvider
{
    Task<string> GetTokenAsync(IntegrationSystem system, CancellationToken ct = default);
}
