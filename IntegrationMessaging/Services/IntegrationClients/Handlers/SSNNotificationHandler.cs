using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Handlers;
using System.Text.Json;

namespace IntegrationMessaging.Services.IntegrationClients.Handlers;

public sealed class SSNNotificationHandler(IEndpointResolver endpointResolver)
    : IIntegrationMessageHandler
{
    public static string MessageTypeName => HandlerKeys.SSNNotification;

    public async Task<IntegrationRequest> BuildRequestAsync(
        SendContext context, CancellationToken ct = default)
    {
        var q = context.QueueMessage;

        var resolution = await endpointResolver.ResolveAsync(
            q.IntegrationSystemCode, q.MessageTypeName, q.EntityId, ct);

        var payload = q.MessageOperation switch
        {
            MessageOperation.Create or
            MessageOperation.Update => ValidateSSNPayload(q.Payload),
            MessageOperation.Delete => JsonSerializer.Serialize(new
            {
                EntityId       = q.EntityId,
                Cancelled      = true,
                CancelledAtUtc = DateTime.UtcNow
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

    private static string ValidateSSNPayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("VesselCallId", out _) ||
            !doc.RootElement.TryGetProperty("MSRefId",      out _))
            throw new IntegrationMessagingException(
                $"{HandlerKeys.SSNNotification} payload missing 'VesselCallId' or 'MSRefId'.");
        return payload;
    }
}
