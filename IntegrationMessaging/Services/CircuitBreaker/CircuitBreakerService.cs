// Services/CircuitBreaker/CircuitBreakerService.cs
// FIX #3: Non-atomic read-increment-write replaced with lock-guarded
//         operations so concurrent workers produce a correct failure count.

using IntegrationMessaging.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace IntegrationMessaging.Services.CircuitBreaker;

/// <summary>
/// In-memory circuit breaker per IntegrationSystem.
/// State: failure counter + open-until timestamp stored in IMemoryCache.
/// Thread-safe: all mutations on the failure counter are lock-guarded.
/// </summary>
public sealed class CircuitBreakerService(
    IMemoryCache cache,
    ILogger<CircuitBreakerService> logger) : ICircuitBreakerService
{
    // FIX #3: single lock instance — guards all read-modify-write on failure counters
    private readonly Lock _lock = new();

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
        lock (_lock)
        {
            cache.Remove(FailureKey(systemCode));
            cache.Remove(OpenUntilKey(systemCode));
        }
    }

    public void RecordFailure(string systemCode, IntegrationSystem system)
    {
        lock (_lock)
        {
            // FIX #3: read and write under the same lock — no lost increments
            var failures = cache.TryGetValue(FailureKey(systemCode), out int existing)
                ? existing + 1
                : 1;

            cache.Set(FailureKey(systemCode), failures, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(10)
            });

            if (failures >= system.CircuitFailureThreshold)
            {
                var openUntil = DateTime.UtcNow
                    .AddSeconds(system.CircuitBreakDurationSeconds);

                cache.Set(OpenUntilKey(systemCode), openUntil);

                logger.LogWarning(
                    "Circuit OPENED for system {SystemCode} [{SystemName}] " +
                    "after {Failures} failures. Suspended until {OpenUntil:u}.",
                    systemCode, system.SystemName, failures, openUntil);
            }
        }
    }

    public void Reset(string systemCode)
    {
        lock (_lock)
        {
            cache.Remove(FailureKey(systemCode));
            cache.Remove(OpenUntilKey(systemCode));
        }

        logger.LogInformation("Circuit manually RESET for system {SystemCode}.", systemCode);
    }
}
