namespace IntegrationMessaging.Entities;

public class IntegrationSystem
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string IntegrationSystemCode { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    // ── Transport ─────────────────────────────────────────────────────────
    public string BaseAddress { get; set; } = string.Empty;
    public int ClientTimeoutSeconds { get; set; } = 30;
    public int ClientRetryCount { get; set; } = 3;

    // ── Credentials (used by SoapChannelFactoryManager + ITokenProvider) ──
    public string? UserName { get; set; }
    public string? PasswordEncrypted { get; set; }  // AES-256-GCM, Base64
    public int TokenSkewSeconds { get; set; } = 30;

    /// <summary>
    /// Full absolute URL of the authentication endpoint.
    /// Null for systems that use SOAP BasicAuth, API keys, or certificates
    /// — those don't need a separate auth call.
    public string? AuthUrl { get; set; } = string.Empty;

    // ── Queue retry policy ────────────────────────────────────────────────
    public int QueueMessageRetryDelaySeconds { get; set; } = 60;
    public int QueueMessageRetryCount { get; set; } = 10;

    // ── Circuit breaker ───────────────────────────────────────────────────
    public int CircuitFailureThreshold { get; set; } = 10;
    public int CircuitBreakDurationSeconds { get; set; } = 30;

    public DateTimeOffset UpdatedUtc { get; set; }

    public ICollection<IntegrationEndpoint> Endpoints { get; set; } = [];
    public ICollection<IntegrationMessageQueue> QueueMessages { get; set; } = [];
    public ICollection<IntegrationMessage> MessageHistory { get; set; } = [];
}
