using IntegrationMessaging.Entities.Enums;
using IntegrationMessaging.Exceptions;
using IntegrationMessaging.Models;
using IntegrationMessaging.Services.Clients;
using IntegrationMessaging.Services.Handlers;
using System.Text.Json;
using System.Xml.Linq;

namespace IntegrationMessaging.Services.IntegrationClients.Handlers;

public sealed class WasteNotificationHandler(IEndpointResolver endpointResolver)
    : IIntegrationMessageHandler
{
    public static string MessageTypeName => HandlerKeys.WasteNotification;

    public async Task<IntegrationRequest> BuildRequestAsync(
        SendContext context, CancellationToken ct = default)
    {
        var q = context.QueueMessage;

        var resolution = await endpointResolver.ResolveAsync(
            q.IntegrationSystemCode, q.MessageTypeName, q.EntityId, ct);

        bool isSoap = context.System.ClientType
            .StartsWith("SOAP", StringComparison.OrdinalIgnoreCase);

        var payload = (q.MessageOperation, isSoap) switch
        {
            (MessageOperation.Create or MessageOperation.Update, false) => BuildJsonPayload(q.Payload),
            (MessageOperation.Delete, false) => BuildJsonDeletePayload(q.EntityId),
            (MessageOperation.Create or MessageOperation.Update, true) => BuildXmlPayload(q.Payload, q.EntityId),
            (MessageOperation.Delete, true) => BuildXmlDeletePayload(q.EntityId),
            _ => throw new IntegrationMessagingException(
                    $"Unhandled operation '{q.MessageOperation}' for {MessageTypeName}.")
        };

        return new IntegrationRequest
        {
            EndpointPath = resolution.ResolvedPath,
            HttpMethod = resolution.HttpMethod,
            SoapAction = resolution.SoapAction,
            Payload = payload
        };
    }

    private static string BuildJsonPayload(string rawPayload)
    {
        using var doc = JsonDocument.Parse(rawPayload);
        if (!doc.RootElement.TryGetProperty("VesselCallId", out _))
            throw new IntegrationMessagingException(
                $"{HandlerKeys.WasteNotification} payload missing 'VesselCallId'.");
        return rawPayload;
    }

    private static string BuildJsonDeletePayload(int entityId) =>
        JsonSerializer.Serialize(new { EntityId = entityId, IsDeleted = true });

    private static string BuildXmlPayload(string rawPayload, int entityId)
    {
        using var doc = JsonDocument.Parse(rawPayload);
        if (!doc.RootElement.TryGetProperty("VesselCallId", out var vesselCallId))
            throw new IntegrationMessagingException(
                $"{HandlerKeys.WasteNotification} payload missing 'VesselCallId'.");

        var xml = new XElement("SubmitWasteNotification",
            new XAttribute("xmlns", "http://tempuri.org/IWasteService"),
            new XElement("vesselCallId", vesselCallId.GetInt32()),
            new XElement("entityId", entityId),
            doc.RootElement.EnumerateObject()
               .Where(p => p.Name != "VesselCallId")
               .Select(p => new XElement(p.Name, p.Value.ToString()))
        );

        return xml.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildXmlDeletePayload(int entityId)
    {
        var xml = new XElement("CancelWasteNotification",
            new XAttribute("xmlns", "http://tempuri.org/IWasteService"),
            new XElement("entityId", entityId),
            new XElement("isDeleted", true),
            new XElement("cancelledAtUtc", DateTime.UtcNow.ToString("o")));
        return xml.ToString(SaveOptions.DisableFormatting);
    }
}
