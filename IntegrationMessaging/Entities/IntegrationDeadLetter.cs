// Entities/IntegrationDeadLetter.cs
using IntegrationMessaging.Entities.Enums;

namespace IntegrationMessaging.Entities;

public class IntegrationDeadLetter
{
    public int Id { get; set; }
    public int OriginalQueueId { get; set; }
    public int? IntegrationMessageId { get; set; }
    public string IntegrationSystemCode { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public string MessageTypeName { get; set; } = string.Empty;
    public string MessageOperation { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? LastError { get; set; }
    public int AttemptCount { get; set; }
    public DateTime DeadLetteredAtUtc { get; set; }

    // Filled when operator resolves (requeue or discard)
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolvedByUser { get; set; }
    public string? ResolutionNote { get; set; }
    public DeadLetterResolution? Resolution { get; set; }

    public IntegrationSystem? IntegrationSystem { get; set; }
}