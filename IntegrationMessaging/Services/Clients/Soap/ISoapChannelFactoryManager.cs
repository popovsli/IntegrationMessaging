// Services/Clients/Soap/ISoapChannelFactoryManager.cs
using IntegrationMessaging.Entities;
using System.ServiceModel;

namespace IntegrationMessaging.Services.Clients.Soap;

public interface ISoapChannelFactoryManager<TContract>
    where TContract : class
{
    /// <summary>
    /// Returns a cached factory, rebuilds if BaseAddress/Username/Password
    /// changed in DB (fingerprint mismatch) or factory is faulted.
    /// </summary>
    ChannelFactory<TContract> GetOrCreate(
        IntegrationSystem system,
        Func<string, string, ChannelFactory<TContract>> factoryBuilder);

    void Invalidate(string integrationSystemCode);
}
