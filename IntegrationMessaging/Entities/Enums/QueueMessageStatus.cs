namespace IntegrationMessaging.Entities.Enums;

public enum QueueMessageStatus
{
    Queued,
    Processing,
    Sent,
    Failed,
    Skipped // ← NEW
}
