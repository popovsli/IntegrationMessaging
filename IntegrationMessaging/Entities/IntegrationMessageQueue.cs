using IntegrationMessaging.Entities.Enums;

namespace IntegrationMessaging.Entities;

public class IntegrationMessageQueue
{
    public int Id { get; set; }

    /// <summary>
    /// Domain entity identifier. Used to verify whether a successful Create
    /// was previously sent before processing Update or Delete operations.
    /// </summary>
    public int EntityId { get; set; }

    public string IntegrationSystemCode { get; set; } = string.Empty;
    public MessageOperation MessageOperation { get; set; } = MessageOperation.Update;
    public string Payload { get; set; } = string.Empty;
    public QueueMessageStatus Status { get; set; } = QueueMessageStatus.Queued;
    public int AttemptCount { get; set; } = 0;
    public DateTime CreationTime { get; set; } = DateTime.UtcNow;

    public Guid? WorkerStamp { get; set; }

    /// <summary>
    /// Worker exclusive lock. Another worker skips this row until LockedUntil expires.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    /// Earliest time the next retry should run.
    /// </summary>
    public DateTime? NextAttempt { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Fully-qualified message type. Used to resolve the handler and endpoint.
    /// </summary>
    public string MessageTypeName { get; set; } = string.Empty;

    public int? RequeuedFromMessageId { get; set; }
    public string RequeuedBy { get; set; } = string.Empty;
    public DateTime RequeuedAtUtc { get; set; }

    public IntegrationSystem? IntegrationSystem { get; set; }
}
