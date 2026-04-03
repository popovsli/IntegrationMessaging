using IntegrationMessaging.Entities.Enums;

namespace IntegrationMessaging.Entities;

public class IntegrationMessage
{
    public int IntegrationMessageId { get; set; }
    public string IntegrationSystemCode { get; set; } = string.Empty;

    /// <summary>
    /// Shared with IntegrationMessageQueue.EntityId.
    /// Queried to verify a successful Create exists before processing Update/Delete.
    /// </summary>
    public int EntityId { get; set; }

    public MessageOperation Operation { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public int RetryCount { get; set; } = 0;
    public string Status { get; set; } = string.Empty;
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? Error { get; set; }
    public int? HttpStatusCode { get; set; }

    public IntegrationSystem? IntegrationSystem { get; set; }
}
