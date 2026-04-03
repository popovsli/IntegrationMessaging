using IntegrationMessaging.Entities;

namespace IntegrationMessaging.Models;

public sealed class SendContext
{
    public required IntegrationMessageQueue QueueMessage { get; init; }
    public required IntegrationSystem System { get; init; }
    public IntegrationResponse? Response { get; set; }
}
