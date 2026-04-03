using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Handlers;
using System.Text.Json;

namespace IntegrationMessaging.Services.IntegrationClients.Handlers;

public sealed class PCSNotificationHandler(IEndpointResolver endpointResolver)
    : IIntegrationMessageHandler
{
    public static string MessageTypeName => HandlerKeys.PCSNotification;

    public async Task<IntegrationRequest> BuildRequestAsync(
        SendContext context, CancellationToken ct = default)
    {
        var q = context.QueueMessage;

        var resolution = await endpointResolver.ResolveAsync(
            q.IntegrationSystemCode, q.MessageTypeName, q.EntityId, ct);

        var payload = q.MessageOperation switch
        {
            MessageOperation.Create or
            MessageOperation.Update => ValidatePCSPayload(q.Payload),
            MessageOperation.Delete => JsonSerializer.Serialize(new
            {
                MessageId   = Guid.NewGuid().ToString(),
                MessageType = "CANCELLATION",
                VesselId    = q.EntityId,
                MessageTime = DateTime.UtcNow
            }),
            _ => throw new IntegrationMessagingException(
                    $"Unknown operation '{q.MessageOperation}' for {MessageTypeName}.")
        };

        return new IntegrationRequest
        {
            EndpointPath = resolution.ResolvedPath,
            HttpMethod   = resolution.HttpMethod,
            SoapAction   = resolution.SoapAction,
            Payload      = payload
        };
    }

    private static string ValidatePCSPayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("MessageId",   out _) ||
            !doc.RootElement.TryGetProperty("MessageType", out _))
            throw new IntegrationMessagingException(
                $"{HandlerKeys.PCSNotification} payload missing 'MessageId' or 'MessageType'.");
        return payload;
    }
}
