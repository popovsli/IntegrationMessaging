using System.ComponentModel.DataAnnotations;

namespace IntegrationMessaging.Configuration;

public sealed class IntegrationMessagingOptions
{
    public const string Section = "IntegrationMessaging";

    [Range(1, 1000, ErrorMessage = "BatchSize must be 1–1000.")]
    public int BatchSize { get; set; } = 50;

    [Range(1, 3600, ErrorMessage = "PollIntervalSeconds must be 1–3600.")]
    public int PollIntervalSeconds { get; set; } = 5;

    [Range(1, 60, ErrorMessage = "LockDurationMinutes must be 1–60.")]
    public int LockDurationMinutes { get; set; } = 5;

    [Range(1, 60, ErrorMessage = "EndpointCacheMinutes must be 1–60.")]
    public int EndpointCacheMinutes { get; set; } = 5;

    /// <summary>
    /// How many rows the stale-lock requeue step may unlock per tick.
    /// Prevents a single unbounded UPDATE after a mass crash.
    /// Default: 2× BatchSize.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "StaleRequeueLimit must be 1–10000.")]
    public int StaleRequeueLimit { get; set; } = 100;
}
