namespace IntegrationMessaging.Entities;

public class IntegrationSystem
{
    public string IntegrationSystemCode { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? PasswordSecret { get; set; }
    public int TokenSkewSeconds { get; set; } = 30;
    public string SystemName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string BaseAddress { get; set; } = string.Empty;
    public string EndpointPath { get; set; } = string.Empty;
    public string AuthEndpointPath { get; set; } = string.Empty;
    public int QueueMessageRetryDelaySeconds { get; set; } = 60;
    public int QueueMessageRetryCount { get; set; } = 10;
    public string ClientType { get; set; } = string.Empty;
    public string ContractType { get; set; } = string.Empty;
    public int ClientRetryCount { get; set; } = 5;
    public int ClientTimeoutSeconds { get; set; } = 10;
    public int CircuitFailureThreshold { get; set; } = 10;
    public int CircuitBreakDurationSeconds { get; set; } = 30;
    public string FormatPreference { get; set; } = "JSON";
    public string HeadersJson { get; set; } = "{}";
    public DateTimeOffset UpdatedUtc { get; set; }

    public ICollection<IntegrationEndpoint> Endpoints { get; set; } = [];
    public ICollection<IntegrationMessageQueue> QueueMessages { get; set; } = [];
    public ICollection<IntegrationMessage> MessageHistory { get; set; } = [];
    public string PasswordEncrypted { get; internal set; }
}
