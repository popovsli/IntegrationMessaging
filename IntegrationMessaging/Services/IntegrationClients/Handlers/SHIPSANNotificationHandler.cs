using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Handlers;
using System.Text.Json;

namespace IntegrationMessaging.Services.IntegrationClients.Handlers;

public sealed class SHIPSANNotificationHandler(IEndpointResolver endpointResolver)
    : IIntegrationMessageHandler
{
    public static string MessageTypeName => HandlerKeys.SHIPSANNotification;

    public async Task<IntegrationRequest> BuildRequestAsync(
        SendContext context, CancellationToken ct = default)
    {
        var q = context.QueueMessage;

        var resolution = await endpointResolver.ResolveAsync(
            q.IntegrationSystemCode, q.MessageTypeName, q.EntityId, ct);

        var payload = q.MessageOperation switch
        {
            MessageOperation.Create or
            MessageOperation.Update => ValidateSHIPSANPayload(q.Payload),
            MessageOperation.Delete => JsonSerializer.Serialize(new
            {
                PortCallId  = q.EntityId,
                MessageType = "CANCELLATION"
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

    private static string ValidateSHIPSANPayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("PortCallId", out _))
            throw new IntegrationMessagingException(
                $"{HandlerKeys.SHIPSANNotification} payload missing 'PortCallId'.");
        return payload;
    }
}
