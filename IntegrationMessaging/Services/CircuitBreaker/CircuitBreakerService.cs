using IntegrationMessaging.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace IntegrationMessaging.Services.CircuitBreaker;

/// <summary>
/// In-memory circuit breaker per IntegrationSystem.
/// State: failure counter + open-until timestamp stored in IMemoryCache.
/// </summary>
public sealed class CircuitBreakerService(
    IMemoryCache cache,
    ILogger<CircuitBreakerService> logger) : ICircuitBreakerService
{
    private static string FailureKey(string code) => $"cb:failures:{code}";
    private static string OpenUntilKey(string code) => $"cb:openuntil:{code}";

    public bool IsOpen(string systemCode)
    {
        if (!cache.TryGetValue(OpenUntilKey(systemCode), out DateTime openUntil))
            return false;

        if (DateTime.UtcNow < openUntil)
            return true;

        cache.Remove(OpenUntilKey(systemCode));
        return false;
    }

    public void RecordSuccess(string systemCode)
    {
        cache.Remove(FailureKey(systemCode));
        cache.Remove(OpenUntilKey(systemCode));
    }

    public void RecordFailure(string systemCode, IntegrationSystem system)
    {
        var failures = cache.GetOrCreate(FailureKey(systemCode), e =>
        {
            e.SlidingExpiration = TimeSpan.FromMinutes(10);
            return 0;
        });

        failures++;
        cache.Set(FailureKey(systemCode), failures,
            new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(10) });

        if (failures >= system.CircuitFailureThreshold)
        {
            var openUntil = DateTime.UtcNow.AddSeconds(system.CircuitBreakDurationSeconds);
            cache.Set(OpenUntilKey(systemCode), openUntil);
            logger.LogWarning(
                "Circuit OPENED for system {SystemCode} after {Failures} failures. Suspended until {OpenUntil:u}.",
                systemCode, failures, openUntil);
        }
    }

    public void Reset(string systemCode)
    {
        cache.Remove(FailureKey(systemCode));
        cache.Remove(OpenUntilKey(systemCode));
        logger.LogInformation("Circuit manually RESET for system {SystemCode}.", systemCode);
    }
}
