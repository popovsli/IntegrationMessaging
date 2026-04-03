// Services/CircuitBreaker/CircuitBreakerService.cs
// FIX #3 (carried forward): lock-guarded failure counter.
// FIX NEW-A: cache.Remove in IsOpen moved inside lock so it cannot race
//            with RecordFailure reading the counter.
// FIX NEW-B: Half-open probe support — after the break duration elapses,
//            one attempt is allowed through (probe) before fully closing.
//            On probe success → Close; on probe failure → extend break.

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
    private readonly Lock _lock = new();

    private static string FailureKey(string code) => $"cb:failures:{code}";
    private static string OpenUntilKey(string code) => $"cb:openuntil:{code}";
    private static string ProbeKey(string code) => $"cb:probe:{code}";

    /// <summary>
    /// Returns true when the circuit is fully open (within break window AND
    /// a probe has already been dispatched).
    /// Returns false when the window has expired OR the circuit is closed
    /// OR this is the designated probe attempt.
    /// </summary>
    public bool IsOpen(string systemCode)
    {
        lock (_lock)
        {
            if (!cache.TryGetValue(OpenUntilKey(systemCode), out DateTime openUntil))
                return false;                   // circuit closed

            if (DateTime.UtcNow >= openUntil)
            {
                // Break duration elapsed — allow exactly one probe through
                if (!cache.TryGetValue(ProbeKey(systemCode), out _))
                {
                    cache.Set(ProbeKey(systemCode), true,
                        new MemoryCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow =
                                TimeSpan.FromSeconds(30)    // probe window
                        });

                    logger.LogInformation(
                        "Circuit HALF-OPEN probe dispatched for {SystemCode}.",
                        systemCode);
                    return false;               // let the probe through
                }
                return true;                    // probe already in-flight — block
            }

            return true;                        // still within break window
        }
    }

    public void RecordSuccess(string systemCode)
    {
        lock (_lock)
        {
            var wasProbing = cache.TryGetValue(ProbeKey(systemCode), out _);
            cache.Remove(FailureKey(systemCode));
            cache.Remove(OpenUntilKey(systemCode));
            cache.Remove(ProbeKey(systemCode));

            if (wasProbing)
                logger.LogInformation(
                    "Circuit CLOSED for {SystemCode} after successful probe.",
                    systemCode);
        }
    }

    public void RecordFailure(string systemCode, IntegrationSystem system)
    {
        lock (_lock)
        {
            // If failing during a probe — extend the break duration
            bool wasProbing = cache.TryGetValue(ProbeKey(systemCode), out _);
            cache.Remove(ProbeKey(systemCode));

            var failures = cache.TryGetValue(FailureKey(systemCode), out int existing)
                ? existing + 1
                : 1;

            cache.Set(FailureKey(systemCode), failures,
                new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(10)
                });

            if (wasProbing || failures >= system.CircuitFailureThreshold)
            {
                var openUntil = DateTime.UtcNow
                    .AddSeconds(system.CircuitBreakDurationSeconds);
                cache.Set(OpenUntilKey(systemCode), openUntil);

                logger.LogWarning(
                    "Circuit {Action} for {SystemCode} [{SystemName}] " +
                    "after {Failures} failures. Suspended until {OpenUntil:u}.",
                    wasProbing ? "RE-OPENED (probe failed)" : "OPENED",
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
            cache.Remove(ProbeKey(systemCode));
        }

        logger.LogInformation(
            "Circuit manually RESET for system {SystemCode}.", systemCode);
    }
}
