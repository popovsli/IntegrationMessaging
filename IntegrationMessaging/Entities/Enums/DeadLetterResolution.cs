// Entities/Enums/DeadLetterResolution.cs
namespace IntegrationMessaging.Entities.Enums;

public enum DeadLetterResolution
{
    Requeued,   // moved back to IntegrationMessageQueue
    Discarded   // operator decided to drop it
}