// Services/Clients/Soap/SoapChannelFactoryManager.cs
using IntegrationMessaging.Entities;
using IntegrationMessaging.Services.Security;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;

namespace IntegrationMessaging.Services.Clients.Soap;

public sealed class SoapChannelFactoryManager<TContract>(
    IPasswordEncryptor encryptor,
    ILogger<SoapChannelFactoryManager<TContract>> logger)
    : ISoapChannelFactoryManager<TContract>, IDisposable
    where TContract : class
{
    private record CachedEntry(
        ChannelFactory<TContract> Factory,
        string Fingerprint);

    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new();
    private readonly Lock _lock = new();

    public ChannelFactory<TContract> GetOrCreate(
        IntegrationSystem system,
        Func<string, string, ChannelFactory<TContract>> factoryBuilder)
    {
        var fingerprint = ComputeFingerprint(system);

        // Fast path — no lock needed if entry is valid
        if (_cache.TryGetValue(system.IntegrationSystemCode, out var entry) &&
            entry.Fingerprint == fingerprint &&
            IsHealthy(entry.Factory))
            return entry.Factory;

        lock (_lock)
        {
            // Re-check after acquiring lock
            if (_cache.TryGetValue(system.IntegrationSystemCode, out entry) &&
                entry.Fingerprint == fingerprint &&
                IsHealthy(entry.Factory))
                return entry.Factory;

            // Discard stale or faulted factory
            if (_cache.TryRemove(system.IntegrationSystemCode, out var stale))
            {
                TryAbort(stale.Factory);
                logger.LogInformation(
                    "SoapChannelFactoryManager<{Contract}>: factory for '{SystemCode}' " +
                    "discarded (config changed or faulted).",
                    typeof(TContract).Name, system.IntegrationSystemCode);
            }

            // Decrypt only at factory-build time
            var password = string.IsNullOrWhiteSpace(system.PasswordEncrypted)
                ? string.Empty
                : encryptor.Decrypt(system.PasswordEncrypted);

            var factory = factoryBuilder(system.UserName ?? string.Empty, password);

            _cache[system.IntegrationSystemCode] =
                new CachedEntry(factory, fingerprint);

            logger.LogInformation(
                "SoapChannelFactoryManager<{Contract}>: factory created for '{SystemCode}'.",
                typeof(TContract).Name, system.IntegrationSystemCode);

            return factory;
        }
    }

    public void Invalidate(string integrationSystemCode)
    {
        if (!_cache.TryRemove(integrationSystemCode, out var entry)) return;
        TryAbort(entry.Factory);
        logger.LogWarning(
            "SoapChannelFactoryManager<{Contract}>: factory for '{SystemCode}' invalidated.",
            typeof(TContract).Name, integrationSystemCode);
    }

    // Fingerprint covers the three DB values that affect factory construction
    private static string ComputeFingerprint(IntegrationSystem system)
    {
        var raw = $"{system.BaseAddress}|{system.UserName}|{system.PasswordEncrypted}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static bool IsHealthy(ChannelFactory<TContract> f) =>
        f.State is not CommunicationState.Faulted
               and not CommunicationState.Closed
               and not CommunicationState.Closing;

    private static void TryAbort(ChannelFactory<TContract> f)
    {
        try { f.Abort(); } catch { /* already dead */ }
    }

    public void Dispose()
    {
        foreach (var (_, entry) in _cache)
        {
            try { entry.Factory.Close(); }
            catch { TryAbort(entry.Factory); }
        }
        _cache.Clear();
    }
}