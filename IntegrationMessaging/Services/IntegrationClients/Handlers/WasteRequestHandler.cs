using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Handlers;
using System.Text.Json;

namespace IntegrationMessaging.Services.IntegrationClients.Handlers;

public sealed class WasteRequestHandler(IEndpointResolver endpointResolver)
    : IIntegrationMessageHandler
{
    public static string MessageTypeName => HandlerKeys.WasteRequest;

    public async Task<IntegrationRequest> BuildRequestAsync(
        SendContext context, CancellationToken ct = default)
    {
        var q = context.QueueMessage;

        var resolution = await endpointResolver.ResolveAsync(
            q.IntegrationSystemCode, q.MessageTypeName, q.EntityId, ct);

        var payload = q.MessageOperation switch
        {
            MessageOperation.Create or
            MessageOperation.Update => EnsureRequiredFields(q.Payload, ["VesselCallId", "Sender"]),
            MessageOperation.Delete => JsonSerializer.Serialize(new { EntityId = q.EntityId }),
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

    private static string EnsureRequiredFields(string payload, string[] required)
    {
        using var doc = JsonDocument.Parse(payload);
        var missing = required.Where(f => !doc.RootElement.TryGetProperty(f, out _)).ToList();
        if (missing.Count > 0)
            throw new IntegrationMessagingException(
                $"{HandlerKeys.WasteRequest} payload missing fields: {string.Join(", ", missing)}.");
        return payload;
    }
}
